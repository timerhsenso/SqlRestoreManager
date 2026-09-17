using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Models;
using SqlRestoreManager.Services.Notifications;
using SqlRestoreManager.Services.Sql;
using SqlRestoreManager.Services.Uploads;

namespace SqlRestoreManager.Services.Jobs;

/// <summary>
/// Executa um job: preparar → validar → restaurar, persistindo e notificando cada etapa.
/// </summary>
public sealed class RestoreJobProcessor
{
    private const int ErrorMessageMaxLength = 4000;
    private const int StepMaxLength = 200;
    private const int RestoreStartPercent = 12;

    private readonly AppDbContext _db;
    private readonly SqlRestoreService _sql;
    private readonly BackupPathMapper _pathMapper;
    private readonly RestoreNotifier _notifier;
    private readonly RestoreOptions _options;
    private readonly ILogger<RestoreJobProcessor> _logger;

    public RestoreJobProcessor(
        AppDbContext db,
        SqlRestoreService sql,
        BackupPathMapper pathMapper,
        RestoreNotifier notifier,
        IOptions<RestoreOptions> options,
        ILogger<RestoreJobProcessor> logger)
    {
        _db = db;
        _sql = sql;
        _pathMapper = pathMapper;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(RestoreJob job, CancellationToken stoppingToken)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = job.JobId,
            ["TargetDatabase"] = job.TargetDatabase
        });

        var history = await _db.RestoreHistory.SingleOrDefaultAsync(x => x.JobId == job.JobId, stoppingToken);
        if (history is null)
        {
            _logger.LogWarning("Histórico do job não encontrado; job descartado.");
            TempFolder.TryDelete(job.JobFolder, _logger);
            return;
        }

        try
        {
            await StepAsync(history, RestoreStatus.Preparing, "Preparando arquivo de backup...", 3, stoppingToken);
            var localBackup = await Task.Run(() => _sql.PrepareBackup(job.UploadedPath, job.JobFolder), stoppingToken);
            var sqlBackup = _pathMapper.ToSqlServerPath(localBackup);

            await StepAsync(history, RestoreStatus.Validating, "Validando backup no SQL Server...", 8, stoppingToken);
            var backup = await _sql.InspectBackupAsync(sqlBackup, stoppingToken);

            history.SourceDatabase = Truncate(backup.SourceDatabase, 128);
            history.BackupPosition = backup.Position;
            history.BackupFinishDate = backup.BackupFinishDate;
            history.BackupServerName = Truncate(backup.ServerName, 128);

            await StepAsync(history, RestoreStatus.Restoring,
                $"Encerrando conexões e restaurando '{backup.SourceDatabase}'...",
                RestoreStartPercent, stoppingToken);

            var result = await RunRestoreWithProgressAsync(
                history, new RestoreRequest(job.TargetDatabase, sqlBackup, backup), stoppingToken);

            history.DisconnectedSessions = result.DisconnectedSessions;
            history.Status = RestoreStatus.Success;
            history.PercentComplete = 100;
            history.CurrentStep = "Restore concluído.";
            history.ErrorMessage = null;
            history.FinishedAt = DateTime.Now;
            await _db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation(
                "Restore concluído. Origem {SourceDatabase}, {Sessions} sessões encerradas.",
                backup.SourceDatabase, result.DisconnectedSessions);

            await _notifier.CompletedAsync(job.JobId,
                $"Restore concluído: '{backup.SourceDatabase}' → '{job.TargetDatabase}'.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Aplicação encerrada antes do início do restore.");
            await EndWithFailureAsync(history, RestoreStatus.Interrupted,
                "A aplicação foi encerrada antes do início do restore. Envie o arquivo novamente.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no job de restore.");
            await EndWithFailureAsync(history, RestoreStatus.Error, SqlErrorTranslator.Translate(ex));
        }
        finally
        {
            TempFolder.TryDelete(job.JobFolder, _logger);
        }
    }

    private async Task<RestoreResult> RunRestoreWithProgressAsync(
        RestoreHistory history, RestoreRequest request, CancellationToken stoppingToken)
    {
        var sessionId = new StrongBox<int>(0);

        using var stoppingRegistration = stoppingToken.Register(() =>
            _logger.LogWarning(
                "Encerramento solicitado com RESTORE em andamento. Configure o App Pool do IIS " +
                "(idleTimeout=0, sem reciclagem periódica) para evitar interrupções."));

        var restoreTask = _sql.RestoreAsync(
            request,
            spid => Volatile.Write(ref sessionId.Value, spid),
            stoppingToken);

        var interval = TimeSpan.FromSeconds(_options.ProgressPollSeconds);
        var lastPercent = RestoreStartPercent;

        while (!restoreTask.IsCompleted)
        {
            await Task.WhenAny(restoreTask, Task.Delay(interval, CancellationToken.None));

            var spid = Volatile.Read(ref sessionId.Value);
            if (restoreTask.IsCompleted || spid == 0)
                continue;

            try
            {
                var percent = await _sql.GetRestorePercentAsync(spid, CancellationToken.None);
                if (percent is null)
                    continue;

                var mapped = Math.Clamp(RestoreStartPercent + (int)(percent.Value * 0.87), RestoreStartPercent, 99);
                if (mapped <= lastPercent)
                    continue;

                lastPercent = mapped;
                history.PercentComplete = mapped;
                history.CurrentStep = $"Restaurando... {percent.Value:0}%";
                await _db.SaveChangesAsync(CancellationToken.None);
                await _notifier.ProgressAsync(history.JobId, history.Status, history.CurrentStep, mapped);
            }
            catch (Exception ex)
            {
                // Falha de monitoramento NÃO interrompe o restore.
                _logger.LogWarning(ex, "Falha ao consultar/persistir progresso; o restore continua.");
            }
        }

        return await restoreTask;
    }

    private async Task StepAsync(
        RestoreHistory history, string status, string message, int percent, CancellationToken ct)
    {
        history.Status = status;
        history.CurrentStep = Truncate(message, StepMaxLength);
        history.PercentComplete = percent;
        await _db.SaveChangesAsync(ct);
        await _notifier.ProgressAsync(history.JobId, status, message, percent);
    }

    private async Task EndWithFailureAsync(RestoreHistory history, string status, string message)
    {
        try
        {
            history.Status = status;
            history.CurrentStep = Truncate(RestoreStatus.ToLabel(status), StepMaxLength);
            history.ErrorMessage = Truncate(message, ErrorMessageMaxLength);
            history.FinishedAt = DateTime.Now;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível gravar a falha no histórico.");
        }

        await _notifier.FailedAsync(history.JobId, message);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
