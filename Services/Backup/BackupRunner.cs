using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Models;
using SqlRestoreManager.Services.Jobs;
using SqlRestoreManager.Services.Notifications;
using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Services.Backup;

/// <summary>
/// Orquestra uma execução de backup: uma por vez, nunca junto de um restore.
/// </summary>
public sealed class BackupRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RestoreQueue _restoreQueue;
    private readonly BackupOptions _options;
    private readonly RestoreOptions _restoreOptions;
    private readonly ILogger<BackupRunner> _logger;

    private volatile bool _running;

    public BackupRunner(
        IServiceScopeFactory scopeFactory,
        RestoreQueue restoreQueue,
        IOptions<BackupOptions> options,
        IOptions<RestoreOptions> restoreOptions,
        ILogger<BackupRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _restoreQueue = restoreQueue;
        _options = options.Value;
        _restoreOptions = restoreOptions.Value;
        _logger = logger;
    }

    public bool IsRunning => _running;

    public Guid? CurrentRunId { get; private set; }

    /// <summary>Backup e restore nunca rodam ao mesmo tempo.</summary>
    public bool RestoreInProgress => _restoreQueue.HasActiveJobs;

    /// <summary>
    /// Inicia a execução em segundo plano e devolve o RunId.
    /// </summary>
    public async Task<Guid> StartAsync(IReadOnlyList<string> databases, string trigger, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new RestoreValidationException("O módulo de backup está desabilitado (Backup:Enabled).");

        if (RestoreInProgress)
            throw new RestoreValidationException(
                "Há um restore em andamento. Aguarde o término para iniciar o backup.",
                StatusCodes.Status409Conflict);

        if (!await _gate.WaitAsync(0, ct))
            throw new RestoreValidationException(
                "Já existe um backup em andamento.", StatusCodes.Status409Conflict);

        var runId = Guid.NewGuid();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<BackupService>();

            var targets = (await service.ListTargetsAsync(ct))
                .Where(t => databases.Count == 0 ||
                            databases.Any(d => string.Equals(d, t.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.Name)
                .ToList();

            if (targets.Count == 0)
                throw new RestoreValidationException("Nenhum banco selecionado para backup.");

            db.BackupRuns.Add(new BackupRun
            {
                RunId = runId,
                Trigger = trigger,
                Status = BackupStatus.Running,
                CurrentStep = "Na fila...",
                TotalDatabases = targets.Count,
                NodeName = _restoreOptions.EffectiveNodeName,
                StartedAt = DateTime.Now
            });
            await db.SaveChangesAsync(ct);

            _running = true;
            CurrentRunId = runId;

            // Continua em segundo plano: a requisição HTTP não espera o backup terminar.
            _ = Task.Run(() => ExecuteAsync(runId, targets), CancellationToken.None);
            return runId;
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task ExecuteAsync(Guid runId, IReadOnlyList<string> databases)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<BackupService>();
        var logs = scope.ServiceProvider.GetRequiredService<LogMaintenance>();
        var notifier = scope.ServiceProvider.GetRequiredService<BackupNotifier>();
        var progress = scope.ServiceProvider.GetRequiredService<SqlRestoreService>();

        var run = await db.BackupRuns.SingleAsync(x => x.RunId == runId);

        try
        {
            for (var i = 0; i < databases.Count; i++)
            {
                var database = databases[i];
                var basePercent = (int)(i * 100d / databases.Count);

                var item = new BackupItem
                {
                    RunId = runId,
                    DatabaseName = database,
                    Status = BackupStatus.Running,
                    StartedAt = DateTime.Now
                };
                db.BackupItems.Add(item);

                run.CurrentStep = $"({i + 1}/{databases.Count}) {database}";
                run.PercentComplete = basePercent;
                await db.SaveChangesAsync();
                await notifier.ProgressAsync(runId, run.Status, run.CurrentStep, basePercent);

                try
                {
                    await service.ResolveTargetAsync(database);

                    if (_options.ShrinkLogBefore)
                    {
                        await notifier.ProgressAsync(runId, run.Status,
                            $"({i + 1}/{databases.Count}) {database}: reduzindo log...", basePercent);

                        var shrink = await logs.ShrinkAsync(database, _options.LogTargetSizeMB,
                            _options.LogGrowthMB, setRecoverySimple: true, _options.CommandTimeoutMinutes * 60);

                        item.LogSizeBeforeMB = shrink.BeforeMB;
                        item.LogSizeAfterMB = shrink.AfterMB;
                        await db.SaveChangesAsync();
                    }

                    var sessionId = new StrongBox<int>(0);
                    var task = service.BackupAsync(database, spid => Volatile.Write(ref sessionId.Value, spid));

                    await TrackProgressAsync(task, sessionId, progress, notifier, runId, run, database,
                        i, databases.Count, db);

                    var file = await task;
                    item.FilePath = file.Path;
                    item.SizeBytes = file.SizeBytes;
                    item.Status = BackupStatus.Success;
                    item.FinishedAt = DateTime.Now;

                    run.SucceededCount++;
                    run.TotalSizeBytes += file.SizeBytes;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha no backup de {Database}.", database);
                    item.Status = BackupStatus.Error;
                    item.ErrorMessage = Truncate(SqlErrorTranslator.Translate(ex), 4000);
                    item.FinishedAt = DateTime.Now;
                    run.FailedCount++;
                }

                await db.SaveChangesAsync();
            }

            try
            {
                await service.CleanupAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao aplicar a retenção dos backups.");
            }

            run.Status = run.FailedCount == 0 ? BackupStatus.Success : BackupStatus.Warning;
            run.CurrentStep = run.FailedCount == 0
                ? "Backup concluído."
                : $"Concluído com {run.FailedCount} falha(s).";
            run.PercentComplete = 100;
            run.FinishedAt = DateTime.Now;
            await db.SaveChangesAsync();

            await notifier.CompletedAsync(runId, run.CurrentStep!, run.FailedCount > 0);
            _logger.LogInformation("Backup {RunId} finalizado: {Ok} ok, {Fail} falha(s).",
                runId, run.SucceededCount, run.FailedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha geral na execução de backup {RunId}.", runId);
            run.Status = BackupStatus.Error;
            run.ErrorMessage = Truncate(SqlErrorTranslator.Translate(ex), 4000);
            run.FinishedAt = DateTime.Now;

            try { await db.SaveChangesAsync(); } catch { /* já registrado no log */ }
            await notifier.FailedAsync(runId, run.ErrorMessage ?? "Falha no backup.");
        }
        finally
        {
            _running = false;
            CurrentRunId = null;
            _gate.Release();
        }
    }

    private async Task TrackProgressAsync(
        Task task, StrongBox<int> sessionId, SqlRestoreService progress, BackupNotifier notifier,
        Guid runId, BackupRun run, string database, int index, int total, AppDbContext db)
    {
        var slice = 100d / total;
        var lastPercent = -1;

        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));

            var spid = Volatile.Read(ref sessionId.Value);
            if (task.IsCompleted || spid == 0)
                continue;

            try
            {
                var percent = await progress.GetRestorePercentAsync(spid);
                if (percent is null)
                    continue;

                var mapped = (int)(index * slice + percent.Value * slice / 100d);
                if (mapped <= lastPercent)
                    continue;

                lastPercent = mapped;
                run.PercentComplete = Math.Clamp(mapped, 0, 99);
                run.CurrentStep = $"({index + 1}/{total}) {database}... {percent.Value:0}%";
                await db.SaveChangesAsync();
                await notifier.ProgressAsync(runId, run.Status, run.CurrentStep, run.PercentComplete);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao consultar progresso do backup; a operação continua.");
            }
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
