using System.Buffers.Binary;
using Feige.Core.Crypto;

namespace Feige.Core.Protocol;

/// <summary>
/// Frames and unframes length-prefixed AES-GCM records on top of a byte stream (通信协议 §4.1, §4.2).
/// <para>
/// Each frame is <c>length(u32, BE) | type | sequence | ctLen | ciphertext | tag</c>, where
/// <paramref name="length"/> counts everything after itself (13 + ctLen). Incoming bytes may arrive in
/// arbitrary fragments: complete records are decoded in sequence order and partial ones stay buffered.
/// </para>
/// <para>
/// The stream enforces the direction rules of §4.2: nothing may be sent after <c>finish</c>, nothing may be
/// received after <c>finish</c>, the byte stream must end exactly on a <c>finish</c> boundary, and the unread
/// buffer is capped so a peer cannot make us buffer unbounded data by announcing huge or fragmented records.
/// </para>
/// </summary>
public sealed class AuthenticatedRecordStream
{
    /// <summary>
    /// Maximum unread bytes kept while waiting for a record to complete: eight maximum-size frames.
    /// </summary>
    public const int MaxReadBatchSize = RecordCipher.MaxFrameSize * 8;

    private readonly RecordCipher _sendCipher;
    private readonly RecordCipher _receiveCipher;
    private readonly object _gate = new();

    private byte[] _buffer = new byte[4096];
    private int _count;
    private bool _sendFinished;
    private bool _receiveFinished;

    /// <summary>Creates a stream from an explicit pair of directional ciphers.</summary>
    public AuthenticatedRecordStream(RecordCipher sendCipher, RecordCipher receiveCipher)
    {
        _sendCipher = sendCipher ?? throw new ArgumentNullException(nameof(sendCipher));
        _receiveCipher = receiveCipher ?? throw new ArgumentNullException(nameof(receiveCipher));
    }

    /// <summary>Creates the sending and receiving ciphers for this side of a handshake session.</summary>
    public static AuthenticatedRecordStream FromSessionKeys(SessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new AuthenticatedRecordStream(RecordCipher.ForSending(keys), RecordCipher.ForReceiving(keys));
    }

    /// <summary>True once this side has sealed a <c>finish</c> record.</summary>
    public bool SendFinished
    {
        get
        {
            lock (_gate)
            {
                return _sendFinished;
            }
        }
    }

    /// <summary>True once this side has opened a <c>finish</c> record.</summary>
    public bool ReceiveFinished
    {
        get
        {
            lock (_gate)
            {
                return _receiveFinished;
            }
        }
    }

    /// <summary>Bytes of an incomplete frame currently buffered.</summary>
    public int PendingByteCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Seals one record and prepends the <c>u32</c> length field, ready to write to the socket.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.AlreadyFinished"/> after a <c>finish</c> record, plus every error
    /// <see cref="RecordCipher.Seal"/> can raise.
    /// </exception>
    public byte[] Encode(TransferMessageType type, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_sendFinished)
            {
                throw new ProtocolException(ProtocolError.AlreadyFinished);
            }

            byte[] frame = _sendCipher.Seal(type, payload);
            byte[] output = new byte[sizeof(uint) + frame.Length];
            BinaryPrimitives.WriteUInt32BigEndian(output, (uint)frame.Length);
            frame.CopyTo(output, sizeof(uint));

            if (type == TransferMessageType.Finish)
            {
                _sendFinished = true;
            }

            return output;
        }
    }

    /// <summary>
    /// Feeds raw socket bytes and returns every record that became complete, in order.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// Any decoding, sequencing or authentication failure, plus
    /// <see cref="ProtocolError.AlreadyFinished"/> for bytes after <c>finish</c>.
    /// </exception>
    public IReadOnlyList<OpenedRecord> Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_receiveFinished)
            {
                if (bytes.IsEmpty)
                {
                    return Array.Empty<OpenedRecord>();
                }

                throw new ProtocolException(ProtocolError.AlreadyFinished);
            }

            var records = new List<OpenedRecord>();
            int offset = 0;

            do
            {
                if (offset < bytes.Length)
                {
                    int room = MaxReadBatchSize - _count;
                    if (room <= 0)
                    {
                        throw new ProtocolException(ProtocolError.FrameTooLarge);
                    }

                    int take = Math.Min(room, bytes.Length - offset);
                    EnsureCapacity(_count + take);
                    bytes.Slice(offset, take).CopyTo(_buffer.AsSpan(_count));
                    _count += take;
                    offset += take;
                }

                Drain(records);
            }
            while (offset < bytes.Length);

            return records;
        }
    }

    /// <summary>
    /// Asserts the peer closed the stream in the only acceptable way: right after an authenticated
    /// <c>finish</c> record, with no partial or trailing bytes (§4.6).
    /// </summary>
    public void EndOfStream()
    {
        lock (_gate)
        {
            if (_count != 0 || !_receiveFinished)
            {
                throw new ProtocolException(ProtocolError.UnexpectedEndOfStream);
            }
        }
    }

    private void Drain(List<OpenedRecord> records)
    {
        int consumed = 0;

        while (_count - consumed >= sizeof(uint))
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(consumed, sizeof(uint)));
            if (length < RecordCipher.MinFrameSize || length > RecordCipher.MaxFrameSize)
            {
                // Checked before waiting for the body so a bogus length cannot pin the buffer (§4.2).
                throw new ProtocolException(ProtocolError.InvalidLength);
            }

            int frameSize = (int)length;
            if (_count - consumed < sizeof(uint) + frameSize)
            {
                break;
            }

            OpenedRecord record = _receiveCipher.Open(_buffer.AsSpan(consumed + sizeof(uint), frameSize));
            consumed += sizeof(uint) + frameSize;
            records.Add(record);

            if (record.Type == TransferMessageType.Finish)
            {
                if (_count != consumed)
                {
                    throw new ProtocolException(ProtocolError.UnexpectedTrailingData);
                }

                _receiveFinished = true;
                break;
            }
        }

        if (consumed == 0)
        {
            return;
        }

        _count -= consumed;
        if (_count > 0)
        {
            Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _count);
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
        {
            return;
        }

        int size = _buffer.Length;
        while (size < required)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }
}
