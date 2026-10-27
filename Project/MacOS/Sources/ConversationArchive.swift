import Foundation

struct ConversationArchive: Codable {
    static let currentVersion = 1

    var version: Int
    var messages: [ChatMessage]
    var hiddenFingerprints: Set<String>

    init(messages: [ChatMessage] = [], hiddenFingerprints: Set<String> = []) {
        version = Self.currentVersion
        self.messages = messages
        self.hiddenFingerprints = hiddenFingerprints
    }

    static func decode(_ data: Data) throws -> ConversationArchive {
        let decoder = JSONDecoder()
        if let legacyMessages = try? decoder.decode([ChatMessage].self, from: data) {
            return ConversationArchive(messages: legacyMessages)
        }
        let archive = try decoder.decode(ConversationArchive.self, from: data)
        guard archive.version == currentVersion else { throw ArchiveError.unsupportedVersion(archive.version) }
        return archive
    }

    enum ArchiveError: LocalizedError {
        case unsupportedVersion(Int)
        var errorDescription: String? {
            switch self {
            case .unsupportedVersion(let version): "会话记录版本 \(version) 不受支持，原文件已保留。"
            }
        }
    }
}
