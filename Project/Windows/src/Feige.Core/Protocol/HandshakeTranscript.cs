using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Feige.Core.Crypto;

namespace Feige.Core.Protocol;

/// <summary>
/// The canonical signed byte string for one handshake (通信协议 §3.5), plus the values derived from it:
/// the SHA-256 digest, the six-digit SAS and the peer fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is always <b>initiator first, responder second</b>, regardless of which side created the object,
/// so both peers sign and hash identical bytes. The digest is computed once: it is the HKDF salt for the session
/// keys, the nonce-prefix source, and the value a confirmation token is bound to.
/// </para>
/// <para>
/// Creating a transcript is also the offer cross-check of §3.4 — version, opposite roles, distinct identities
/// and an equal <c>sessionCode</c> are all enforced here, before any signature is produced.
/// </para>
/// </remarks>
public sealed class HandshakeTranscript
{
    /// <summary>Domain separator including the trailing NUL, byte-for-byte <c>"FEIGE-HANDSHAKE-V1\0"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FEIGE-HANDSHAKE-V1\0"u8;

    private readonly byte[] _digest;
    private byte[]? _encoded;

    private HandshakeTranscript(HandshakeOffer initiator, HandshakeOffer responder)
    {
        Initiator = initiator;
        Responder = responder;
        _digest = SHA256.HashData(Encode());
    }

    /// <summary>The offer of the side that initiated the connection.</summary>
    public HandshakeOffer Initiator { get; }

    /// <summary>The offer of the side that responded.</summary>
    public HandshakeOffer Responder { get; }

    /// <summary>SHA-256 of <see cref="Encode"/>; used as HKDF salt and nonce-prefix source.</summary>
    public ReadOnlyMemory<byte> Digest => _digest;

    /// <summary>Six-digit SAS both sides must display identically (§3.10).</summary>
    public string Sas
    {
        get
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(_digest);
            return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Validates the offer pair (§3.4) and orders them by role.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidTranscript"/> when versions, roles or identities do not line up;
    /// <see cref="ProtocolError.PairingCodeMismatch"/> when the six-digit codes differ.
    /// </exception>
    public static HandshakeTranscript Create(HandshakeOffer local, HandshakeOffer peer)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(peer);

        if (local.Version != HandshakeOffer.CurrentVersion || peer.Version != HandshakeOffer.CurrentVersion)
        {
            throw new ProtocolException(ProtocolError.InvalidTranscript);
        }

        if (local.Role == peer.Role)
        {
            throw new ProtocolException(ProtocolError.InvalidTranscript);
        }

        // Pairing with ourselves would make the SAS check meaningless: both sides would be the same key.
        if (local.IdentityPublicKeyDer.SequenceEqual(peer.IdentityPublicKeyDer))
        {
            throw new ProtocolException(ProtocolError.InvalidTranscript);
        }

        if (!string.Equals(local.SessionCode, peer.SessionCode, StringComparison.Ordinal))
        {
            throw new ProtocolException(ProtocolError.PairingCodeMismatch);
        }

        return local.Role == HandshakeRole.Initiator
            ? new HandshakeTranscript(local, peer)
            : new HandshakeTranscript(peer, local);
    }

    /// <summary>
    /// <c>"FEIGE-HANDSHAKE-V1" || 0x00 || offer(initiator) || offer(responder)</c> — the exact bytes that get
    /// signed (§3.6) and hashed.
    /// </summary>
    public byte[] Encode()
    {
        if (_encoded is not null)
        {
            return _encoded;
        }

        byte[] initiator = EncodeOffer(Initiator);
        byte[] responder = EncodeOffer(Responder);

        byte[] buffer = new byte[Magic.Length + initiator.Length + responder.Length];
        Magic.CopyTo(buffer);
        initiator.CopyTo(buffer, Magic.Length);
        responder.CopyTo(buffer, Magic.Length + initiator.Length);

        _encoded = buffer;
        return buffer;
    }

    /// <summary>The offer that belongs to <paramref name="localRole"/>.</summary>
    public HandshakeOffer LocalOffer(HandshakeRole localRole)
        => localRole == HandshakeRole.Initiator ? Initiator : Responder;

    /// <summary>The offer of the other side, from <paramref name="localRole"/>'s point of view.</summary>
    public HandshakeOffer PeerOffer(HandshakeRole localRole)
        => localRole == HandshakeRole.Initiator ? Responder : Initiator;

    /// <summary>Formatted fingerprint of the peer's identity key (§3.6).</summary>
    public string PeerFingerprint(HandshakeRole localRole)
        => DeviceFingerprint.Compute(PeerOffer(localRole).IdentityPublicKeyDer);

    /// <summary>
    /// Encodes one offer for the transcript: all integers big-endian, each variable field preceded by a
    /// <c>u32</c> byte length that excludes itself (§3.5).
    /// </summary>
    public static byte[] EncodeOffer(HandshakeOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        byte[] deviceName = offer.EncodeDeviceName();
        ReadOnlySpan<byte> identity = offer.IdentityPublicKeyDer;
        ReadOnlySpan<byte> ephemeral = offer.EphemeralPublicKey;
        ReadOnlySpan<byte> nonce = offer.Nonce;
        byte[] sessionCode = System.Text.Encoding.ASCII.GetBytes(offer.SessionCode);

        int size = sizeof(ushort) + sizeof(byte)
                   + 4 + deviceName.Length
                   + 4 + identity.Length
                   + 4 + ephemeral.Length
                   + 4 + nonce.Length
                   + 4 + sessionCode.Length;

        byte[] buffer = new byte[size];
        Span<byte> cursor = buffer;

        BinaryPrimitives.WriteUInt16BigEndian(cursor, offer.Version);
        cursor = cursor[sizeof(ushort)..];
        cursor[0] = (byte)offer.Role;
        cursor = cursor[1..];

        cursor = WriteLengthPrefixed(cursor, deviceName);
        cursor = WriteLengthPrefixed(cursor, identity);
        cursor = WriteLengthPrefixed(cursor, ephemeral);
        cursor = WriteLengthPrefixed(cursor, nonce);
        _ = WriteLengthPrefixed(cursor, sessionCode);

        return buffer;
    }

    private static Span<byte> WriteLengthPrefixed(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value.Length);
        value.CopyTo(destination[4..]);
        return destination[(4 + value.Length)..];
    }
}
