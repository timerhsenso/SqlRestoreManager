namespace SqlRestoreManager.Services.Jobs;

/// <summary>
/// Consome a fila. Nenhuma exceção de job escapa daqui (não derruba o host).
/// </summary>
public sealed class RestoreWorker : BackgroundService
{
    private readonly RestoreQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RestoreWorker> _logger;

    public RestoreWorker(RestoreQueue queue, IServiceScopeFactory scopeFactory, ILogger<RestoreWorker> logger)
    {
        _queue = queue;
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
}
