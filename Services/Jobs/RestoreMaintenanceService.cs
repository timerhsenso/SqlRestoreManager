using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Models;
using SqlRestoreManager.Services.Uploads;

namespace SqlRestoreManager.Services.Jobs;

/// <summary>
/// 1) No startup: marca como Interrupted os jobs desta instância que ficaram ativos
///    por reciclagem/queda (a fila é em memória).
/// 2) Periodicamente: remove pastas temporárias órfãs.
/// </summary>
public sealed class RestoreMaintenanceService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RestoreQueue _queue;
    private readonly RestoreOptions _options;
    private readonly ILogger<RestoreMaintenanceService> _logger;
    private readonly DateTime _processStartedAt;

    public RestoreMaintenanceService(
        IServiceScopeFactory scopeFactory,
        RestoreQueue queue,
        IOptions<RestoreOptions> options,
        ILogger<RestoreMaintenanceService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
        using var process = Process.GetCurrentProcess();
        _processStartedAt = process.StartTime; // hora local, mesmo padrão de StartedAt
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            EnsureTempFolder();
            await ReconcileInterruptedJobsAsync(stoppingToken);

            using var timer = new PeriodicTimer(CleanupInterval);
            do
            {
                CleanupTempFolders();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no serviço de manutenção.");
        }
    }

    private void EnsureTempFolder()
    {
        try
        {
            Directory.CreateDirectory(_options.TempPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível criar/acessar Restore:TempPath ({TempPath}).", _options.TempPath);
        }
    }

    private async Task ReconcileInterruptedJobsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var node = _options.EffectiveNodeName;

            var stale = await db.RestoreHistory
                .Where(x => RestoreStatus.Active.Contains(x.Status)
                            && x.NodeName == node
                            && x.StartedAt < _processStartedAt)
                .ToListAsync(ct);

            if (stale.Count == 0)
                return;

            foreach (var item in stale)
            {
                item.ErrorMessage = item.Status switch
                {
                    RestoreStatus.Restoring =>
                        "A aplicação foi reiniciada durante o restore. O banco de destino pode ter ficado em " +
                        "estado RESTORING; execute um novo restore.",
                    RestoreStatus.PostRestoring =>
                        "A aplicação foi reiniciada durante o pós-restore. O banco foi restaurado, mas os " +
                        "ajustes (recovery, log, usuários, scripts) podem não ter sido concluídos.",
                    _ => "A aplicação foi reiniciada antes da execução do restore. Envie o arquivo novamente."
                };
                item.Status = RestoreStatus.Interrupted;
                item.CurrentStep = RestoreStatus.ToLabel(RestoreStatus.Interrupted);
                item.FinishedAt = DateTime.Now;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogWarning("{Count} job(s) interrompido(s) marcados na reconciliação do node {Node}.", stale.Count, node);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Não foi possível reconciliar o histórico. Verifique a HistoryConnection e se o script " +
                "Database/001_RestoreHistory.sql foi executado.");
        }
    }

    private void CleanupTempFolders()
    {
        try
        {
            if (!Directory.Exists(_options.TempPath))
                return;

            var limit = DateTime.UtcNow.AddHours(-_options.TempRetentionHours);

            foreach (var dir in Directory.EnumerateDirectories(_options.TempPath))
            {
                // Só mexe em pastas criadas por esta aplicação (Guid "N").
                if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var jobId))
                    continue;

                if (_queue.IsJobActive(jobId) || Directory.GetLastWriteTimeUtc(dir) > limit)
                    continue;

                if (TempFolder.TryDelete(dir, _logger))
                    _logger.LogInformation("Pasta temporária órfã removida: {Folder}.", dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha na limpeza de pastas temporárias.");
        }
    }
}
