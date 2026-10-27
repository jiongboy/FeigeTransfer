using System.Net.Sockets;

namespace Feige.Core.Protocol;

/// <summary>
/// Adapts a connected <see cref="System.IO.Stream"/> (a <c>NetworkStream</c> in the app) to
/// <see cref="IHandshakeDuplex"/>, reassembling the <c>u32</c>-prefixed JSON messages of §3.1.
/// </summary>
/// <remarks>
/// Writes are serialized because the handshake may write from a different task than it reads; reads are serialized
/// because <see cref="HandshakeFrameReader"/> is stateful per connection. This type does not own the stream.
/// </remarks>
public sealed class StreamHandshakeDuplex : IHandshakeDuplex
{
    private const int ReadBufferSize = 4096;

    private readonly Stream _stream;
    private readonly HandshakeFrameReader _reader = new();
    private readonly Queue<byte[]> _pending = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Wraps an already-connected stream.</summary>
    public StreamHandshakeDuplex(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Hands over bytes that were read beyond the last handshake message, so they can be fed to the record layer.
    /// Call this once the handshake has finished and pass the result to
    /// <see cref="StreamRecordChannel"/>'s <c>initialBytes</c>; dropping them loses the start of the first record.
    /// </summary>
    public byte[] TakeRemainder() => _reader.TakeRemainder();

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new ProtocolException(ProtocolError.UnexpectedEndOfStream, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_pending.Count == 0)
            {
                byte[] buffer = new byte[ReadBufferSize];
                int read;

                try
                {
                    read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    // A peer that vanishes mid-handshake surfaces as an I/O error, not a clean EOF; both mean the
                    // same thing here, and callers should see a protocol error rather than a raw socket exception.
                    throw new ProtocolException(ProtocolError.UnexpectedEndOfStream, ex);
                }

                if (read == 0)
                {
                    // A handshake must be completed; a bare EOF here is a protocol failure, not a graceful close.
                    throw new ProtocolException(ProtocolError.UnexpectedEndOfStream);
                }

                foreach (byte[] payload in _reader.Append(buffer.AsSpan(0, read)))
                {
                    _pending.Enqueue(payload);
                }
            }

            return _pending.Dequeue();
        }
        finally
        {
            _readLock.Release();
        }
    }
}
