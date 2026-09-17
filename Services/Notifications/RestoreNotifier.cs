using Microsoft.AspNetCore.SignalR;

namespace SqlRestoreManager.Services.Notifications;

public sealed record RestoreProgressMessage(Guid JobId, string Status, string Message, int Percent);

public sealed record RestoreResultMessage(Guid JobId, string Message, bool HasWarnings = false);

/// <summary>
/// Envia eventos SignalR apenas para o grupo do job. Falha de envio nunca afeta o restore.
/// </summary>
public sealed class RestoreNotifier
{
    private readonly IHubContext<RestoreHub> _hub;
    private readonly ILogger<RestoreNotifier> _logger;

    public RestoreNotifier(IHubContext<RestoreHub> hub, ILogger<RestoreNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public static string GroupName(Guid jobId) => $"job:{jobId:N}";

    public Task ProgressAsync(Guid jobId, string status, string message, int percent) =>
        SendAsync(jobId, "RestoreProgress", new RestoreProgressMessage(jobId, status, message, percent));

    public Task CompletedAsync(Guid jobId, string message, bool hasWarnings = false) =>
        SendAsync(jobId, "RestoreCompleted", new RestoreResultMessage(jobId, message, hasWarnings));

    public Task FailedAsync(Guid jobId, string message) =>
        SendAsync(jobId, "RestoreFailed", new RestoreResultMessage(jobId, message));

    private async Task SendAsync(Guid jobId, string method, object payload)
    {
        try
        {
            await _hub.Clients.Group(GroupName(jobId)).SendAsync(method, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enviar {Method} do job {JobId}.", method, jobId);
        }
    }
}
