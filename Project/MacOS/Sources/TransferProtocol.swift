import Foundation

/// Errors reserved for the future Network.framework adapter. No legacy HTTP/TLS transport lives here.
enum TransferProtocolError: LocalizedError {
    case invalidFrame
    case rejected
    case tooLarge
    case truncated
    case transportNotReady

    var errorDescription: String? {
        switch self {
        case .invalidFrame: "传输协议帧无效。"
        case .rejected: "接收方拒绝了文件。"
        case .tooLarge: "文件超过单文件大小限制。"
        case .truncated: "连接中断，文件未完成。"
        case .transportNotReady: "安全网络传输尚未启用。"
        }
    }
}
