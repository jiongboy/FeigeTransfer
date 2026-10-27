using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>
/// Applies a per-operation timeout to an inner stream, turning a stalled peer into
/// <see cref="ProtocolError.Timeout"/> instead of an indefinitely pending read.
/// </summary>
/// <remarks>
/// <para>
/// This is an idle timeout, not a total deadline: each <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> and
/// write gets its own budget, so a long transfer of many small records still progresses as long as data keeps flowing.
/// </para>
/// <para>
/// A cancelled socket read leaves the underlying connection unusable, which matches protocol v1: every error is fatal
/// and the caller closes the connection (§4.6). This type does not own <see cref="Inner"/>.
/// </para>
/// </remarks>
public sealed class TimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _timeout;
    private readonly bool _leaveOpen;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">Stream to delegate to.</param>
    /// <param name="timeout">Per-operation budget.</param>
    /// <param name="leaveOpen">
    /// Keep the inner stream alive on dispose. Used when the same socket is wrapped twice — once for the handshake and
    /// once for the record layer — so the first wrapper must not close the connection the second one still needs.
    /// </param>
    public TimeoutStream(Stream inner, TimeSpan timeout, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive and at most one hour.");
        }

        _timeout = timeout;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The wrapped stream, for callers that need socket-level detail.</summary>
    public Stream Inner => _inner;

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        using var cts = new CancellationTokenSource(_timeout);
        WriteAsync(buffer.AsMemory(offset, count), cts.Token).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            return await _inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProtocolException(ProtocolError.Timeout, ex);
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            await _inner.WriteAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProtocolException(ProtocolError.Timeout, ex);
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
