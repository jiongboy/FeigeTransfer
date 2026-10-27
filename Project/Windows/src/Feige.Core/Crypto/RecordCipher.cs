using System.Buffers.Binary;
using Feige.Core.Protocol;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Feige.Core.Crypto;

/// <summary>
/// AES-256-GCM record codec for one direction of one handshake session (通信协议 §4.1).
/// <para>
/// Layout, without the outer 4-byte length prefix:
/// <c>type(1) | sequence(8, BE) | ctLen(4, BE) | ciphertext | tag(16)</c>, where the first 13 bytes
/// are the AEAD's associated data.
/// </para>
/// <para>
/// The nonce is <b>16 bytes</b>, <c>prefix(8) || sequence(8, BE)</c>. That is not the 12-byte nonce
/// <c>System.Security.Cryptography.AesGcm</c> accepts, so this class uses BouncyCastle's
/// <c>GcmBlockCipher</c>, which handles arbitrary nonce lengths via GHASH.
/// </para>
/// <para>
/// Instances are stateful and single-use per connection: sequence numbering starts at 0 and only
/// moves forward, which is what makes replay, drop and reorder detectable. Never share an instance
/// between connections or clone it with the same key and sequence origin.
/// </para>
/// </summary>
public sealed class RecordCipher
{
    /// <summary>Length of the authenticated header: type + sequence + ctLen.</summary>
    public const int HeaderSize = 13;

    /// <summary>Length of the GCM authentication tag.</summary>
    public const int TagSize = 16;

    /// <summary>Largest plaintext a single record may carry (§4.2).</summary>
    public const int MaxPlaintextSize = 64 * 1024;

    /// <summary>Smallest possible frame: header plus tag, i.e. an empty payload.</summary>
    public const int MinFrameSize = HeaderSize + TagSize;

    /// <summary>Largest possible frame: header + 65536 plaintext + tag = 65565.</summary>
    public const int MaxFrameSize = HeaderSize + MaxPlaintextSize + TagSize;

    private const int MacSizeBits = TagSize * 8;

    private readonly byte[] _key;
    private readonly byte[] _noncePrefix;
    private readonly KeyParameter _keyParameter;
    private readonly object _gate = new();
    private ulong _nextSequence;

    /// <summary>
    /// Creates a cipher from raw direction material. Prefer <see cref="SessionKeySchedule.Derive"/> and
    /// <see cref="ForSending"/>/<see cref="ForReceiving"/> in production; this constructor exists so tests can
    /// drive the fixed vectors in §8.2 without fabricating a handshake.
    /// </summary>
    /// <param name="key">32-byte AES-256 key.</param>
    /// <param name="noncePrefix">8-byte nonce prefix (<c>digest[0..4] || "INIT"|"RESP"</c>).</param>
    public RecordCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix)
    {
        if (key.Length != SessionKeySchedule.KeySize)
        {
            throw new ArgumentException($"Record key must be {SessionKeySchedule.KeySize} bytes, got {key.Length}.", nameof(key));
        }

        if (noncePrefix.Length != SessionKeySchedule.NoncePrefixSize)
        {
            throw new ArgumentException(
                $"Nonce prefix must be {SessionKeySchedule.NoncePrefixSize} bytes, got {noncePrefix.Length}.",
                nameof(noncePrefix));
        }

        _key = key.ToArray();
        _noncePrefix = noncePrefix.ToArray();
        _keyParameter = new KeyParameter(_key);
    }

    /// <summary>Number of records sealed or opened so far; equals the next expected sequence number.</summary>
    public ulong NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _nextSequence;
            }
        }
    }

    /// <summary>Creates the sending cipher for <paramref name="keys"/>.</summary>
    public static RecordCipher ForSending(SessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new RecordCipher(keys.SendKey, keys.SendNoncePrefix);
    }

    /// <summary>Creates the receiving cipher for <paramref name="keys"/>.</summary>
    public static RecordCipher ForReceiving(SessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new RecordCipher(keys.ReceiveKey, keys.ReceiveNoncePrefix);
    }

    /// <summary>
    /// Encrypts one record and returns <c>header || ciphertext || tag</c> (13 + plaintext + 16 bytes).
    /// The caller prepends the <c>u32</c> length field; see <see cref="AuthenticatedRecordStream.Encode"/>.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidType"/> for a type outside 1..7,
    /// <see cref="ProtocolError.InvalidLength"/> for a payload larger than 65536 bytes or empty when the type
    /// is not <see cref="TransferMessageType.Finish"/>, and
    /// <see cref="ProtocolError.SequenceExhausted"/> when the 64-bit counter is used up.
    /// </exception>
    public byte[] Seal(TransferMessageType type, ReadOnlySpan<byte> plaintext)
    {
        byte typeByte = (byte)type;
        if (!TransferMessageTypes.IsDefined(typeByte))
        {
            throw new ProtocolException(ProtocolError.InvalidType);
        }

        if (plaintext.Length > MaxPlaintextSize)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        if (plaintext.IsEmpty && type != TransferMessageType.Finish)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        lock (_gate)
        {
            if (_nextSequence == ulong.MaxValue)
            {
                throw new ProtocolException(ProtocolError.SequenceExhausted);
            }

            ulong sequence = _nextSequence;
            byte[] frame = SealCore(typeByte, sequence, plaintext);
            _nextSequence = sequence + 1;
            return frame;
        }
    }

    /// <summary>
    /// Verifies and decrypts one frame (<c>header || ciphertext || tag</c>, without the length prefix).
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidLength"/> when the frame or <c>ctLen</c> is inconsistent,
    /// <see cref="ProtocolError.InvalidType"/> for a type outside 1..7,
    /// <see cref="ProtocolError.UnexpectedSequence"/> for a duplicate, gap or reorder,
    /// <see cref="ProtocolError.AuthenticationFailed"/> when the tag does not verify.
    /// </exception>
    public OpenedRecord Open(ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < MinFrameSize or > MaxFrameSize)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        byte typeByte = frame[0];
        if (!TransferMessageTypes.IsDefined(typeByte))
        {
            throw new ProtocolException(ProtocolError.InvalidType);
        }

        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(frame[1..9]);
        uint ciphertextLength = BinaryPrimitives.ReadUInt32BigEndian(frame[9..13]);

        if (ciphertextLength < TagSize
            || ciphertextLength > MaxPlaintextSize + TagSize
            || frame.Length != HeaderSize + ciphertextLength)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        lock (_gate)
        {
            if (_nextSequence == ulong.MaxValue)
            {
                throw new ProtocolException(ProtocolError.SequenceExhausted);
            }

            if (sequence != _nextSequence)
            {
                throw new ProtocolException(ProtocolError.UnexpectedSequence);
            }

            byte[] plaintext = OpenCore(frame, sequence, ciphertextLength);

            int payloadLength = (int)ciphertextLength - TagSize;
            if (payloadLength == 0 && typeByte != (byte)TransferMessageType.Finish)
            {
                throw new ProtocolException(ProtocolError.InvalidLength);
            }

            _nextSequence = sequence + 1;
            return new OpenedRecord((TransferMessageType)typeByte, sequence, plaintext);
        }
    }

    private byte[] SealCore(byte type, ulong sequence, ReadOnlySpan<byte> plaintext)
    {
        byte[] header = new byte[HeaderSize];
        header[0] = type;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(1, 8), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9, 4), (uint)(plaintext.Length + TagSize));

        byte[] nonce = SessionKeySchedule.BuildNonce(_noncePrefix, sequence);

        var cipher = CreateCipher(forEncryption: true, nonce, header);
        byte[] plaintextBytes = plaintext.ToArray();
        byte[] body = new byte[cipher.GetOutputSize(plaintextBytes.Length)];
        int written = cipher.ProcessBytes(plaintextBytes, 0, plaintextBytes.Length, body, 0);
        written += cipher.DoFinal(body, written);

        if (written != plaintextBytes.Length + TagSize)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        byte[] frame = new byte[HeaderSize + written];
        header.CopyTo(frame, 0);
        body.AsSpan(0, written).CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    private byte[] OpenCore(ReadOnlySpan<byte> frame, ulong sequence, uint ciphertextLength)
    {
        byte[] header = frame[..HeaderSize].ToArray();
        byte[] body = frame[HeaderSize..].ToArray();
        byte[] nonce = SessionKeySchedule.BuildNonce(_noncePrefix, sequence);

        var cipher = CreateCipher(forEncryption: false, nonce, header);
        byte[] plaintext = new byte[cipher.GetOutputSize(body.Length)];
        int written;
        try
        {
            written = cipher.ProcessBytes(body, 0, body.Length, plaintext, 0);
            written += cipher.DoFinal(plaintext, written);
        }
        catch (InvalidCipherTextException ex)
        {
            // Tampered header, ciphertext or tag; the reason is deliberately not distinguished.
            throw new ProtocolException(ProtocolError.AuthenticationFailed, ex);
        }

        if (written != (int)ciphertextLength - TagSize)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        return written == plaintext.Length ? plaintext : plaintext[..written];
    }

    private GcmBlockCipher CreateCipher(bool forEncryption, byte[] nonce, byte[] associatedData)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption, new AeadParameters(_keyParameter, MacSizeBits, nonce, associatedData));
        return cipher;
    }
}
