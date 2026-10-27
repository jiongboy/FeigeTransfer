namespace Feige.Core.Conversations;

/// <summary>
/// Bounded text receipt window. Keep one instance across reconnections and seed it from received history.
/// A message enters the window only after the application's recording callback succeeds.
/// </summary>
public sealed class V3TextInbox
{
    private readonly int _capacity;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HashSet<(string Peer, Guid Id)> _received = [];
    private readonly Queue<(string Peer, Guid Id)> _order = [];

    public V3TextInbox(int capacity = 1000, IEnumerable<(string Peer, Guid Id)>? received = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        if (received is not null)
            foreach (var item in received) Remember((Normalize(item.Peer), item.Id));
    }

    internal async Task RecordAsync(string peer, V3Text message, Func<V3Text, Task> record, CancellationToken ct)
    {
        // Network cancellation stops waiting, but an in-flight durable write still owns the deduplication gate.
        // If it succeeds late, remember it before a reconnection can record the same UUID again.
        var recording = RecordCoreAsync(peer, message, record, ct);
        try { await recording.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            _ = recording.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }

    private async Task RecordCoreAsync(string peer, V3Text message, Func<V3Text, Task> record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = (Normalize(peer), message.Id);
            if (_received.Contains(key)) return;
            await record(message).ConfigureAwait(false);
            Remember(key);
        }
        finally { _gate.Release(); }
    }

    private void Remember((string Peer, Guid Id) key)
    {
        if (!_received.Add(key)) return;
        _order.Enqueue(key);
        while (_order.Count > _capacity) _received.Remove(_order.Dequeue());
    }

    private static string Normalize(string fingerprint) => fingerprint.Replace(" ", "").ToUpperInvariant();
}
