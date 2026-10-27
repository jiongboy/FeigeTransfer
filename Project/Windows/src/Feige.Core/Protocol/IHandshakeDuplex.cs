namespace Feige.Core.Protocol;

/// <summary>
/// The byte/message channel the handshake runs over. One <see cref="ReadAsync"/> returns exactly one complete
/// handshake message payload (the JSON body, without the <c>u32</c> length prefix).
/// </summary>
/// <remarks>
/// Implemented by <see cref="StreamHandshakeDuplex"/> for sockets and by an in-memory pair in tests. Keeping this
/// abstract is what lets the four-message exchange be tested deterministically without opening a port.
/// </remarks>
public interface IHandshakeDuplex
{
    /// <summary>Writes one already-framed message (length prefix included).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>Reads one complete message payload.</summary>
    /// <exception cref="ProtocolException">
    /// <see cref="ProtocolError.UnexpectedEndOfStream"/> when the peer closes before sending a whole message.
    /// </exception>
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default);
}
