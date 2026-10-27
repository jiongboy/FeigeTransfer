namespace Feige.Core.Protocol;

/// <summary>
/// A peer's <c>proof</c> message: the offer it already sent plus its RSA-PSS signature over the transcript (§3.7).
/// </summary>
/// <param name="Offer">The echoed offer, which must equal the one received earlier.</param>
/// <param name="Signature">Raw RSA-PSS signature bytes (384 for a 3072-bit key).</param>
public sealed record HandshakeProof(HandshakeOffer Offer, byte[] Signature);
