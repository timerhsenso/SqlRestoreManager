using Microsoft.AspNetCore.SignalR;

namespace SqlRestoreManager.Services.Notifications;

/// <summary>
/// Cada cliente entra somente no grupo do job que iniciou/acompanha.
/// (Autorização do hub entra na Fase 2.)
/// </summary>
public sealed class RestoreHub : Hub
{
    public Task JoinJob(string jobId)
    {
        if (!Guid.TryParse(jobId, out var id))
            throw new HubException("JobId inválido.");

        return Groups.AddToGroupAsync(Context.ConnectionId, RestoreNotifier.GroupName(id));
    }

    public Task JoinBackup(string runId)
    {
        if (!Guid.TryParse(runId, out var id))
            throw new HubException("RunId inválido.");

        return Groups.AddToGroupAsync(Context.ConnectionId, Services.Backup.BackupNotifier.GroupName(id));
    }
}
