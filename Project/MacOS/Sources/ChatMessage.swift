import Foundation

struct ChatMessage: Identifiable, Codable, Equatable {
    enum Kind: String, Codable { case text, image, file }
    /// `awaitingApproval` is the receiver-side state before the user accepts or declines a file.
    enum Status: String, Codable { case queued, awaitingApproval, sending, delivered, received, declined, failed }
    var id = UUID()
    var peerID: String
    var peerName: String
    var direction: TransferRecord.Direction
    var kind: Kind
    var date = Date()
    var text: String?
    var fileName: String?
    var size: Int64?
    var localPath: String?
    var status: Status
    var progress: Double = 0
    var failureReason: String? = nil

    var rowID: String { "\(peerID):\(direction.rawValue):\(kind.rawValue):\(id)" }
    var isPending: Bool { status == .queued || status == .awaitingApproval || status == .sending }
    var preview: String {
        switch kind {
        case .text: text ?? "文字消息"
        case .image: "[图片] \(fileName ?? "")"
        case .file: "[文件] \(fileName ?? "")"
        }
    }
}

struct ChatPeer: Identifiable {
    let id: String
    let name: String
    let preview: String
    let date: Date
    var serviceName: String { "Feige-" + id.replacingOccurrences(of: " ", with: "").prefix(20) }
}
