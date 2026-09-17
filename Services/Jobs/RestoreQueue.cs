using System.Collections.Concurrent;
using System.Threading.Channels;
using SqlRestoreManager.Models;

namespace SqlRestoreManager.Services.Jobs;

/// <summary>
/// Fila em memória + reserva por banco (impede dois jobs para o mesmo destino nesta instância).
/// Entre instâncias (dev x IIS) a proteção é o sp_getapplock no SqlRestoreService.
/// </summary>
public sealed class RestoreQueue
{
    private readonly Channel<RestoreJob> _channel = Channel.CreateUnbounded<RestoreJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ConcurrentDictionary<string, Guid> _reservations =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryReserve(string database, Guid jobId) => _reservations.TryAdd(database, jobId);

    public void Release(string database, Guid jobId) =>
        _reservations.TryRemove(new KeyValuePair<string, Guid>(database, jobId));

    public bool IsJobActive(Guid jobId) => _reservations.Values.Contains(jobId);

    public ValueTask EnqueueAsync(RestoreJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<RestoreJob> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
