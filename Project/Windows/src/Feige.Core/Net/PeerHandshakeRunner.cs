using System.Net.Sockets;
using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>
/// Shared tail of <see cref="PeerConnector"/> and <see cref="PeerListener"/>: run the four-message handshake on an
/// already-connected socket and hand back a session whose record channel continues exactly where the handshake stopped.
/// </summary>
internal static class PeerHandshakeRunner
{
    /// <summary>
    /// Completes the handshake as <paramref name="role"/> and takes ownership of <paramref name="client"/>.
    /// On any failure the socket is closed and the exception propagates.
    /// </summary>
    internal static async Task<PeerSession> CompleteAsync(
        TcpClient client,
        LocalDevice local,
        HandshakeRole role,
        Func<HandshakePrompt, bool> approve,
        string? sessionCode,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        NetworkStream network = client.GetStream();
        TimeoutStream? recordStream = null;

        try
        {
            HandshakeOutcome outcome;
            byte[] remainder;

            // The handshake wrapper must not close the socket: the record layer keeps using it afterwards.
            using (var handshakeStream = new TimeoutStream(network, options.HandshakeTimeout, leaveOpen: true))
            {
                var duplex = new StreamHandshakeDuplex(handshakeStream);
                using LocalHandshake handshake = LocalHandshake.Create(local.Identity, role, local.DeviceName, sessionCode);
                outcome = await handshake.RunAsync(duplex, approve, cancellationToken).ConfigureAwait(false);

                // §3.1: the peer's first record may already sit in the handshake reader's buffer.
                remainder = duplex.TakeRemainder();
            }

            recordStream = new TimeoutStream(network, options.IdleTimeout);
            var channel = new StreamRecordChannel(recordStream, outcome.Keys, remainder);
            return new PeerSession(client, recordStream, channel, outcome);
        }
        catch
        {
            // NetworkStream disposal closes the socket; TcpClient is then just belt and braces.
            recordStream?.Dispose();
            network.Dispose();
            client.Dispose();
            throw;
        }
    }
}
