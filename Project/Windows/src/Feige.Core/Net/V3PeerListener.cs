using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;

namespace Feige.Core.Net;

/// <summary>V3 listener. A rejected or broken handshake affects only the accepted connection.</summary>
public sealed class V3PeerListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private bool _started, _disposed;

    public V3PeerListener(IPEndPoint? bindTo = null)
    {
        _listener = new TcpListener(bindTo ?? new IPEndPoint(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        if (_listener.Server.AddressFamily == AddressFamily.InterNetworkV6) _listener.Server.DualMode = true;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _listener.Start(); _started = true;
    }

    public Task<V3Connection> AcceptAsync(LocalDevice local, Func<string, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        return AcceptAsync(() => local, approve, cancellationToken);
    }

    /// <summary>Reads the current identity/name after TCP accept so a pending wait does not capture an old name.</summary>
    public async Task<V3Connection> AcceptAsync(Func<LocalDevice> localProvider, Func<string, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localProvider);
        ArgumentNullException.ThrowIfNull(approve);
        Start();
        var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            client.NoDelay = true;
            V3PeerConnector.RequireLocal((IPEndPoint)client.Client.RemoteEndPoint!);
            var local = localProvider();
            return await V3Connection.EstablishAsync(client.GetStream(), local.Identity, false, local.DeviceName,
                approve, cancellationToken).ConfigureAwait(false);
        }
        catch { client.Dispose(); throw; }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; _listener.Stop(); }
        return ValueTask.CompletedTask;
    }
}
