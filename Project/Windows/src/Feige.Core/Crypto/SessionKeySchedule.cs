using System.Security.Cryptography;
using System.Text;
using Feige.Core.Protocol;

namespace Feige.Core.Crypto;

/// <summary>
/// Turns the X25519 shared secret into directional record keys and nonce prefixes (通信协议 §3.9).
/// </summary>
public static class SessionKeySchedule
{
    /// <summary>Length of a transcript digest, a session key and a shared secret, in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>Length of a nonce prefix: 4 digest bytes plus the 4-byte direction tag.</summary>
    public const int NoncePrefixSize = 8;

    /// <summary>
    /// Nonce length used on the wire: 8-byte prefix plus an 8-byte big-endian sequence number.
    /// </summary>
    /// <remarks>
    /// This is 16 bytes, not the 12 bytes a standard AES-GCM nonce would use. The macOS reference
    /// implementation builds the same 16-byte value, so Windows must match it; that also means
    /// <c>System.Security.Cryptography.AesGcm</c> cannot be used here (it rejects nonces that are not
    /// 12 bytes), and the record layer has to go through BouncyCastle's <c>GcmBlockCipher</c>.
    /// </remarks>
    public const int NonceSize = NoncePrefixSize + sizeof(ulong);

    private static readonly byte[] InitiatorToResponderInfo = Encoding.UTF8.GetBytes("feige-v1 initiator-to-responder");
    private static readonly byte[] ResponderToInitiatorInfo = Encoding.UTF8.GetBytes("feige-v1 responder-to-initiator");
    private static readonly byte[] InitiatorTag = Encoding.ASCII.GetBytes("INIT");
    private static readonly byte[] ResponderTag = Encoding.ASCII.GetBytes("RESP");

    /// <summary>
    /// Derives <c>i2r</c>/<c>r2i</c> with HKDF-SHA256 (salt = transcript digest) and selects the direction
    /// matching <paramref name="localRole"/>.
    /// </summary>
    /// <param name="sharedSecret">32-byte X25519 shared secret.</param>
    /// <param name="transcriptDigest">32-byte SHA-256 of the transcript (§3.5).</param>
    /// <param name="localRole">Role of this side, which decides which key is used for sending.</param>
    public static SessionKeys Derive(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> transcriptDigest,
        HandshakeRole localRole)
    {
        ValidateSize(sharedSecret, nameof(sharedSecret), "shared secret");
        ValidateSize(transcriptDigest, nameof(transcriptDigest), "transcript digest");

        if (localRole is not (HandshakeRole.Initiator or HandshakeRole.Responder))
        {
            throw new ArgumentOutOfRangeException(nameof(localRole), localRole, "Unknown handshake role.");
        }

        if (X25519KeyAgreement.IsAllZero(sharedSecret))
        {
            throw new CryptographicException("Shared secret is all zeros; aborting key derivation.");
        }

        byte[] salt = transcriptDigest.ToArray();
        byte[] ikm = sharedSecret.ToArray();
        byte[] initiatorToResponder = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySize, salt, InitiatorToResponderInfo);
        byte[] responderToInitiator = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySize, salt, ResponderToInitiatorInfo);

        bool isInitiator = localRole == HandshakeRole.Initiator;
        byte[] sendKey = isInitiator ? initiatorToResponder : responderToInitiator;
        byte[] receiveKey = isInitiator ? responderToInitiator : initiatorToResponder;
        byte[] sendPrefix = BuildNoncePrefix(transcriptDigest, isInitiator ? InitiatorTag : ResponderTag);
        byte[] receivePrefix = BuildNoncePrefix(transcriptDigest, isInitiator ? ResponderTag : InitiatorTag);

        return new SessionKeys(sendKey, receiveKey, sendPrefix, receivePrefix);
    }

    /// <summary>
    /// Returns the record nonce: 8-byte prefix followed by the big-endian 64-bit sequence number (§4.1).
    /// </summary>
    public static byte[] BuildNonce(ReadOnlySpan<byte> prefix, ulong sequence)
    {
        if (prefix.Length != NoncePrefixSize)
        {
            throw new ArgumentException($"Nonce prefix must be {NoncePrefixSize} bytes, got {prefix.Length}.", nameof(prefix));
        }

        byte[] nonce = new byte[NonceSize];
        prefix.CopyTo(nonce);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(NoncePrefixSize), sequence);
        return nonce;
    }

    private static byte[] BuildNoncePrefix(ReadOnlySpan<byte> transcriptDigest, byte[] directionTag)
    {
        byte[] prefix = new byte[NoncePrefixSize];
        transcriptDigest[..4].CopyTo(prefix);
        directionTag.CopyTo(prefix.AsSpan(4));
        return prefix;
    }

    private static void ValidateSize(ReadOnlySpan<byte> value, string parameterName, string label)
    {
        if (value.Length != KeySize)
        {
            throw new ArgumentException($"The {label} must be exactly {KeySize} bytes, got {value.Length}.", parameterName);
        }
    }
}
