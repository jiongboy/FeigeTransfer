import Foundation

public enum ChatError: LocalizedError {
    case invalidText
    public var errorDescription: String? { "消息不能为空，且单条文字不能超过 8 KB；长文可作为文件发送。" }
}

public struct ChatText: Codable, Equatable, Sendable {
    public static let maxUTF8Bytes = 8 * 1024
    public let id: UUID
    public let text: String
    private enum CodingKeys: String, CodingKey { case id, text }

    public init(id: UUID = UUID(), text: String) throws {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              text.utf8.count <= Self.maxUTF8Bytes else { throw ChatError.invalidText }
        self.id = id
        self.text = text
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(id: values.decode(UUID.self, forKey: .id), text: values.decode(String.self, forKey: .text))
    }
}
