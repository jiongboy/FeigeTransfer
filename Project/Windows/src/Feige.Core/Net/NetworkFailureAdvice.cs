using System.Net.Sockets;
using Feige.Core.Protocol;

namespace Feige.Core.Net;

/// <summary>Suggest checks for a failed connection without claiming that a proxy has been detected.</summary>
public static class NetworkFailureAdvice
{
    public const string Message = "若正在使用 VPN、TUN 或透明代理，请检查“绕过局域网 / 局域网直连”分流规则，并确认对方的真实局域网地址、当前端口及防火墙许可。仅凭此次失败无法确认是否由代理造成。";

    public static bool IsApplicable(Exception error) => error switch
    {
        OperationCanceledException => false,
        SocketException socket => socket.SocketErrorCode is SocketError.TimedOut or SocketError.NetworkUnreachable
            or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.NetworkReset
            or SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.AccessDenied or SocketError.AddressNotAvailable,
        TimeoutException or EndOfStreamException => true,
        ProtocolException protocol => protocol.Error is ProtocolError.Timeout or ProtocolError.UnexpectedEndOfStream,
        IOException { InnerException: SocketException socket } => IsApplicable(socket),
        _ => false,
    };
}
