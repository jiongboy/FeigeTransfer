using Feige.Core.Storage;

namespace Feige.Core.Protocol;

/// <summary>A file that arrived and passed verification.</summary>
/// <param name="Manifest">The manifest the sender announced.</param>
/// <param name="Path">Absolute path the file was atomically renamed to.</param>
public sealed record ReceivedFile(TransferManifest Manifest, string Path);

/// <summary>
/// Receives one file over an authenticated record channel (通信协议 §5.1).
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written before <c>approve</c> returns true, and <c>receipt</c> is sent only after the temp file has been
/// verified and atomically renamed — that ordering is required by §5.1 and is the difference from the current macOS
/// behaviour described in §5.3.
/// </para>
/// <para>
/// Any protocol error, size mismatch or digest mismatch aborts the connection and deletes the temp file, so a failed
/// transfer never leaves a half-written file under the final name.
/// </para>
/// </remarks>
public static class TransferReceiver
{
    private static readonly byte[] Accept = [0x01];
    private static readonly byte[] Decline = [0x00];

    /// <summary>
    /// Reads the manifest, asks the local user, then streams the file into <paramref name="targetDirectory"/>.
    /// </summary>
    /// <param name="channel">Authenticated channel to the sender.</param>
    /// <param name="targetDirectory">Directory the file is written to; created when missing.</param>
    /// <param name="approve">
    /// Invoked once, with the validated manifest, before any file is created. Returning false sends
    /// <c>decision 0x00</c> and aborts with <see cref="ProtocolError.UserRejected"/>.
    /// </param>
    /// <param name="progress">Optional byte counter, reported after each chunk.</param>
    /// <param name="cancellationToken">Aborts the transfer and deletes the temp file.</param>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.InvalidManifest"/>, <see cref="ProtocolError.UnexpectedMessage"/>,
    /// <see cref="ProtocolError.SizeMismatch"/>, <see cref="ProtocolError.ChecksumMismatch"/>,
    /// <see cref="ProtocolError.StorageFailure"/> or <see cref="ProtocolError.UserRejected"/>.
    /// </exception>
    public static async Task<ReceivedFile> ReceiveAsync(
        IRecordChannel channel,
        string targetDirectory,
        Func<TransferManifest, bool> approve,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        ArgumentNullException.ThrowIfNull(approve);

        // 1. The only record allowed before consent.
        OpenedRecord first = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (first.Type != TransferMessageType.Manifest)
        {
            throw new ProtocolException(ProtocolError.UnexpectedMessage);
        }

        TransferManifest manifest = TransferManifest.FromJson(first.Payload);

        // 2. Ask the user. Nothing has touched the disk yet at this point.
        if (!approve(manifest))
        {
            await TrySendDeclineAsync(channel, cancellationToken).ConfigureAwait(false);
            throw new ProtocolException(ProtocolError.UserRejected);
        }

        // 3. Create the temp sink before accepting: if the directory is unusable, the sender must not stream 100 MiB
        //    only to have us fail at the end.
        await using TransferFileSink sink = TransferFileSink.Create(targetDirectory, manifest.FileName, manifest.Size);

        await channel.WriteAsync(TransferMessageType.Decision, Accept, cancellationToken).ConfigureAwait(false);

        // 4. Chunks until finish. Offsets are implied by order, so the only checks are per-chunk size and the running
        //    total against the manifest.
        bool finished = false;
        while (!finished)
        {
            OpenedRecord record = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);

            switch (record.Type)
            {
                case TransferMessageType.Chunk:
                    if (record.Payload.Length is < 1 or > TransferSender.ChunkSize)
                    {
                        throw new ProtocolException(ProtocolError.InvalidLength);
                    }

                    if (sink.Written + record.Payload.Length > manifest.Size)
                    {
                        throw new ProtocolException(ProtocolError.SizeMismatch);
                    }

                    await sink.WriteAsync(record.Payload, cancellationToken).ConfigureAwait(false);
                    progress?.Report(sink.Written);
                    break;

                case TransferMessageType.Finish:
                    finished = true;
                    break;

                default:
                    // decision/receipt never travel sender → receiver; reserved types must not be sent at all.
                    throw new ProtocolException(ProtocolError.UnexpectedMessage);
            }
        }

        // 5. Verify, then rename. Only a committed file earns a receipt.
        string path = await sink.CommitAsync(manifest.Sha256, cancellationToken).ConfigureAwait(false);
        await channel.WriteAsync(TransferMessageType.Receipt, Accept, cancellationToken).ConfigureAwait(false);

        return new ReceivedFile(manifest, path);
    }

    private static async ValueTask TrySendDeclineAsync(IRecordChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await channel.WriteAsync(TransferMessageType.Decision, Decline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or ObjectDisposedException)
        {
            // The refusal is best effort: the sender also detects the closed connection.
        }
    }
}
