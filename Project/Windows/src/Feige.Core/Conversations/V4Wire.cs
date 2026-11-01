using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feige.Core.Crypto;

namespace Feige.Core.Conversations;

/// <summary>Independent v4 wire contract. V3 types and record validation remain unchanged.</summary>
public sealed record V4Offer(
    [property: JsonRequired] ushort Version,
    [property: JsonRequired] byte Role,
    [property: JsonRequired] string DeviceName,
    [property: JsonRequired] byte[] IdentityPublicKeyDER,
    [property: JsonRequired] byte[] EphemeralPublicKey,
    [property: JsonRequired] byte[] Nonce,
    [property: JsonRequired] string SessionCode)
{
    public const ushort CurrentVersion = 4;
    public const ushort DurableAbortVersion = 5;

    public void Validate()
    {
        if (Version is not (CurrentVersion or DurableAbortVersion) || Role is not (1 or 2) || string.IsNullOrEmpty(DeviceName) ||
            Encoding.UTF8.GetByteCount(DeviceName) > 128 || IdentityPublicKeyDER is not { Length: > 0 and <= 1024 } ||
            EphemeralPublicKey is not { Length: 32 } || Nonce is not { Length: 32 } || SessionCode != "")
            throw new InvalidDataException("对方协议版本不兼容或设备信息无效；v4 不会回退到旧版本。");
    }
}

/// <summary>V4 handshake encoding, strict JSON decoding, group record type table, and record cipher helpers.</summary>
public static class V4Wire
{
    public const int MaxPlaintext = 64 * 1024;
    public const int HeaderLength = 13;
    public const int TagLength = 16;
    public const int MaxFrameLength = HeaderLength + MaxPlaintext + TagLength;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    public static T Decode<T>(byte[] bytes)
        => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("消息格式无效。");

    public static bool IsRecordType(byte type) => type is >= 1 and <= 16;

    public static byte[] Transcript(V4Offer first, V4Offer second)
    {
        first.Validate();
        second.Validate();
        if (first.Version != second.Version || first.Role == second.Role || first.IdentityPublicKeyDER.AsSpan().SequenceEqual(second.IdentityPublicKeyDER))
            throw new InvalidDataException("设备身份、协议版本或握手方向无效。");

        V4Offer initiator = first.Role == 1 ? first : second;
        V4Offer responder = first.Role == 2 ? first : second;
        using var output = new MemoryStream();
        output.Write(Encoding.UTF8.GetBytes($"FEIGE-HANDSHAKE-V{initiator.Version}\0"));
        WriteOffer(output, initiator);
        WriteOffer(output, responder);
        return output.ToArray();
    }

    public static string Fingerprint(V4Offer offer) => DeviceFingerprint.Compute(offer.IdentityPublicKeyDER);

    private static void WriteOffer(Stream output, V4Offer offer)
    {
        Span<byte> scalar = stackalloc byte[3];
        BinaryPrimitives.WriteUInt16BigEndian(scalar, offer.Version);
        scalar[2] = offer.Role;
        output.Write(scalar);
        WriteField(output, Encoding.UTF8.GetBytes(offer.DeviceName));
        WriteField(output, offer.IdentityPublicKeyDER);
        WriteField(output, offer.EphemeralPublicKey);
        WriteField(output, offer.Nonce);
        WriteField(output, Encoding.UTF8.GetBytes(offer.SessionCode));
    }

    private static void WriteField(Stream output, ReadOnlySpan<byte> field)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)field.Length));
        output.Write(length);
        output.Write(field);
    }
}

/// <summary>AES-256-GCM v4 directional records; sequence advances only after authentication succeeds.</summary>
public sealed class V4RecordCipher : IDisposable
{
    private readonly AesGcm _aes;
    private readonly byte[] _noncePrefix;
    private readonly object _gate = new();
    private ulong _sequence;
    private bool _disposed;

    public V4RecordCipher(byte[] key, byte[] transcriptDigest)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(transcriptDigest);
        if (key.Length != 32 || transcriptDigest.Length != 32) throw new ArgumentException("Invalid v4 record key or transcript digest.");
        _aes = new AesGcm(key, V4Wire.TagLength);
        _noncePrefix = transcriptDigest[..4];
    }

    public byte[] Seal(byte type, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!V4Wire.IsRecordType(type) || plaintext.Length > V4Wire.MaxPlaintext ||
                (type != 7 && plaintext.Length == 0) || _sequence == ulong.MaxValue)
                throw new InvalidDataException("记录类型、长度或序列号无效。");
            var frame = new byte[V4Wire.HeaderLength + plaintext.Length + V4Wire.TagLength];
            frame[0] = type;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1, 8), _sequence);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(9, 4), checked((uint)(plaintext.Length + V4Wire.TagLength)));
            Span<byte> nonce = stackalloc byte[12];
            _noncePrefix.CopyTo(nonce);
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sequence);
            _aes.Encrypt(nonce, plaintext, frame.AsSpan(V4Wire.HeaderLength, plaintext.Length),
                frame.AsSpan(V4Wire.HeaderLength + plaintext.Length, V4Wire.TagLength), frame.AsSpan(0, V4Wire.HeaderLength));
            _sequence++;
            return frame;
        }
    }

    public (byte Type, byte[] Payload) Open(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (frame.Length < V4Wire.HeaderLength + V4Wire.TagLength || frame.Length > V4Wire.MaxFrameLength ||
                !V4Wire.IsRecordType(frame[0]) || _sequence == ulong.MaxValue ||
                BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1, 8)) != _sequence ||
                BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9, 4)) != frame.Length - V4Wire.HeaderLength)
                throw new InvalidDataException("记录长度、类型或序列号无效。");
            int plaintextLength = frame.Length - V4Wire.HeaderLength - V4Wire.TagLength;
            if (plaintextLength > V4Wire.MaxPlaintext || (frame[0] != 7 && plaintextLength == 0))
                throw new InvalidDataException("记录明文长度无效。");
            Span<byte> nonce = stackalloc byte[12];
            _noncePrefix.CopyTo(nonce);
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sequence);
            var plaintext = new byte[plaintextLength];
            _aes.Decrypt(nonce, frame.AsSpan(V4Wire.HeaderLength, plaintextLength),
                frame.AsSpan(V4Wire.HeaderLength + plaintextLength, V4Wire.TagLength), plaintext,
                frame.AsSpan(0, V4Wire.HeaderLength));
            _sequence++;
            return (frame[0], plaintext);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_noncePrefix);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
