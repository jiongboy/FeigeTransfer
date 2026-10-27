using System.Security.Cryptography;
using Feige.Core.Crypto;

namespace Feige.Core.Protocol;

/// <summary>
/// Drives the four-message handshake of 通信协议 §3.2 for one connection: offer → offer → proof → proof,
/// then signature verification, then local user confirmation, then key derivation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order matters:</b> the local offer is written <i>before</i> reading the peer's. Waiting to receive first would
/// deadlock two peers that both wait.
/// </para>
/// <para>
/// No session key exists until the local user approves: a rejected prompt throws
/// <see cref="ProtocolError.UserRejected"/> and the caller must close the connection. Approval is per side —
/// this side approving says nothing about the peer, which approves independently.
/// </para>
/// <para>
/// The ephemeral X25519 private key lives only in this object and is zeroed on dispose; it is never part of the offer,
/// never logged, and never reused for another connection.
/// </para>
/// </remarks>
public sealed class LocalHandshake : IDisposable
{
    private readonly IdentityKey _identity;
    private readonly byte[] _ephemeralPrivateKey;
    private bool _disposed;

    private LocalHandshake(IdentityKey identity, HandshakeOffer offer, byte[] ephemeralPrivateKey)
    {
        _identity = identity;
        Offer = offer;
        _ephemeralPrivateKey = ephemeralPrivateKey;
    }

    /// <summary>This side's offer, already containing the ephemeral public key and a fresh nonce.</summary>
    public HandshakeOffer Offer { get; }

    /// <summary>
    /// Builds a local offer with a fresh X25519 key pair, a fresh 32-byte nonce and either the given pairing code
    /// or a random one.
    /// </summary>
    public static LocalHandshake Create(IdentityKey identity, HandshakeRole role, string deviceName, string? sessionCode = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        byte[] ephemeralPrivateKey = X25519KeyAgreement.CreatePrivateKey();
        byte[] ephemeralPublicKey = X25519KeyAgreement.GetPublicKey(ephemeralPrivateKey);
        byte[] nonce = RandomNumberGenerator.GetBytes(HandshakeOffer.NonceSize);
        string code = sessionCode ?? HandshakeOffer.CreateSessionCode();

        try
        {
            var offer = new HandshakeOffer(
                HandshakeOffer.CurrentVersion,
                role,
                deviceName,
                identity.SubjectPublicKeyInfo,
                ephemeralPublicKey,
                nonce,
                code);

            return new LocalHandshake(identity, offer, ephemeralPrivateKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
            throw;
        }
    }

    /// <summary>
    /// Runs the handshake against <paramref name="duplex"/>. <paramref name="approve"/> is invoked only after the
    /// peer's signature verified, and must return true to let key derivation proceed.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.PairingCodeMismatch"/>, <see cref="ProtocolError.InvalidTranscript"/>,
    /// <see cref="ProtocolError.SignatureFailed"/>, <see cref="ProtocolError.InvalidOffer"/> for a small-order
    /// ephemeral key, or <see cref="ProtocolError.UserRejected"/> when the local user declines.
    /// </exception>
    public async Task<HandshakeOutcome> RunAsync(
        IHandshakeDuplex duplex,
        Func<HandshakePrompt, bool> approve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duplex);
        ArgumentNullException.ThrowIfNull(approve);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Offer out, offer in. Both sides write first (§3.2).
        await duplex.WriteAsync(HandshakeMessages.EncodeOfferFrame(Offer), cancellationToken).ConfigureAwait(false);
        HandshakeOffer peer = HandshakeMessages.DecodeOffer(await duplex.ReadAsync(cancellationToken).ConfigureAwait(false));

        // 2. Cross-check the offers; this is where version/role/self-identity/pairing-code failures surface.
        HandshakeTranscript transcript = HandshakeTranscript.Create(Offer, peer);
        byte[] transcriptBytes = transcript.Encode();

        // 3. Proof out, proof in.
        byte[] signature = _identity.Sign(transcriptBytes);
        await duplex.WriteAsync(HandshakeMessages.EncodeProofFrame(Offer, signature), cancellationToken).ConfigureAwait(false);
        HandshakeProof proof = HandshakeMessages.DecodeProof(await duplex.ReadAsync(cancellationToken).ConfigureAwait(false));

        // 4. The proof must echo the same offer: never trust a key that reappears only in the proof (§3.7).
        if (!proof.Offer.Equals(peer))
        {
            throw new ProtocolException(ProtocolError.InvalidTranscript);
        }

        IdentityKey.VerifyOrThrow(proof.Offer.IdentityPublicKeyDer, transcriptBytes, proof.Signature);

        // 5. Only now is the peer's identity worth showing to a human.
        string peerFingerprint = DeviceFingerprint.Compute(peer.IdentityPublicKeyDer);
        var prompt = new HandshakePrompt(peer, peerFingerprint, transcript.Sas, transcript);
        if (!approve(prompt))
        {
            throw new ProtocolException(ProtocolError.UserRejected);
        }

        // 6. Key agreement, then HKDF with the transcript digest as salt (§3.9).
        byte[] sharedSecret;
        try
        {
            sharedSecret = X25519KeyAgreement.Agree(_ephemeralPrivateKey, peer.EphemeralPublicKey);
        }
        catch (CryptographicException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer, ex);
        }

        try
        {
            SessionKeys keys = SessionKeySchedule.Derive(sharedSecret, transcript.Digest.Span, Offer.Role);
            return new HandshakeOutcome(keys, transcript, peer, peerFingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_ephemeralPrivateKey);
    }
}
