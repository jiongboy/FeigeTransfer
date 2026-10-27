import Foundation
import CryptoKit

public enum TransferSessionError: LocalizedError {
    case peerRejected, unexpectedRecord(UInt8), tooLarge, ioFailure, interrupted, alreadyUsed
    public var errorDescription: String? {
        switch self {
        case .peerRejected: "对端拒绝了本次传输。"
        case .unexpectedRecord(let type): "收到预期之外的传输记录（类型 \(type)）。"
        case .tooLarge: "文件大小无效或不受文件系统支持。"
        case .ioFailure: "读取或写入本地文件失败。"
        case .interrupted: "传输在完成前被中断。"
        case .alreadyUsed: "每条连接只能传输一个文件，请重新连接。"
        }
    }
}

/// Orchestrates one file transfer over an already authenticated session: it wires the
/// directional AES-GCM record streams to the `TransferCoordinator` state machine and drives
/// the manifest → decision → chunk → finish → receipt exchange. No socket is opened here;
/// the caller supplies a `ByteTransport` (Network.framework adapter in production, in-memory
/// duplex pipe in tests). The receiver's approval and file sink are injected by the caller
/// so the UI and filesystem stay outside this type.
public final class TransferSession: @unchecked Sendable {
    private static let chunkSize = SecureRecordCipher.maxPlaintextLength - 128
    private let transport: ByteTransport
    private let sendStream: AuthenticatedRecordStream
    private let receiveStream: AuthenticatedRecordStream
    private let coordinator = TransferCoordinator()
    private var pending: [OpenedRecord] = []
    private let lock = NSLock()
    private let timeout: TimeInterval
    private let approvalTimeout: TimeInterval
    private var used = false

    private func begin() throws {
        try lock.withLock {
            guard !used else { throw TransferSessionError.alreadyUsed }
            used = true
        }
    }

    public func cancel() { transport.close() }

    public init(session: HandshakeSession, transport: ByteTransport, timeout: TimeInterval = 15,
                approvalTimeout: TimeInterval = 10 * 60) {
        self.transport = transport
        self.sendStream = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: session, direction: .send))
        self.receiveStream = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: session, direction: .receive))
        self.timeout = timeout
        self.approvalTimeout = approvalTimeout
    }

    // MARK: - Sender

    /// Send one file. Returns after the peer's receipt record confirms digest verification.
    public func send(fileURL: URL, progress: (@Sendable (UInt64, UInt64) -> Void)? = nil) async throws {
        try begin()
        defer { transport.close() }
        try Task.checkCancellation()
        let size: Int
        let digest: Data
        do {
            let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
            guard values.isRegularFile == true, let fileSize = values.fileSize else {
                throw TransferSessionError.ioFailure
            }
            size = fileSize
            guard size > 0, UInt64(size) <= FileManifest.maxSize else { throw TransferSessionError.tooLarge }
            digest = try Self.sha256(of: fileURL)
        } catch let error as TransferSessionError { throw error }
        catch { throw TransferSessionError.ioFailure }
        guard size > 0, UInt64(size) <= FileManifest.maxSize else { throw TransferSessionError.tooLarge }

        let manifest = try FileManifest(fileName: fileURL.lastPathComponent, size: UInt64(size), sha256: digest)
        try await sendRecord(type: .manifest, payload: try JSONEncoder().encode(manifest))

        let decision = try await nextRecord(ofTypes: [TransferMessageType.decision.rawValue], wait: approvalTimeout)
        guard decision.plaintext == Data([0]) || decision.plaintext == Data([1]) else {
            throw TransferCoordinatorError.invalidDecision
        }
        guard decision.plaintext == Data([1]) else { throw TransferSessionError.peerRejected }

        let handle = try FileHandle(forReadingFrom: fileURL)
        defer { try? handle.close() }
        var offset: UInt64 = 0
        while offset < manifest.size {
            try Task.checkCancellation()
            let remaining = Int(manifest.size - offset)
            let count = min(Self.chunkSize, remaining)
            guard let chunk = try handle.read(upToCount: count), !chunk.isEmpty else {
                throw TransferSessionError.ioFailure
            }
            try await sendRecord(type: .chunk, payload: chunk)
            offset += UInt64(chunk.count)
            progress?(offset, manifest.size)
        }
        try await sendRecord(type: .finish, payload: Data())
        let receipt = try await nextRecord(ofTypes: [TransferMessageType.receipt.rawValue])
        guard receipt.plaintext == Data([1]) else { throw TransferCoordinatorError.invalidDecision }
    }

    // MARK: - Receiver

    /// Receive one file. `approve` is awaited after an authenticated manifest arrives and
    /// before any byte is written to `sink`; returning false rejects the transfer. `sink`
    /// receives verified-size chunks only while transferring. The returned manifest has
    /// passed SHA-256 verification.
    public func receive(approve: @escaping @Sendable (FileManifest) async -> Bool,
                         prepare: @escaping @Sendable (FileManifest) throws -> Void = { _ in },
                         sink: @escaping @Sendable (Data) throws -> Void,
                         finalize: @escaping @Sendable () throws -> Void = {},
                         progress: (@Sendable (UInt64, UInt64) -> Void)? = nil) async throws -> FileManifest {
        try begin()
        defer { transport.close() }
        let manifestRecord = try await nextRecord(ofTypes: [TransferMessageType.manifest.rawValue])
        let manifest = try JSONDecoder().decode(FileManifest.self, from: manifestRecord.plaintext)
        _ = try coordinator.receiveManifest(manifest)

        guard await approve(manifest) else {
            _ = try? coordinator.reject(transferID: manifest.transferID)
            try? await sendRecord(type: .decision, payload: Data([0]))
            throw TransferSessionError.peerRejected
        }
        try prepare(manifest)
        _ = try coordinator.approve(transferID: manifest.transferID, sink: sink)
        try await sendRecord(type: .decision, payload: Data([1]))

        var received: UInt64 = 0
        while received < manifest.size {
            try Task.checkCancellation()
            let record = try await nextRecord(ofTypes: [TransferMessageType.chunk.rawValue,
                                                        TransferMessageType.finish.rawValue])
            guard record.type == TransferMessageType.chunk.rawValue else { throw TransferSessionError.interrupted }
            _ = try coordinator.receiveChunk(offset: received, payload: record.plaintext)
            received += UInt64(record.plaintext.count)
            progress?(received, manifest.size)
        }
        let finish = try await nextRecord(ofTypes: [TransferMessageType.finish.rawValue])
        guard finish.plaintext.isEmpty else { throw AuthenticatedStreamError.invalidFrame }
        _ = try coordinator.finish()
        try finalize()
        try await sendRecord(type: .receipt, payload: Data([1]))
        return manifest
    }

    // MARK: - Record plumbing

    private func sendRecord(type: TransferMessageType, payload: Data) async throws {
        let frame = try sendStream.encode(type: type.rawValue, payload: payload)
        try await transport.send(frame, timeout: timeout)
    }

    /// Read records until one of the desired types is available; extras are buffered so a
    /// coalesced TCP read that carries several records does not drop any.
    private func nextRecord(ofTypes types: Set<UInt8>, wait: TimeInterval? = nil) async throws -> OpenedRecord {
        while true {
            try Task.checkCancellation()
            let buffered: OpenedRecord? = lock.withLock {
                pending.isEmpty ? nil : pending.removeFirst()
            }
            if let buffered {
                guard types.contains(buffered.type) else { throw TransferSessionError.unexpectedRecord(buffered.type) }
                return buffered
            }
            let bytes = try await transport.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength,
                                                     timeout: wait ?? timeout)
            let records = try receiveStream.append(bytes)
            // Read only when pending is empty; the codec's byte cap bounds this batch.
            lock.withLock { pending.append(contentsOf: records) }
        }
    }

    private static func sha256(of url: URL) throws -> Data {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let block = try handle.read(upToCount: 64 * 1024), !block.isEmpty {
            try Task.checkCancellation()
            hasher.update(data: block)
        }
        return Data(hasher.finalize())
    }
}
