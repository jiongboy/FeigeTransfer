using System.Security.Cryptography;
using Feige.Core.Protocol;

namespace Feige.Core.Storage;

/// <summary>
/// Streams incoming bytes into a temporary file, hashes them as they arrive, and only then renames the file into
/// place (通信协议 §5.1, 安全要求 6).
/// </summary>
/// <remarks>
/// <para>
/// The ordering here is the point: <see cref="CommitAsync"/> verifies the byte count and the SHA-256 <b>before</b>
/// the rename, and the caller sends <c>receipt</c> only after it returns. The macOS reference implementation sends
/// the receipt first and renames in the caller, so a failed rename makes the sender believe the transfer succeeded
/// (§5.3); this class makes that mistake impossible to express.
/// </para>
/// <para>
/// Nothing is written to the final path until verification passes: the temp file is created with
/// <see cref="FileMode.CreateNew"/> in the target directory so the rename stays on one volume and is atomic, and
/// <see cref="FileMode.CreateNew"/> also means an existing file or symlink is never followed.
/// </para>
/// <para>
/// Existing files are never overwritten: the commit picks <c>name (1).ext</c>, <c>name (2).ext</c>, … instead.
/// </para>
/// </remarks>
public sealed class TransferFileSink : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _tempPath;
    private readonly long _expectedSize;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private FileStream? _stream;
    private long _written;
    private bool _committed;
    private bool _disposed;
    private readonly bool _preserveFileName;

    private TransferFileSink(string directory, string tempPath, string requestedFileName, long expectedSize, FileStream stream, bool preserveFileName)
    {
        _directory = directory;
        _tempPath = tempPath;
        RequestedFileName = requestedFileName;
        _expectedSize = expectedSize;
        _stream = stream;
        _preserveFileName = preserveFileName;
    }

    /// <summary>Name the sender asked for, before any local sanitization.</summary>
    public string RequestedFileName { get; }

    /// <summary>Bytes accepted so far.</summary>
    public long Written => _written;

    /// <summary>
    /// Creates the temporary file for a transfer of <paramref name="expectedSize"/> bytes in
    /// <paramref name="targetDirectory"/>.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.StorageFailure"/> when the directory or temp file is unusable.</exception>
    public static TransferFileSink Create(string targetDirectory, string requestedFileName, long expectedSize)
    {
        if (expectedSize < 1 || expectedSize > TransferManifest.MaxFileSize)
            throw new ProtocolException(ProtocolError.InvalidManifest);
        return CreateCore(targetDirectory, requestedFileName, expectedSize, preserveFileName: false);
    }

    /// <summary>V3 streams zero-length and large attachments without the legacy v1 product cap.</summary>
    public static TransferFileSink CreateForSession(string targetDirectory, string requestedFileName, long expectedSize)
    {
        SafeFileName.ValidateForWindows(requestedFileName);
        return CreateCore(targetDirectory, requestedFileName, expectedSize, preserveFileName: true);
    }

    private static TransferFileSink CreateCore(string targetDirectory, string requestedFileName, long expectedSize, bool preserveFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        TransferManifest.ValidateFileName(requestedFileName);

        if (expectedSize < 0)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            string tempPath = Path.Combine(targetDirectory, $".feige-{Guid.NewGuid():N}.tmp");
            FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return new TransferFileSink(targetDirectory, tempPath, requestedFileName, expectedSize, stream, preserveFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ProtocolException(ProtocolError.StorageFailure, ex);
        }
    }

    /// <summary>
    /// Appends one chunk. The cumulative length may never exceed the manifest size, so a lying sender cannot fill
    /// the disk past what the user approved.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.SizeMismatch"/> or <see cref="ProtocolError.StorageFailure"/>.</exception>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_committed)
        {
            throw new InvalidOperationException("The sink has already been committed.");
        }

        if (data.Length > _expectedSize - _written)
        {
            throw new ProtocolException(ProtocolError.SizeMismatch);
        }

        try
        {
            await _stream!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException(ProtocolError.StorageFailure, ex);
        }

        _hash.AppendData(data.Span);
        _written += data.Length;
    }

    /// <summary>
    /// Verifies size and SHA-256, then atomically renames the temp file into place and returns its final path.
    /// </summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.SizeMismatch"/>, <see cref="ProtocolError.ChecksumMismatch"/> or
    /// <see cref="ProtocolError.StorageFailure"/>; in every failure case the temp file is left for disposal.
    /// </exception>
    public async Task<string> CommitAsync(ReadOnlyMemory<byte> expectedSha256, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_committed)
        {
            throw new InvalidOperationException("The sink has already been committed.");
        }

        if (expectedSha256.Length != TransferManifest.Sha256Size)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        if (_written != _expectedSize)
        {
            throw new ProtocolException(ProtocolError.SizeMismatch);
        }

        byte[] actual = _hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha256.Span))
        {
            throw new ProtocolException(ProtocolError.ChecksumMismatch);
        }

        try
        {
            FileStream stream = _stream!;
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            await stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;

            string finalPath = MoveToUniquePath(_preserveFileName ? RequestedFileName : SafeFileName.MakeWritable(RequestedFileName));
            _committed = true;
            return finalPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException(ProtocolError.StorageFailure, ex);
        }
    }

    /// <summary>Deletes the temp file when the transfer did not commit.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hash.Dispose();

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (_committed)
        {
            return;
        }

        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless and must not mask the original failure.
        }
    }

    private string MoveToUniquePath(string fileName)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string candidate = Path.Combine(_directory, SafeFileName.WithSuffix(fileName, attempt));
            try
            {
                File.Move(_tempPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken in the meantime: try the next suffix. Never overwrite (§安全要求 6).
            }
        }

        throw new ProtocolException(ProtocolError.StorageFailure);
    }
}
