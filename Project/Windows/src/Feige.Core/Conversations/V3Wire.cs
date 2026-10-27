using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feige.Core.Crypto;

namespace Feige.Core.Conversations;

public sealed record V3Offer(
    [property: JsonRequired] ushort Version, [property: JsonRequired] byte Role,
    [property: JsonRequired] string DeviceName, [property: JsonRequired] byte[] IdentityPublicKeyDER,
    [property: JsonRequired] byte[] EphemeralPublicKey, [property: JsonRequired] byte[] Nonce,
    [property: JsonRequired] string SessionCode)
{
    public void Validate()
    {
        if (Version != 3 || Role is not (1 or 2) || string.IsNullOrEmpty(DeviceName) ||
            Encoding.UTF8.GetByteCount(DeviceName) > 128 || IdentityPublicKeyDER is not { Length: > 0 and <= 1024 } ||
            EphemeralPublicKey is not { Length: 32 } || Nonce is not { Length: 32 } || SessionCode != "")
            throw new InvalidDataException("对方协议版本不兼容或设备信息无效，请两端更新到 v3。");
    }
}

public sealed record V3Text([property: JsonRequired] Guid Id, [property: JsonRequired] string Text)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Text) || Encoding.UTF8.GetByteCount(Text) > 8192)
            throw new InvalidDataException("消息不能为空，单条文字最多 8 KB，长文可作为文件发送。");
    }
}

public sealed record V3File([property: JsonRequired] Guid TransferID, [property: JsonRequired] string FileName,
    [property: JsonRequired] long Size, string Kind = "file")
{
    public void Validate()
    {
        if (Size < 0 || Kind is not ("file" or "image")) throw new InvalidDataException("附件信息无效。");
        Protocol.TransferManifest.ValidateFileName(FileName);
    }
}

public static class V3Wire
{
    public const int MaxPlaintext = 65536;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("消息格式无效。");
    public static bool IsRecordType(byte type) => type is >= 1 and <= 4 or >= 7 and <= 10;

    public static byte[] Transcript(V3Offer a, V3Offer b)
    {
        a.Validate(); b.Validate();
        if (a.Role == b.Role || a.IdentityPublicKeyDER.AsSpan().SequenceEqual(b.IdentityPublicKeyDER))
            throw new InvalidDataException("设备身份或握手方向无效。");
        using var stream = new MemoryStream();
        stream.Write("FEIGE-HANDSHAKE-V3\0"u8);
        foreach (var o in a.Role == 1 ? new[] { a, b } : new[] { b, a })
        {
            stream.Write(new byte[] { 0, 3, o.Role });
            foreach (var field in new[] { Encoding.UTF8.GetBytes(o.DeviceName), o.IdentityPublicKeyDER, o.EphemeralPublicKey, o.Nonce, Array.Empty<byte>() })
            { stream.Write(Length(field.Length)); stream.Write(field); }
        }
        return stream.ToArray();
    }
    public static byte[] Length(int count) { var data = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(data, count); return data; }
    public static string Fingerprint(V3Offer offer) => DeviceFingerprint.Compute(offer.IdentityPublicKeyDER);
}

/// <summary>V3 only. V1's 16-byte nonce and finish-closes-stream codec remain unchanged.</summary>
public sealed class V3RecordCipher : IDisposable
{
    private readonly AesGcm _aes;
    private readonly byte[] _prefix;
    private ulong _sequence;
    private readonly object _gate = new();
    public V3RecordCipher(byte[] key, byte[] digestPrefix)
    {
        if (key.Length != 32 || digestPrefix.Length != 4) throw new ArgumentException("Invalid key/prefix.");
        _aes = new AesGcm(key, 16); _prefix = digestPrefix.ToArray();
    }
    public byte[] Seal(byte type, byte[] plaintext)
    {
        lock (_gate)
        {
            if (!V3Wire.IsRecordType(type) || plaintext.Length is < 1 or > V3Wire.MaxPlaintext || _sequence == ulong.MaxValue)
                throw new InvalidDataException("记录类型或长度无效。");
            var frame = new byte[13 + plaintext.Length + 16]; frame[0] = type;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1), _sequence);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(9), plaintext.Length + 16);
            _aes.Encrypt(Nonce(), plaintext, frame.AsSpan(13, plaintext.Length), frame.AsSpan(13 + plaintext.Length, 16), frame.AsSpan(0, 13));
            _sequence++; return frame;
        }
    }
    public (byte Type, byte[] Payload) Open(byte[] frame)
    {
        lock (_gate)
        {
            if (frame.Length is < 30 or > 65565 || !V3Wire.IsRecordType(frame[0]) || _sequence == ulong.MaxValue ||
                BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1)) != _sequence ||
                BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(9)) != frame.Length - 13)
                throw new InvalidDataException("记录长度、类型或序列号无效。");
            var plaintext = new byte[frame.Length - 29];
            _aes.Decrypt(Nonce(), frame.AsSpan(13, plaintext.Length), frame.AsSpan(frame.Length - 16), plaintext, frame.AsSpan(0, 13));
            _sequence++; return (frame[0], plaintext);
        }
    }
    private byte[] Nonce() { var nonce = new byte[12]; _prefix.CopyTo(nonce, 0); BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _sequence); return nonce; }
    public void Dispose() { lock (_gate) _aes.Dispose(); }
}
