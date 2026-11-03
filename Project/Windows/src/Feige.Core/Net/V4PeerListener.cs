using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;

namespace Feige.Core.Net;

/// <summary>Accepts local-network sockets and performs only the authenticated v4 handshake.</summary>
public sealed class V4PeerListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private bool _started;
    private bool _disposed;

    public V4PeerListener(IPEndPoint? bindTo = null)
    {
        IPEndPoint endpoint = bindTo ?? new IPEndPoint(
            Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        _listener = new TcpListener(endpoint);
        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            _listener.Server.DualMode = true;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;
    public int Port => LocalEndPoint.Port;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _listener.Start();
        _started = true;
    }

    public Task<V4Connection> AcceptAsync(LocalDevice local,
        Func<string, CancellationToken, Task<bool>> approve, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        return AcceptAsync(() => local, approve, cancellationToken);
    }

    public Task<V4Connection> AcceptAsync(Func<LocalDevice> localProvider,
        Func<string, CancellationToken, Task<bool>> approve, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approve);
        return AcceptAsync(localProvider, (name, _, token) => approve(name, token), cancellationToken);
    }

    /// <summary>Obtains the current local identity/name after accept and prompts once for this connection only.
    /// The approval receives the peer name and its authenticated fingerprint.</summary>
    public async Task<V4Connection> AcceptAsync(Func<LocalDevice> localProvider,
        Func<string, string, CancellationToken, Task<bool>> approve, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localProvider);
        ArgumentNullException.ThrowIfNull(approve);
        Start();

        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            client.NoDelay = true;
            V4PeerConnector.RequireLocal((IPEndPoint)client.Client.RemoteEndPoint!);
            LocalDevice local = localProvider()
                ?? throw new InvalidOperationException("本机设备信息不可用。");
            return await V4Connection.EstablishAsync(client.GetStream(), local.Identity, initiator: false,
                local.DeviceName, approve, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
