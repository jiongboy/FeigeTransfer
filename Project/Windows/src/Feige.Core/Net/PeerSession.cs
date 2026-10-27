using System.Net;
using System.Net.Sockets;
using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>
/// One connected, authenticated TCP session: a completed handshake plus the record channel built on top of it,
/// ready to carry exactly one file in either direction.
/// </summary>
/// <remarks>
/// <para>
/// 通信协议 §1: a connection carries one handshake and one single-file transfer, then closes. There is no resume and
/// no second transfer on the same connection, so <see cref="SendFileAsync"/>, <see cref="SendAsync"/> and
/// <see cref="ReceiveFileAsync"/> may be called at most once between them; doing it twice throws.
/// </para>
/// <para>
/// The handshake role determines key direction only. Either side may send (§1), so both methods exist on both roles.
/// </para>
/// <para>
/// Disposing closes the socket without any protocol goodbye — v1 has none, and a peer detects the close as EOF (§4.6).
/// </para>
/// </remarks>
public sealed class PeerSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly TimeoutStream _stream;
    private readonly StreamRecordChannel _channel;
    private int _transferStarted;
    private int _disposed;

    internal PeerSession(TcpClient client, TimeoutStream stream, StreamRecordChannel channel, HandshakeOutcome handshake)
    {
        _client = client;
        _stream = stream;
        _channel = channel;
        Handshake = handshake;
    }

    /// <summary>Keys, transcript and peer offer produced by the handshake.</summary>
    public HandshakeOutcome Handshake { get; }

    /// <summary>The peer's announced name, already validated by the handshake.</summary>
    public string PeerDeviceName => Handshake.Peer.DeviceName;

    /// <summary>Fingerprint the local user confirmed; the only identity value worth persisting.</summary>
    public string PeerFingerprint => Handshake.PeerFingerprint;

    /// <summary>Six-digit value both sides displayed before approving (§3.10).</summary>
    public string Sas => Handshake.Sas;

    /// <summary>Peer socket address, or null once the socket is gone.</summary>
    public IPEndPoint? RemoteEndPoint => _client.Client.RemoteEndPoint as IPEndPoint;

    /// <summary>True once a transfer has been started on this connection in either direction.</summary>
    public bool TransferStarted => Volatile.Read(ref _transferStarted) != 0;

    /// <summary>
    /// Hashes <paramref name="path"/>, announces it, streams it and waits for the receiver's <c>receipt</c> (§5.1).
    /// </summary>
    /// <param name="path">Existing file, 1 byte to 100 MiB.</param>
    /// <param name="progress">Bytes acknowledged as sent so far.</param>
    /// <param name="cancellationToken">Aborts the transfer; the connection is then unusable.</param>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.PeerRejected"/> when the receiver declines,
    /// <see cref="ProtocolError.InvalidManifest"/> when the file violates §4.4, or a record-layer error.
    /// </exception>
    public async Task SendFileAsync(string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfDisposed();

        TransferManifest manifest = await TransferManifest.ForFileAsync(path, cancellationToken).ConfigureAwait(false);

        await using var source = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, TransferSender.ChunkSize, useAsync: true);

        await SendAsync(manifest, source, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends an already-prepared manifest and source stream.</summary>
    /// <remarks>Hashing is the caller's job here; <see cref="SendFileAsync"/> does it for a path.</remarks>
    public async Task SendAsync(
        TransferManifest manifest,
        Stream source,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        ClaimTransfer();

        await TransferSender.SendAsync(_channel, manifest, source, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives one file into <paramref name="targetDirectory"/>, which is created when missing.
    /// </summary>
    /// <param name="targetDirectory">Destination directory; the final name may be adjusted for Windows and collisions.</param>
    /// <param name="approve">Asked once, before anything touches the disk.</param>
    /// <param name="progress">Bytes received so far.</param>
    /// <param name="cancellationToken">Aborts and deletes the partial temp file.</param>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.UserRejected"/> when the local user declines,
    /// <see cref="ProtocolError.ChecksumMismatch"/> when the digest does not match, or a record-layer error.
    /// </exception>
    public async Task<ReceivedFile> ReceiveFileAsync(
        string targetDirectory,
        Func<TransferManifest, bool> approve,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        ArgumentNullException.ThrowIfNull(approve);
        ClaimTransfer();

        return await TransferReceiver.ReceiveAsync(_channel, targetDirectory, approve, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Closes the socket. Safe to call twice and while a transfer is failing.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // TimeoutStream owns the NetworkStream, which owns the socket; TcpClient disposal is then a no-op guard.
        try
        {
            _stream.Dispose();
        }
        finally
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void ClaimTransfer()
    {
        ThrowIfDisposed();

        if (Interlocked.Exchange(ref _transferStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "This connection already carried a transfer; protocol v1 allows one file per connection.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
