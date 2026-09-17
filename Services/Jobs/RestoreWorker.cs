using Microsoft.EntityFrameworkCore;
using SqlRestoreManager.Data;
using SqlRestoreManager.Models;
using SqlRestoreManager.Services.Notifications;
using SqlRestoreManager.Services.Uploads;

namespace SqlRestoreManager.Services.Jobs;

/// <summary>
/// Consome a fila. Nenhuma exceção de job escapa daqui (não derruba o host).
/// </summary>
public sealed class RestoreWorker : BackgroundService
{
    private readonly RestoreQueue _queue;
    private readonly RestoreJobRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RestoreWorker> _logger;

    public RestoreWorker(
        RestoreQueue queue,
        RestoreJobRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<RestoreWorker> logger)
    {
        _queue = queue;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker de restore iniciado.");

        try
        {
            await foreach (var job in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    if (_registry.IsCancelRequested(job.JobId))
                    {
                        _logger.LogInformation("Job {JobId} cancelado enquanto estava na fila.", job.JobId);
                        await MarkCanceledAsync(scope.ServiceProvider, job);
                        continue;
                    }

                    var processor = scope.ServiceProvider.GetRequiredService<RestoreJobProcessor>();
                    await processor.ProcessAsync(job, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro não tratado no job {JobId}.", job.JobId);
                }
                finally
                {
                    _queue.Release(job.TargetDatabase, job.JobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }

        _logger.LogInformation("Worker de restore finalizado.");
    }

    private async Task MarkCanceledAsync(IServiceProvider services, RestoreJob job)
    {
        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            var history = await db.RestoreHistory.SingleOrDefaultAsync(x => x.JobId == job.JobId);
            if (history is not null)
            {
                history.Status = RestoreStatus.Canceled;
                history.CurrentStep = RestoreStatus.ToLabel(RestoreStatus.Canceled);
                history.ErrorMessage = "Cancelado antes de iniciar. O banco de destino não foi alterado.";
                history.FinishedAt = DateTime.Now;
                await db.SaveChangesAsync();
            }

            var notifier = services.GetRequiredService<RestoreNotifier>();
            await notifier.FailedAsync(job.JobId, "Restore cancelado.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao registrar cancelamento do job {JobId}.", job.JobId);
        }
        finally
        {
            TempFolder.TryDelete(job.JobFolder, _logger);
        }
    }
}
