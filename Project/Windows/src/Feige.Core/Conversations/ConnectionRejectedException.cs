namespace Feige.Core.Conversations;

/// <summary>The peer completed the handshake and declined the connection request.</summary>
public sealed class ConnectionRejectedException() : InvalidOperationException("对方拒绝了连接。");
