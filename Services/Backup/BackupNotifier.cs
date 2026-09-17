using Microsoft.AspNetCore.SignalR;
using SqlRestoreManager.Services.Notifications;

namespace SqlRestoreManager.Services.Backup;

public sealed record BackupProgressMessage(Guid RunId, string Status, string Message, int Percent);

public sealed record BackupResultMessage(Guid RunId, string Message, bool HasWarnings = false);

/// <summary>Eventos da execução de backup, no grupo do RunId.</summary>
public sealed class BackupNotifier
{
    private readonly IHubContext<RestoreHub> _hub;
    private readonly ILogger<BackupNotifier> _logger;

    public BackupNotifier(IHubContext<RestoreHub> hub, ILogger<BackupNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public static string GroupName(Guid runId) => $"backup:{runId:N}";

    public Task ProgressAsync(Guid runId, string status, string message, int percent) =>
        SendAsync(runId, "BackupProgress", new BackupProgressMessage(runId, status, message, percent));

    public Task CompletedAsync(Guid runId, string message, bool hasWarnings) =>
        SendAsync(runId, "BackupCompleted", new BackupResultMessage(runId, message, hasWarnings));

    public Task FailedAsync(Guid runId, string message) =>
        SendAsync(runId, "BackupFailed", new BackupResultMessage(runId, message));

    private async Task SendAsync(Guid runId, string method, object payload)
    {
        try
        {
            await _hub.Clients.Group(GroupName(runId)).SendAsync(method, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enviar {Method} do backup {RunId}.", method, runId);
        }
    }
}
