namespace Feige.Core.Protocol;

/// <summary>
/// Record type byte carried in the 13-byte record header (通信协议 §4.3).
/// Values 5 and 6 are reserved and must not be sent by v1 peers.
/// </summary>
public enum TransferMessageType : byte
{
    /// <summary>Sender → receiver, UTF-8 JSON manifest (§4.4).</summary>
    Manifest = 1,

    /// <summary>Receiver → sender, one byte: 0x00 reject, 0x01 accept.</summary>
    Decision = 2,

    /// <summary>Sender → receiver, raw file bytes, 1..65408 per record (§4.5).</summary>
    Chunk = 3,

    /// <summary>Receiver → sender, one byte 0x01 after the SHA-256 check passed.</summary>
    Receipt = 4,

    /// <summary>Reserved for v1; must not be sent.</summary>
    Cancel = 5,

    /// <summary>Reserved for v1; must not be sent.</summary>
    Error = 6,

    /// <summary>Sender → receiver, empty payload, no chunk follows.</summary>
    Finish = 7,
}

/// <summary>Type-byte helpers shared by the record layer and the transfer state machine.</summary>
public static class TransferMessageTypes
{
    /// <summary>The lowest type value defined by v1.</summary>
    public const byte MinValue = (byte)TransferMessageType.Manifest;

    /// <summary>The highest type value defined by v1.</summary>
    public const byte MaxValue = (byte)TransferMessageType.Finish;

    /// <summary>Returns true for 1..7; anything else must be rejected before decrypting.</summary>
    public static bool IsDefined(byte type) => type is >= MinValue and <= MaxValue;

    /// <summary>
    /// Returns true for the reserved types 5 and 6, which are known but must never appear on the wire in v1.
    /// </summary>
    public static bool IsReserved(byte type) => type is (byte)TransferMessageType.Cancel or (byte)TransferMessageType.Error;
}
