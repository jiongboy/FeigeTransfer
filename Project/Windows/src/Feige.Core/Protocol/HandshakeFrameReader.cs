using System.Buffers.Binary;

namespace Feige.Core.Protocol;

/// <summary>
/// Reassembles handshake messages from an arbitrary byte stream (§3.1: <c>u32</c> length + JSON, 1..16384 bytes).
/// </summary>
/// <remarks>
/// TCP gives no message boundaries, so a JSON body can arrive split across reads or several messages can arrive
/// in one read. The length is validated <b>before</b> waiting for the body: an oversized or zero length is rejected
/// immediately instead of making the process buffer whatever the peer announces.
/// </remarks>
public sealed class HandshakeFrameReader
{
    private readonly object _gate = new();
    private byte[] _buffer = new byte[512];
    private int _count;

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
    /// Appends received bytes and returns the payloads of every message that became complete, in order.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidHandshakeFrame"/> for a zero, oversized or inconsistent length prefix.
    /// </exception>
    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            var payloads = new List<byte[]>();

            if (!bytes.IsEmpty)
            {
                EnsureCapacity(_count + bytes.Length);
                bytes.CopyTo(_buffer.AsSpan(_count));
                _count += bytes.Length;
            }

            Drain(payloads);
            return payloads;
        }
    }

    /// <summary>
    /// Removes and returns bytes that were read past the last complete message.
    /// </summary>
    /// <remarks>
    /// The final handshake message and the first record can arrive in one TCP segment: a peer may finish the
    /// handshake and immediately send its manifest while this side is still reading the proof. Those bytes belong to
    /// the record layer, so they must be handed over instead of dropped.
    /// </remarks>
    public byte[] TakeRemainder()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return [];
            }

            byte[] remainder = _buffer.AsSpan(0, _count).ToArray();
            _count = 0;
            return remainder;
        }
    }

    private void Drain(List<byte[]> payloads)
    {
        int consumed = 0;

        while (_count - consumed >= sizeof(uint))
        {
            uint declared = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(consumed, sizeof(uint)));
            if (declared == 0 || declared > HandshakeMessages.MaxJsonBytes)
            {
                throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
            }

            int total = sizeof(uint) + (int)declared;
            if (_count - consumed < total)
            {
                break;
            }

            payloads.Add(_buffer.AsSpan(consumed + sizeof(uint), (int)declared).ToArray());
            consumed += total;
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
