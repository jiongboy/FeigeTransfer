using System.Net.Sockets;
using Feige.Core.Protocol;

namespace Feige.App.Services;

/// <summary>Turns protocol and socket failures into something a user can act on.</summary>
public static class ErrorText
{
    /// <summary>Short Chinese description of <paramref name="exception"/>.</summary>
    public static string Describe(Exception exception)
    {
        if (exception is ProtocolException protocol)
        {
            return protocol.Error switch
            {
                ProtocolError.PairingCodeMismatch => "配对码不一致，请核对对端屏幕上显示的 6 位数字。",
                ProtocolError.UserRejected => "已取消本次连接。",
                ProtocolError.PeerRejected => "对方拒绝接收这个文件。",
                ProtocolError.Timeout => "连接超时，请确认两台设备在同一网络且防火墙允许。",
                ProtocolError.SignatureFailed or ProtocolError.InvalidTranscript or ProtocolError.FingerprintMismatch
                    => "对端身份校验失败，连接可能被篡改，已中止。",
                ProtocolError.ChecksumMismatch => "文件校验失败，已删除未完成的文件。",
                ProtocolError.SizeMismatch => "接收到的数据长度与声明不符，已中止。",
                ProtocolError.UnexpectedEndOfStream => "连接意外中断，传输未完成。",
                ProtocolError.StorageFailure => "无法写入接收文件夹，请检查磁盘空间与权限。",
                ProtocolError.InvalidManifest => "对端发来的文件信息不合法，已拒绝。",
                ProtocolError.InvalidOffer or ProtocolError.InvalidHandshakeFrame => "对端协议数据不合法，已断开。",
                _ => "协议错误：" + protocol.Error,
            };
        }

        return exception switch
        {
            TimeoutException => "连接或回执等待超时，请重连后重试。",
            IOException when exception.Message == "等待接收确认超时。" => exception.Message,
            IOException when exception.Message == "已拒绝接收文件。" => exception.Message,
            IOException when exception.Message.StartsWith("连接已断开", StringComparison.Ordinal) => exception.Message,
            System.Security.Cryptography.CryptographicException => "安全校验失败，已断开连接。",
            System.Text.Json.JsonException => "对端消息格式或本机记录格式无效。",
            InvalidDataException => exception.Message,
            SocketException socket => socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝。请确认对方的飞鸽快传正在运行，并使用设置中当前显示的端口。",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "无法到达对方网络。请检查 VPN/TUN 分流、路由器之间的路由及访客网络隔离；多层 NAT 可能阻挡此方向的连接。",
                SocketError.TimedOut => "直连超时。请核对对方当前地址和端口，并检查防火墙、VPN/TUN 与路由器隔离。",
                SocketError.AccessDenied => "系统未允许此网络操作，请检查应用的防火墙许可或单位网络策略。",
                SocketError.AddressNotAvailable => "当前网络地址已变化或不可用，请刷新设备并重新获取对方地址。",
                _ => "无法连接到对端，请检查地址、端口、路由与防火墙，可在连接帮助中查看代理和多层路由说明。",
            },
            OperationCanceledException => "已取消。",
            ObjectDisposedException => "连接已关闭。",
            UnauthorizedAccessException => "没有权限访问该文件或文件夹。",
            IOException => "网络或磁盘读写失败：" + exception.Message,
            _ => exception.Message,
        };
    }
}
