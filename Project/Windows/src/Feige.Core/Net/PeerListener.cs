using System.Net;
using System.Net.Sockets;
using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>
/// Listens for inbound peers and runs the handshake as <c>responder</c> (通信协议 §3.2).
/// </summary>
/// <remarks>
/// <para>
/// Binds <see cref="IPAddress.IPv6Any"/> in dual-stack mode by default so one socket serves both address families —
/// the discovery layer may hand the dialer either an A or an AAAA address.
/// </para>
/// <para>
/// A failed inbound handshake aborts only that connection: the listener keeps accepting, so callers loop over
/// <see cref="AcceptAsync"/> and swallow <see cref="ProtocolException"/> per iteration. Do not treat one bad peer as a
/// reason to stop listening.
/// </para>
/// </remarks>
public sealed class PeerListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a listener; not started until <see cref="Start"/>.</summary>
    /// <param name="bindTo">
    /// Address and port to bind; defaults to dual-stack any on an ephemeral port. Pass a concrete port when the value is
    /// going to be advertised, or 0 to let the OS pick one and read it back from <see cref="Port"/>.
    /// </param>
    public PeerListener(IPEndPoint? bindTo = null)
    {
        IPEndPoint bind = bindTo ?? new IPEndPoint(IPAddress.IPv6Any, 0);
        _listener = new TcpListener(bind);

        if (bind.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Dual-stack: IPv4 peers arrive as ::ffff:a.b.c.d, which the connector handles transparently.
            _listener.Server.DualMode = true;
        }
    }

    /// <summary>The bound endpoint; only meaningful after <see cref="Start"/> (the port may be OS-assigned).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>The port peers should dial; available once started.</summary>
    public int Port => LocalEndPoint.Port;

    /// <summary>Starts listening. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _listener.Start();
        _started = true;
    }

    /// <summary>
    /// Waits for one inbound connection and completes the handshake on it.
    /// </summary>
    /// <param name="local">This device's identity and display name.</param>
    /// <param name="sessionCode">
    /// Six-digit pairing code this listener is willing to accept; generate one with
    /// <see cref="HandshakeOffer.CreateSessionCode"/> and show it to the local user. Both sides must use the same value
    /// (§3.4); a mismatch surfaces as <see cref="ProtocolError.PairingCodeMismatch"/> from both ends.
    /// </param>
    /// <param name="approve">
    /// Called once with the peer's validated offer, fingerprint and SAS; runs on a worker thread while the socket waits.
    /// Returning false rejects with <see cref="ProtocolError.UserRejected"/> and closes that connection only.
    /// </param>
    /// <param name="options">Timeouts; defaults to <see cref="ConnectionOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops waiting for a connection and cancels the handshake.</param>
    /// <exception cref="ProtocolException">The handshake failed; the connection is closed and the listener still works.</exception>
    public async ValueTask<PeerSession> AcceptAsync(
        LocalDevice local,
        string sessionCode,
        Func<HandshakePrompt, bool> approve,
        ConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(approve);

        if (!HandshakeOffer.IsValidSessionCode(sessionCode))
        {
            throw new ArgumentException("Session code must be exactly six ASCII digits.", nameof(sessionCode));
        }

        ConnectionOptions policies = options ?? ConnectionOptions.Default;
        policies.Validate();

        Start();

        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            client.NoDelay = true;
            return await PeerHandshakeRunner
                .CompleteAsync(client, local, HandshakeRole.Responder, approve, sessionCode, policies, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
