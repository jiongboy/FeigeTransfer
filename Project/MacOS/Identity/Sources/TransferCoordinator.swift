import Foundation
import CryptoKit

public enum TransferMessageType: UInt8, Codable {
    case manifest = 1
    case decision = 2
    case chunk = 3
    case receipt = 4
    case cancel = 5
    case error = 6
    case finish = 7
    case connectionDecision = 8
    case text = 9
    case textReceipt = 10
}

public struct FileManifest: Codable, Equatable, Sendable {
    /// Filesystem offsets use signed 64-bit integers; there is no product size cap.
    public static let maxSize: UInt64 = UInt64(Int64.max)
    public let transferID: UUID
    public let fileName: String
    public let size: UInt64
    public let sha256: Data

    private enum CodingKeys: String, CodingKey { case transferID, fileName, size, sha256 }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(transferID: values.decode(UUID.self, forKey: .transferID),
                      fileName: values.decode(String.self, forKey: .fileName),
                      size: values.decode(UInt64.self, forKey: .size),
                      sha256: values.decode(Data.self, forKey: .sha256))
    }

    public init(transferID: UUID = UUID(), fileName: String, size: UInt64, sha256: Data) throws {
        let unsafe = fileName.isEmpty || fileName == "." || fileName == ".." ||
            fileName.contains("/") || fileName.contains("\\") ||
            fileName.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
        guard !unsafe, fileName.utf8.count <= 255, size > 0, size <= Self.maxSize, sha256.count == 32 else {
            throw TransferCoordinatorError.invalidManifest
        }
        self.transferID = transferID
        self.fileName = fileName
        self.size = size
        self.sha256 = sha256
    }
}

public enum TransferCoordinatorError: LocalizedError {
    case invalidManifest, wrongMessage, invalidDecision, interrupted, checksumMismatch, alreadyFinished, sinkFailure
    /// The receiver explicitly declined the file, so the connection closes without a false success.
    case rejectedByReceiver
    public var errorDescription: String? {
        switch self {
        case .invalidManifest: "文件清单无效或超出限制。"
        case .wrongMessage: "传输消息顺序或类型无效。"
        case .invalidDecision: "接收方决策消息无效。"
        case .interrupted: "文件传输未完成。"
        case .checksumMismatch: "文件完整性校验失败。"
        case .alreadyFinished: "此传输会话已结束。"
        case .sinkFailure: "无法写入接收暂存文件。"
        case .rejectedByReceiver: "已拒绝接收该文件。"
        }
    }
}

public struct TransferEvent: Equatable {
    public enum Kind: Equatable { case manifest(FileManifest), progress(UInt64, UInt64), completed(String), rejected, failed(String) }
    public let kind: Kind
}

/// Single transfer receiver. No network or filesystem path is chosen here: caller supplies
/// a temporary sink only after approval; caller atomically promotes it after digest verification.
public final class TransferCoordinator {
    public enum State: Equatable { case idle, awaitingApproval, transferring, verifying, completed, rejected, failed }
    public private(set) var state: State = .idle
    public private(set) var manifest: FileManifest?
    private var received: UInt64 = 0
    private var hasher = SHA256()
    private var sink: ((Data) throws -> Void)?
    private let chunkLimit = SecureRecordCipher.maxPlaintextLength - 128

    public init() {}

    public func receiveManifest(_ value: FileManifest) throws -> TransferEvent {
        guard state == .idle else { throw TransferCoordinatorError.alreadyFinished }
        manifest = value
        state = .awaitingApproval
        return TransferEvent(kind: .manifest(value))
    }

    /// Approval must be issued by the UI after showing authenticated sender identity and manifest.
    public func approve(transferID: UUID, sink: @escaping (Data) throws -> Void) throws -> TransferEvent {
        guard state == .awaitingApproval, manifest?.transferID == transferID else { throw TransferCoordinatorError.invalidDecision }
        self.sink = sink
        state = .transferring
        return TransferEvent(kind: .progress(0, manifest?.size ?? 0))
    }

    public func reject(transferID: UUID) throws -> TransferEvent {
        guard state == .awaitingApproval, manifest?.transferID == transferID else { throw TransferCoordinatorError.invalidDecision }
        state = .rejected
        return TransferEvent(kind: .rejected)
    }

    public func receiveChunk(offset: UInt64, payload: Data) throws -> TransferEvent {
        guard state == .transferring, let manifest, let sink else { throw TransferCoordinatorError.wrongMessage }
        guard offset == received, !payload.isEmpty, payload.count <= chunkLimit,
              received <= manifest.size, UInt64(payload.count) <= manifest.size - received else {
            state = .failed
            self.sink = nil
            throw TransferCoordinatorError.invalidManifest
        }
        do { try sink(payload) }
        catch {
            state = .failed
            self.sink = nil
            throw TransferCoordinatorError.sinkFailure
        }
        hasher.update(data: payload)
        received += UInt64(payload.count)
        if received == manifest.size { state = .verifying }
        return TransferEvent(kind: .progress(received, manifest.size))
    }

    /// Call only after the authenticated finish record. Digest must match before promotion.
    public func finish() throws -> TransferEvent {
        guard state == .verifying, let manifest, received == manifest.size else {
            state = .failed
            sink = nil
            throw TransferCoordinatorError.interrupted
        }
        let digest = Data(hasher.finalize())
        guard Self.constantTimeEqual(digest, manifest.sha256) else {
            state = .failed
            sink = nil
            throw TransferCoordinatorError.checksumMismatch
        }
        state = .completed
        sink = nil
        return TransferEvent(kind: .completed(manifest.fileName))
    }

    public func cancel() {
        guard state != .completed, state != .rejected, state != .failed else { return }
        sink = nil
        state = .failed
    }

    private static func constantTimeEqual(_ lhs: Data, _ rhs: Data) -> Bool {
        guard lhs.count == rhs.count else { return false }
        var difference: UInt8 = 0
        for (a, b) in zip(lhs, rhs) { difference |= a ^ b }
        return difference == 0
    }
}
