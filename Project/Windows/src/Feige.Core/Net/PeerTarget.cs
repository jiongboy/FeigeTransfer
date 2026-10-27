using System.Net;

namespace Feige.Core.Net;

/// <summary>
/// Where to connect: a single TCP endpoint plus whatever the discovery layer already learned about it.
/// </summary>
/// <param name="EndPoint">Peer address and port; must not be a wildcard address.</param>
/// <param name="DeviceName">Announced name, when known, purely for UI.</param>
/// <param name="Fingerprint">
/// Formatted SPKI fingerprint from discovery, when known. Advisory only: the value the user confirms always comes
/// from the handshake (<see cref="Protocol.HandshakePrompt.PeerFingerprint"/>), never from discovery data.
/// </param>
public sealed record PeerTarget(IPEndPoint EndPoint, string? DeviceName = null, string? Fingerprint = null)
{
    /// <summary>Throws for a wildcard or IPv6-any endpoint, which cannot be dialed.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(EndPoint);

        if (EndPoint.Port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(EndPoint), EndPoint.Port, "Port must be 1..65535.");
        }

        if (EndPoint.Address.Equals(IPAddress.Any) || EndPoint.Address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("A wildcard address cannot be dialed; use a concrete peer address.", nameof(EndPoint));
        }
    }
}
