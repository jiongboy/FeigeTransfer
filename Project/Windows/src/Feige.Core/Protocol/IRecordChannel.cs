namespace Feige.Core.Protocol;

/// <summary>
/// One authenticated record channel for a single transfer (§5.1). The sender and receiver state machines talk only
/// through this interface, so they can be driven by a socket, an in-memory pipe or a test double.
/// </summary>
public interface IRecordChannel
{
    /// <summary>Encrypts and writes one record.</summary>
    ValueTask WriteAsync(TransferMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Reads the next record, blocking until one is complete.</summary>
    ValueTask<OpenedRecord> ReadAsync(CancellationToken cancellationToken = default);
}
