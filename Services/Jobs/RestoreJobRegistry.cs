using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace SqlRestoreManager.Services.Jobs;

/// <summary>
/// Jobs em execução nesta instância e pedidos de cancelamento.
/// Guarda o SPID para poder encerrar a sessão do SQL Server quando o restore já começou.
/// </summary>
public sealed class RestoreJobRegistry
{
    private sealed record Entry(CancellationTokenSource Cancellation, StrongBox<int> SessionId);

    private readonly ConcurrentDictionary<Guid, Entry> _running = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancelRequests = new();

    /// <summary>Registra o job em execução; o token vale enquanto o job existir.</summary>
    public CancellationTokenSource Register(Guid jobId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[jobId] = new Entry(cts, new StrongBox<int>(0));

        // Cancelamento pedido enquanto o job ainda estava na fila.
        if (_cancelRequests.ContainsKey(jobId))
            cts.Cancel();

        return cts;
    }

    public void Unregister(Guid jobId)
    {
        if (_running.TryRemove(jobId, out var entry))
            entry.Cancellation.Dispose();

        _cancelRequests.TryRemove(jobId, out _);
    }

    /// <summary>Guarda o SPID da operação em andamento (verify, backup ou restore).</summary>
    public void SetSessionId(Guid jobId, int sessionId)
    {
        if (_running.TryGetValue(jobId, out var entry))
            Volatile.Write(ref entry.SessionId.Value, sessionId);
    }

    public bool IsCancelRequested(Guid jobId) => _cancelRequests.ContainsKey(jobId);

    /// <summary>
    /// Marca o cancelamento e devolve o SPID a encerrar (0 = nada em execução no SQL Server).
    /// </summary>
    public (bool Known, int SessionId) RequestCancel(Guid jobId)
    {
        _cancelRequests[jobId] = 1;

        if (!_running.TryGetValue(jobId, out var entry))
            return (false, 0);

        entry.Cancellation.Cancel();
        return (true, Volatile.Read(ref entry.SessionId.Value));
    }
}
