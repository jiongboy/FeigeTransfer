using System.Security.Cryptography;
using System.Text;

namespace Feige.Core.Protocol;

/// <summary>
/// One side's <c>offer</c> payload (通信协议 §3.3). Values are validated at construction, so an instance
/// that exists is already within the protocol's limits — decoding peer input is where
/// <see cref="ProtocolError.InvalidOffer"/> is raised.
/// </summary>
/// <remarks>
/// Equality is structural across the byte fields, not reference equality, because §3.7 requires the
/// <c>offer</c> echoed inside a <c>proof</c> to be compared field-by-field with the one received earlier.
/// </remarks>
public sealed class HandshakeOffer : IEquatable<HandshakeOffer>
{
    /// <summary>The only protocol version this build speaks; there is no downgrade path (§3.4).</summary>
    public const ushort CurrentVersion = 1;

    /// <summary>Maximum UTF-8 length of <c>deviceName</c>.</summary>
    public const int MaxDeviceNameBytes = 128;

    /// <summary>Maximum length of the SPKI DER identity key.</summary>
    public const int MaxIdentityKeySize = 1024;

    /// <summary>Required length of <c>ephemeralPublicKey</c> (raw X25519 public key).</summary>
    public const int EphemeralKeySize = 32;

    /// <summary>Required length of <c>nonce</c>.</summary>
    public const int NonceSize = 32;

    /// <summary>Required number of ASCII digits in <c>sessionCode</c>.</summary>
    public const int SessionCodeLength = 6;

    private readonly byte[] _identityPublicKeyDer;
    private readonly byte[] _ephemeralPublicKey;
    private readonly byte[] _nonce;

    /// <summary>Creates a validated offer.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidOffer"/> when a field is empty, oversized or malformed.
    /// </exception>
    public HandshakeOffer(
        ushort version,
        HandshakeRole role,
        string deviceName,
        ReadOnlySpan<byte> identityPublicKeyDer,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> nonce,
        string sessionCode)
    {
        if (version != CurrentVersion)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        if (role is not (HandshakeRole.Initiator or HandshakeRole.Responder))
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        if (string.IsNullOrEmpty(deviceName) || Encoding.UTF8.GetByteCount(deviceName) > MaxDeviceNameBytes)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        if (identityPublicKeyDer.IsEmpty || identityPublicKeyDer.Length > MaxIdentityKeySize)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        if (ephemeralPublicKey.Length != EphemeralKeySize || nonce.Length != NonceSize)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        if (!IsValidSessionCode(sessionCode))
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        Version = version;
        Role = role;
        DeviceName = deviceName;
        _identityPublicKeyDer = identityPublicKeyDer.ToArray();
        _ephemeralPublicKey = ephemeralPublicKey.ToArray();
        _nonce = nonce.ToArray();
        SessionCode = sessionCode;
    }

    /// <summary>Always <see cref="CurrentVersion"/> for a valid offer.</summary>
    public ushort Version { get; }

    /// <summary>Local role of the sender: 1 = initiator, 2 = responder.</summary>
    public HandshakeRole Role { get; }

    /// <summary>Human-readable device name shown in the confirmation UI.</summary>
    public string DeviceName { get; }

    /// <summary>RSA-3072 public key as SubjectPublicKeyInfo DER (§3.3); never PKCS#1.</summary>
    public ReadOnlySpan<byte> IdentityPublicKeyDer => _identityPublicKeyDer;

    /// <summary>Ephemeral X25519 public key, 32 bytes.</summary>
    public ReadOnlySpan<byte> EphemeralPublicKey => _ephemeralPublicKey;

    /// <summary>32 random bytes that make each handshake transcript unique.</summary>
    public ReadOnlySpan<byte> Nonce => _nonce;

    /// <summary>Six ASCII digits; only groups the two sides into one handshake, never a secret (§3.8).</summary>
    public string SessionCode { get; }

    /// <summary>UTF-8 bytes of <see cref="DeviceName"/>, precomputed for transcript encoding.</summary>
    public byte[] EncodeDeviceName() => Encoding.UTF8.GetBytes(DeviceName);

    /// <summary>
    /// Returns true for exactly six ASCII digits <c>0</c>–<c>9</c>; length is counted in UTF-8 bytes so
    /// non-ASCII digits are rejected.
    /// </summary>
    public static bool IsValidSessionCode(ReadOnlySpan<char> code)
    {
        if (code.Length != SessionCodeLength)
        {
            return false;
        }

        foreach (char c in code)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Generates a random six-digit pairing code with the platform CSPRNG.</summary>
    public static string CreateSessionCode()
    {
        // Rejection sampling keeps the distribution uniform: 0..999999 is taken from 0..4294967295.
        uint limit = uint.MaxValue - (uint.MaxValue % 1_000_000);
        Span<byte> buffer = stackalloc byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(buffer);
            uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (value < limit)
            {
                return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(HandshakeOffer? other)
        => other is not null
           && Version == other.Version
           && Role == other.Role
           && string.Equals(DeviceName, other.DeviceName, StringComparison.Ordinal)
           && SessionCode == other.SessionCode
           && _identityPublicKeyDer.AsSpan().SequenceEqual(other._identityPublicKeyDer)
           && _ephemeralPublicKey.AsSpan().SequenceEqual(other._ephemeralPublicKey)
           && _nonce.AsSpan().SequenceEqual(other._nonce);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HandshakeOffer);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(Role);
        hash.Add(DeviceName, StringComparer.Ordinal);
        hash.Add(SessionCode, StringComparer.Ordinal);
        hash.AddBytes(_identityPublicKeyDer);
        hash.AddBytes(_ephemeralPublicKey);
        hash.AddBytes(_nonce);
        return hash.ToHashCode();
    }

    /// <summary>Structural equality, so <c>Assert.Equal</c> and <c>==</c> agree.</summary>
    public static bool operator ==(HandshakeOffer? left, HandshakeOffer? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Structural inequality.</summary>
    public static bool operator !=(HandshakeOffer? left, HandshakeOffer? right) => !(left == right);

    /// <summary>Returns a log-safe summary: name and role only, never key material (§开发总则 日志约束).</summary>
    public override string ToString() => $"HandshakeOffer(role={Role}, deviceName={DeviceName})";
}
