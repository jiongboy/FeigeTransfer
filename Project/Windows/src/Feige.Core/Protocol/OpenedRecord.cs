namespace Feige.Core.Protocol;

/// <summary>
/// A record that passed authentication, as produced by <see cref="Crypto.RecordCipher.Open"/>.
/// </summary>
/// <param name="Type">Record type byte, always within 1..7.</param>
/// <param name="Sequence">Sequence number carried in the header, equal to the value the cipher expected.</param>
/// <param name="Payload">Authenticated plaintext; empty only for <see cref="TransferMessageType.Finish"/>.</param>
public sealed record OpenedRecord(TransferMessageType Type, ulong Sequence, byte[] Payload);
