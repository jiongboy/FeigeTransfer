using System.Security.Cryptography;
using Feige.Core.Crypto;

namespace Feige.Core.Protocol;

/// <summary>
/// What the confirmation UI needs: who the peer claims to be, the value to compare out-of-band, and the session
/// the approval would unlock (§3.8).
/// </summary>
/// <param name="Peer">The peer's validated offer.</param>
/// <param name="PeerFingerprint">Formatted SPKI fingerprint of the peer identity.</param>
/// <param name="Sas">Six-digit value both sides must display identically.</param>
/// <param name="Transcript">The transcript both signatures cover; bind any confirmation token to its digest.</param>
public sealed record HandshakePrompt(
    HandshakeOffer Peer,
    string PeerFingerprint,
    string Sas,
    HandshakeTranscript Transcript);

/// <summary>Result of a successful handshake: the derived record-layer keys and the evidence behind them.</summary>
/// <param name="Keys">Directional AES-GCM keys for this connection.</param>
/// <param name="Transcript">Signed transcript; its digest identifies this session.</param>
/// <param name="Peer">The peer's offer, already matched to the offer inside its proof.</param>
/// <param name="PeerFingerprint">Fingerprint the user confirmed.</param>
public sealed record HandshakeOutcome(
    SessionKeys Keys,
    HandshakeTranscript Transcript,
    HandshakeOffer Peer,
    string PeerFingerprint)
{
    /// <summary>The SAS the user saw; kept so the caller can log the session without logging keys.</summary>
    public string Sas => Transcript.Sas;
}
