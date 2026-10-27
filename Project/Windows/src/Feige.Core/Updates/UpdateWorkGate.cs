namespace Feige.Core.Updates;

/// <summary>Atomically admits business work or enters installation. Idle connections do not count as work.</summary>
public sealed class UpdateWorkGate
{
    private readonly object _gate = new();
    private int _active;
    private bool _installing;
    public bool IsInstalling { get { lock (_gate) return _installing; } }
    public bool HasActiveWork { get { lock (_gate) return _active != 0; } }
    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (_installing) return null;
            _active++;
            return new Lease(this);
        }
    }
    public bool TryBeginInstall()
    {
        lock (_gate)
        {
            if (_installing || _active != 0) return false;
            _installing = true;
            return true;
        }
    }
    public void CancelInstall() { lock (_gate) _installing = false; }
    private sealed class Lease(UpdateWorkGate owner) : IDisposable
    {
        private UpdateWorkGate? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } gate) lock (gate._gate) gate._active--;
        }
    }
}
