using Feige.Core.Crypto;

namespace Feige.Core.Protocol;

/// <summary>
/// Adapts a connected stream to <see cref="IRecordChannel"/> using the session keys from the handshake.
/// </summary>
/// <remarks>
/// Reads and writes are serialized: the record stream keeps per-direction sequence state, and a transfer pumps both
/// directions (chunks out, receipt in) from different tasks.
/// </remarks>
public sealed class StreamRecordChannel : IRecordChannel
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly AuthenticatedRecordStream _records;
    private readonly Queue<OpenedRecord> _pending = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Wraps a connected stream with the session keys derived by the handshake.</summary>
    /// <param name="stream">The same connection the handshake ran on.</param>
    /// <param name="keys">Keys returned by <see cref="LocalHandshake"/>.</param>
    /// <param name="initialBytes">
    /// Bytes the handshake reader consumed beyond the last handshake message, from
    /// <see cref="StreamHandshakeDuplex.TakeRemainder"/>. They are usually the beginning of the first record.
    /// </param>
    public StreamRecordChannel(Stream stream, SessionKeys keys, ReadOnlyMemory<byte> initialBytes = default)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _records = AuthenticatedRecordStream.FromSessionKeys(keys ?? throw new ArgumentNullException(nameof(keys)));

        if (!initialBytes.IsEmpty)
        {
            foreach (OpenedRecord record in _records.Append(initialBytes.Span))
            {
                _pending.Enqueue(record);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(TransferMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] frame = _records.Encode(type, payload.Span);
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<OpenedRecord> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_pending.Count == 0)
            {
                byte[] buffer = new byte[ReadBufferSize];
                int read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new ProtocolException(ProtocolError.UnexpectedEndOfStream);
                }

                foreach (OpenedRecord record in _records.Append(buffer.AsSpan(0, read)))
                {
                    _pending.Enqueue(record);
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
