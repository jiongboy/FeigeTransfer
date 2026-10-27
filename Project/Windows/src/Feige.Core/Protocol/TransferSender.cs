namespace Feige.Core.Protocol;

/// <summary>
/// Sends one file over an authenticated record channel (通信协议 §5.1).
/// </summary>
/// <remarks>
/// A connection carries exactly one file and then closes (§4.2); there is no resume, no chunk-level ACK and no
/// in-band error record in v1. Any failure simply aborts the connection, which the peer detects as EOF.
/// </remarks>
public static class TransferSender
{
    /// <summary>
    /// Largest chunk payload. 65408 = 65536 − 128, matching the macOS sender and the §4.5 requirement that Windows
    /// never exceed it.
    /// </summary>
    public const int ChunkSize = 65408;

    private static readonly byte[] Accept = [0x01];

    /// <summary>
    /// Announces <paramref name="manifest"/>, waits for the receiver's <c>decision</c>, streams
    /// <paramref name="manifest"/>'s size bytes from <paramref name="source"/>, sends <c>finish</c> and waits for
    /// <c>receipt</c>.
    /// </summary>
    /// <param name="channel">Authenticated channel to the receiver.</param>
    /// <param name="manifest">What is being sent; must already describe <paramref name="source"/> exactly.</param>
    /// <param name="source">Readable stream positioned at offset 0.</param>
    /// <param name="progress">Optional byte counter, reported after each chunk.</param>
    /// <param name="cancellationToken">Aborts the transfer; the caller must close the connection afterwards.</param>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.PeerRejected"/> when the receiver answers <c>0x00</c>,
    /// <see cref="ProtocolError.SizeMismatch"/> when the source is shorter than the manifest, or
    /// <see cref="ProtocolError.UnexpectedMessage"/> for an out-of-sequence record.
    /// </exception>
    public static async Task SendAsync(
        IRecordChannel channel,
        TransferManifest manifest,
        Stream source,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);

        // 1. Manifest goes out in the clear (inside the encrypted record), before any consent.
        await channel.WriteAsync(TransferMessageType.Manifest, manifest.ToJson(), cancellationToken).ConfigureAwait(false);

        // 2. The receiver only writes after its user approves; 0x00 means stop immediately (§5.2).
        OpenedRecord decision = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (decision.Type != TransferMessageType.Decision)
        {
            throw new ProtocolException(ProtocolError.UnexpectedMessage);
        }

        if (decision.Payload.Length != 1)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        if (decision.Payload[0] == 0x00)
        {
            throw new ProtocolException(ProtocolError.PeerRejected);
        }

        if (decision.Payload[0] != 0x01)
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }

        // 3. Exactly manifest.Size bytes, in order, nothing more.
        byte[] buffer = new byte[ChunkSize];
        long sent = 0;
        while (sent < manifest.Size)
        {
            int wanted = (int)Math.Min(buffer.Length, manifest.Size - sent);
            int read = await ReadAtLeastAsync(source, buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read != wanted)
            {
                // The file shrank or the manifest lied; either way the digest can never match.
                throw new ProtocolException(ProtocolError.SizeMismatch);
            }

            await channel.WriteAsync(TransferMessageType.Chunk, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;
            progress?.Report(sent);
        }

        // 4. Empty finish, then the only success signal in v1.
        await channel.WriteAsync(TransferMessageType.Finish, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);

        OpenedRecord receipt = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (receipt.Type != TransferMessageType.Receipt)
        {
            throw new ProtocolException(ProtocolError.UnexpectedMessage);
        }

        if (receipt.Payload.Length != 1 || receipt.Payload[0] != Accept[0])
        {
            throw new ProtocolException(ProtocolError.InvalidLength);
        }
    }

    /// <summary>Fills <paramref name="buffer"/> as far as the stream allows; returns the bytes actually read.</summary>
    private static async Task<int> ReadAtLeastAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
