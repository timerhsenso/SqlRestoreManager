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
    private const int VerifyStartPercent = 8;
    private const int SafetyStartPercent = 16;
    private const int RestoreStartPercent = 28;

    private readonly AppDbContext _db;
    private readonly SqlRestoreService _sql;
    private readonly PostRestoreService _postRestore;
    private readonly BackupExtractor _extractor;
    private readonly RestoreJobRegistry _registry;
    private readonly BackupPathMapper _pathMapper;
    private readonly RestoreNotifier _notifier;
    private readonly RestoreOptions _options;
    private readonly ILogger<RestoreJobProcessor> _logger;

    public RestoreJobProcessor(
        AppDbContext db,
        SqlRestoreService sql,
        PostRestoreService postRestore,
        BackupExtractor extractor,
        RestoreJobRegistry registry,
        BackupPathMapper pathMapper,
        RestoreNotifier notifier,
        IOptions<RestoreOptions> options,
        ILogger<RestoreJobProcessor> logger)
    {
        _db = db;
        _sql = sql;
        _postRestore = postRestore;
        _extractor = extractor;
        _registry = registry;
        _pathMapper = pathMapper;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(RestoreJob job, CancellationToken hostToken)
    {
        using var jobCancellation = _registry.Register(job.JobId, hostToken);
        try
        {
            await ExecuteAsync(job, jobCancellation.Token, hostToken);
        }
        finally
        {
            _registry.Unregister(job.JobId);
        }
    }

    private async Task ExecuteAsync(RestoreJob job, CancellationToken stoppingToken, CancellationToken hostToken)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = job.JobId,
            ["TargetDatabase"] = job.TargetDatabase
        });

        var history = await _db.RestoreHistory
            .SingleOrDefaultAsync(x => x.JobId == job.JobId, CancellationToken.None);
        if (history is null)
        {
            _logger.LogWarning("Histórico do job não encontrado; job descartado.");
            TempFolder.TryDelete(job.JobFolder, _logger);
            return;
        }

        try
        {
            await StepAsync(history, RestoreStatus.Preparing, "Preparando arquivo de backup...", 3, stoppingToken);
            var localBackup = await _extractor.PrepareAsync(job.SourcePath, job.JobFolder, stoppingToken);
            var sqlBackup = _pathMapper.ToSqlServerPath(localBackup);

            await StepAsync(history, RestoreStatus.Validating, "Validando backup no SQL Server...", 8, stoppingToken);
            var backup = await _sql.InspectBackupAsync(sqlBackup, stoppingToken);

            history.SourceDatabase = Truncate(backup.SourceDatabase, 128);
            history.BackupPosition = backup.Position;
            history.BackupFinishDate = backup.BackupFinishDate;
            history.BackupServerName = Truncate(backup.ServerName, 128);

            if (_options.PreRestore.VerifyBackup)
            {
                await StepAsync(history, RestoreStatus.Verifying, "Verificando integridade do backup...",
                    VerifyStartPercent, stoppingToken);

                await RunWithProgressAsync(history, "Verificando backup", VerifyStartPercent, SafetyStartPercent,
                    onSpid => _sql.VerifyBackupAsync(
                        sqlBackup, backup.Position, backup.HasChecksums, onSpid, stoppingToken));
            }

            await CreateSafetyBackupAsync(history, job.TargetDatabase, stoppingToken);

            await StepAsync(history, RestoreStatus.Restoring,
                $"Encerrando conexões e restaurando '{backup.SourceDatabase}'...",
                RestoreStartPercent, stoppingToken);

            var result = await RunWithProgressAsync(history, "Restaurando", RestoreStartPercent, 99,
                onSpid => _sql.RestoreAsync(
                    new RestoreRequest(job.TargetDatabase, sqlBackup, backup), onSpid, stoppingToken));

            history.DisconnectedSessions = result.DisconnectedSessions;
            _logger.LogInformation(
                "Restore concluído. Origem {SourceDatabase}, {Sessions} sessões encerradas.",
                backup.SourceDatabase, result.DisconnectedSessions);

            // O banco já está restaurado: a partir daqui nada é cancelado nem vira "Error".
            var post = await RunPostRestoreAsync(history, job.TargetDatabase);
            var hasWarnings = post?.HasWarnings == true;

            history.Status = hasWarnings ? RestoreStatus.Warning : RestoreStatus.Success;
            history.PercentComplete = 100;
            history.CurrentStep = hasWarnings ? "Restore concluído com alertas no pós-restore." : "Restore concluído.";
            history.ErrorMessage = null;
            history.FinishedAt = DateTime.Now;
            await _db.SaveChangesAsync(CancellationToken.None);

            var message = $"Restore concluído: '{backup.SourceDatabase}' → '{job.TargetDatabase}'.";
            if (post?.LogSizeBeforeMB is { } before && post.LogSizeAfterMB is { } after && before != after)
                message += $" Log: {before:N0} MB → {after:N0} MB.";
            if (hasWarnings)
                message += " Há alertas no pós-restore: veja o Histórico.";

            await _notifier.CompletedAsync(job.JobId, message, hasWarnings);
        }
        catch (Exception ex) when (_registry.IsCancelRequested(job.JobId))
        {
            _logger.LogWarning(ex, "Job cancelado pelo usuário.");
            await EndWithFailureAsync(history, RestoreStatus.Canceled, CancelMessage(history.Status));
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
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

    /// <summary>Sobrecarga para operações sem retorno (ex.: VERIFYONLY).</summary>
    private Task RunWithProgressAsync(
        RestoreHistory history, string label, int fromPercent, int toPercent, Func<Action<int>, Task> start) =>
        RunWithProgressAsync<bool>(history, label, fromPercent, toPercent, async onSpid =>
        {
            await start(onSpid);
            return true;
        });

    /// <summary>
    /// Executa uma operação longa do SQL Server (verify, backup ou restore) acompanhando
    /// percent_complete pelo SPID. Falha de monitoramento nunca interrompe a operação.
    /// </summary>
    private async Task<T> RunWithProgressAsync<T>(
        RestoreHistory history, string label, int fromPercent, int toPercent, Func<Action<int>, Task<T>> start)
    {
        var sessionId = new StrongBox<int>(0);
        var task = start(spid =>
        {
            Volatile.Write(ref sessionId.Value, spid);
            _registry.SetSessionId(history.JobId, spid);
        });

        var interval = TimeSpan.FromSeconds(_options.ProgressPollSeconds);
        var range = toPercent - fromPercent;
        var lastPercent = fromPercent;

        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(interval, CancellationToken.None));

            var spid = Volatile.Read(ref sessionId.Value);
            if (task.IsCompleted || spid == 0)
                continue;

            try
            {
                var percent = await _sql.GetRestorePercentAsync(spid, CancellationToken.None);
                if (percent is null)
                    continue;

                var mapped = Math.Clamp(fromPercent + (int)(percent.Value * range / 100d), fromPercent, toPercent);
                if (mapped <= lastPercent)
                    continue;

                lastPercent = mapped;
                history.PercentComplete = mapped;
                history.CurrentStep = $"{label}... {percent.Value:0}%";
                await _db.SaveChangesAsync(CancellationToken.None);
                await _notifier.ProgressAsync(history.JobId, history.Status, history.CurrentStep, mapped);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao consultar/persistir progresso; a operação continua.");
            }
        }

        return await task;
    }

    /// <summary>Backup COPY_ONLY do banco atual antes de sobrescrevê-lo.</summary>
    private async Task CreateSafetyBackupAsync(RestoreHistory history, string database, CancellationToken ct)
    {
        var options = _options.PreRestore;
        if (!options.SafetyBackup || string.IsNullOrWhiteSpace(options.SafetyBackupPath))
            return;

        if (!await _sql.DatabaseExistsAsync(database, ct))
        {
            _logger.LogInformation("Banco {Database} ainda não existe: backup de segurança ignorado.", database);
            return;
        }

        await StepAsync(history, RestoreStatus.SafetyBackup,
            "Gerando backup de segurança do banco atual...", SafetyStartPercent, ct);

        var path = await RunWithProgressAsync(history, "Backup de segurança", SafetyStartPercent, RestoreStartPercent,
            onSpid => _sql.CreateSafetyBackupAsync(database, options.SafetyBackupPath!, onSpid, ct));

        history.SafetyBackupPath = Truncate(path, 512);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Backup de segurança gerado em {Path}.", path);

        try
        {
            await _sql.CleanupSafetyBackupsAsync(options.SafetyBackupPath!, options.SafetyBackupRetentionDays, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao limpar backups de segurança antigos.");
        }
    }

    private async Task<PostRestoreResult?> RunPostRestoreAsync(RestoreHistory history, string database)
    {
        if (!_options.PostRestore.Enabled)
            return null;

        try
        {
            await StepAsync(history, RestoreStatus.PostRestoring, "Executando ajustes pós-restore...", 99,
                CancellationToken.None);

            var result = await _postRestore.RunAsync(database, message =>
                _notifier.ProgressAsync(history.JobId, RestoreStatus.PostRestoring, message, 99));

            history.LogSizeBeforeMB = result.LogSizeBeforeMB;
            history.LogSizeAfterMB = result.LogSizeAfterMB;
            history.PostRestoreLog = result.Summary;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha inesperada no pós-restore.");
            history.PostRestoreLog = $"[ALERTA] Pós-restore: {ex.Message}";
            return new PostRestoreResult(
                [new PostRestoreStep("Pós-restore", false, ex.Message)], null, null);
        }
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

    private static string CancelMessage(string status) => status switch
    {
        RestoreStatus.Restoring =>
            "Restore cancelado durante a gravação. O banco de destino ficou em estado RESTORING e " +
            "precisa de um novo restore para voltar a ficar utilizável.",
        RestoreStatus.PostRestoring =>
            "Cancelado durante o pós-restore. O banco foi restaurado, mas os ajustes podem não ter sido concluídos.",
        _ => "Restore cancelado antes de alterar o banco de destino."
    };

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
