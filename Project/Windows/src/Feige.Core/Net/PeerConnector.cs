using System.Net.Sockets;
using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>
/// Dials a peer and runs the handshake as <c>initiator</c> (通信协议 §3.2), returning a session ready for one transfer.
/// </summary>
/// <remarks>
/// The initiator role only fixes key direction and transcript order; after the handshake this side may just as well
/// receive as send (§1).
/// </remarks>
public static class PeerConnector
{
    /// <summary>
    /// Connects to <paramref name="target"/> and completes the handshake.
    /// </summary>
    /// <param name="target">Peer address and port.</param>
    /// <param name="local">This device's identity and display name.</param>
    /// <param name="approve">
    /// Called once with the peer's validated offer, fingerprint and SAS. Runs on a worker thread while the socket waits,
    /// so it may block on a UI dialog but must not touch UI objects directly. Returning false aborts with
    /// <see cref="ProtocolError.UserRejected"/>.
    /// </param>
    /// <param name="sessionCode">
    /// Six-digit pairing code the local user obtained out of band — normally read off the responder's screen. Both
    /// sides must use the same value (§3.4); a mismatch surfaces as
    /// <see cref="ProtocolError.PairingCodeMismatch"/> on both ends.
    /// </param>
    /// <param name="options">Timeouts; defaults to <see cref="ConnectionOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels connect and handshake.</param>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.Timeout"/> when the connect or a handshake read/write times out, or any handshake error.
    /// </exception>
    public static async Task<PeerSession> ConnectAsync(
        PeerTarget target,
        LocalDevice local,
        string sessionCode,
        Func<HandshakePrompt, bool> approve,
        ConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(approve);
        target.Validate();

        // Never let a null/typo'd code fall through to "each side invents its own": that is an unexplainable
        // PairingCodeMismatch a few milliseconds later.
        if (!HandshakeOffer.IsValidSessionCode(sessionCode))
        {
            throw new ArgumentException("Session code must be exactly six ASCII digits.", nameof(sessionCode));
        }

        ConnectionOptions policies = options ?? ConnectionOptions.Default;
        policies.Validate();

        var client = new TcpClient(target.EndPoint.AddressFamily) { NoDelay = true };

        // Do NOT pass a CancellationToken into ConnectAsync: on Windows a refused connect then surfaces as
        // OperationCanceledException and the real SocketException is lost, which turns "refused" into "timeout" in the
        // UI. Waiting with a timeout keeps the diagnostic; disposing the client aborts a pending connect.
        Task connectTask = client.ConnectAsync(target.EndPoint);

        try
        {
            await connectTask.WaitAsync(policies.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            client.Dispose();

            // The abandoned connect will fault once the socket is gone; observe it so it never surfaces process-wide.
            _ = connectTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new ProtocolException(ProtocolError.Timeout, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return await PeerHandshakeRunner
            .CompleteAsync(client, local, HandshakeRole.Initiator, approve, sessionCode, policies, cancellationToken)
            .ConfigureAwait(false);
    }
}
