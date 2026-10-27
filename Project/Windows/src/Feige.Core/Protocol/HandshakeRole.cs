namespace Feige.Core.Protocol;

/// <summary>
/// Handshake role carried in <c>offer.role</c> (通信协议 §3.3).
/// It only decides key direction and transcript order; either side may send files afterwards.
/// </summary>
public enum HandshakeRole : byte
{
    /// <summary>Role value 1.</summary>
    Initiator = 1,

    /// <summary>Role value 2.</summary>
    Responder = 2,
}
