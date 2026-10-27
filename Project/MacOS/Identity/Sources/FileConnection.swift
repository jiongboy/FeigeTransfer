import Foundation
import CryptoKit

public struct FileMetadata: Codable, Equatable, Sendable {
    public enum Kind: String, Codable, Sendable { case file, image }
    public let kind: Kind
    public let transferID: UUID
    public let fileName: String
    public let size: UInt64
    private enum CodingKeys: String, CodingKey { case transferID, fileName, size, kind }

    public init(transferID: UUID = UUID(), fileName: String, size: UInt64, kind: Kind = .file) throws {
        guard !fileName.isEmpty, fileName != ".", fileName != "..", fileName.utf8.count <= 255,
              !fileName.contains("/"), !fileName.contains("\\"),
              !fileName.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              size <= UInt64(Int64.max) else { throw TransferCoordinatorError.invalidManifest }
        self.kind = kind
        self.transferID = transferID
        self.fileName = fileName
        self.size = size
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(transferID: values.decode(UUID.self, forKey: .transferID),
                      fileName: values.decode(String.self, forKey: .fileName),
                      size: values.decode(UInt64.self, forKey: .size),
                      kind: values.decodeIfPresent(Kind.self, forKey: .kind) ?? .file)
    }
}

public enum ConnectionError: LocalizedError {
    case rejected, busy, closed
    public var errorDescription: String? {
        switch self {
        case .rejected: "对方拒绝了连接请求。"
        case .busy: "当前已有文件正在发送。"
        case .closed: "连接已断开，请重新连接。"
        }
    }
}

private struct FileDecision: Codable { let transferID: UUID; let accepted: Bool }
private struct FileDigest: Codable { let transferID: UUID; let sha256: Data }

/// One explicitly accepted v3 connection. Both directions may send files, including
/// simultaneously. Only one reader owns the socket; records are routed by transfer ID.
/// Files are hashed while streaming. No file-size or elapsed-transfer-time product cap.
public final class FileConnection: @unchecked Sendable {
    public let peerName: String
    public let peerFingerprint: String
    private static let chunkSize = SecureRecordCipher.maxPlaintextLength - 24
    private let transport: ByteTransport
    private let incoming: AuthenticatedRecordStream
    private let writer: ConnectionWriter
    private let lock = NSLock()
    private var closed = false
    private var running = false
    private var outgoingID: UUID?
    private var decision: ResponseSlot<Bool>?
    private var receipt: ResponseSlot<Data>?
    private var textReceipts: [UUID: ResponseSlot<Bool>] = [:]
    private var activeSink: TemporaryFileSink?
    private var pending: [OpenedRecord] = []

    init(session: HandshakeSession, transport: ByteTransport, peerName: String, fingerprint: String) {
        self.transport = transport
        self.peerName = peerName
        peerFingerprint = fingerprint
        incoming = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: session, direction: .receive), finishClosesStream: false)
        writer = ConnectionWriter(transport: transport, session: session)
    }

    func sendConsent(_ accepted: Bool) async throws {
        try await writer.send(type: .connectionDecision, payload: Data([accepted ? 1 : 0]))
    }

    func receiveConsent() async throws {
        while pending.isEmpty {
            let bytes = try await transport.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 5 * 60)
            guard !bytes.isEmpty else { throw ConnectionError.closed }
            pending += try incoming.append(bytes)
        }
        let record = pending.removeFirst()
        guard record.type == TransferMessageType.connectionDecision.rawValue,
              record.plaintext == Data([0]) || record.plaintext == Data([1]) else {
            throw AuthenticatedStreamError.invalidFrame
        }
        guard record.plaintext == Data([1]) else { throw ConnectionError.rejected }
    }

    public func close() {
        let state = lock.withLock { () -> (ResponseSlot<Bool>?, ResponseSlot<Data>?, TemporaryFileSink?) in
            closed = true
            let values = (decision, receipt, activeSink)
            decision = nil; receipt = nil; activeSink = nil
            return values
        }
        let texts = lock.withLock { let slots = Array(textReceipts.values); textReceipts.removeAll(); return slots }
        transport.close()
        for slot in texts { slot.resolve(.failure(ConnectionError.closed)) }
        state.0?.resolve(.failure(ConnectionError.closed))
        state.1?.resolve(.failure(ConnectionError.closed))
        state.2?.discard()
    }

    @discardableResult
    public func sendFile(_ fileURL: URL, transferID: UUID = UUID(), kind: FileMetadata.Kind = .file,
                         progress: @escaping @Sendable (UInt64, UInt64) async -> Void = { _, _ in }) async throws -> FileMetadata {
        let id = transferID
        try lock.withLock {
            guard !closed else { throw ConnectionError.closed }
            guard outgoingID == nil else { throw ConnectionError.busy }
            outgoingID = id
        }
        defer { lock.withLock { outgoingID = nil; decision = nil; receipt = nil } }
        return try await withTaskCancellationHandler {
            do {
                try Task.checkCancellation()
                let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
                guard values.isRegularFile == true, let size = values.fileSize, size >= 0 else { throw TransferSessionError.ioFailure }
                let metadata = try FileMetadata(transferID: id, fileName: fileURL.lastPathComponent, size: UInt64(size), kind: kind)
                let handle = try FileHandle(forReadingFrom: fileURL)
                defer { try? handle.close() }
                let accepted = ResponseSlot<Bool>()
                lock.withLock { decision = accepted }
                try await writer.send(type: .manifest, payload: JSONEncoder().encode(metadata))
                guard try await accepted.wait(timeout: 30) else { throw TransferSessionError.peerRejected }

                var offset: UInt64 = 0
                var hasher = SHA256()
                while offset < metadata.size {
                    try Task.checkCancellation()
                    let count = Int(min(UInt64(Self.chunkSize), metadata.size - offset))
                    guard let bytes = try handle.read(upToCount: count), !bytes.isEmpty else { throw TransferSessionError.ioFailure }
                    var payload = Self.idBytes(id)
                    var position = offset.bigEndian
                    withUnsafeBytes(of: &position) { payload.append(contentsOf: $0) }
                    payload.append(bytes)
                    try await writer.send(type: .chunk, payload: payload)
                    hasher.update(data: bytes)
                    offset += UInt64(bytes.count)
                    await progress(offset, metadata.size)
                }
                let digest = Data(hasher.finalize())
                let acknowledged = ResponseSlot<Data>()
                lock.withLock { receipt = acknowledged }
                try await writer.send(type: .finish, payload: JSONEncoder().encode(FileDigest(transferID: id, sha256: digest)))
                guard try await acknowledged.wait(timeout: 5 * 60) == digest else { throw TransferCoordinatorError.checksumMismatch }
                return metadata
            } catch {
                close()
                throw error
            }
        } onCancel: { self.close() }
    }

    /// Text shares the encrypted writer with file chunks, so chat remains available during transfers.
    @discardableResult
    public func sendText(_ text: String, id: UUID = UUID()) async throws -> ChatText {
        let message = try ChatText(id: id, text: text)
        let payload = try JSONEncoder().encode(message)
        guard payload.count <= SecureRecordCipher.maxPlaintextLength else { throw ChatError.invalidText }
        let slot = ResponseSlot<Bool>()
        try lock.withLock {
            guard !closed else { throw ConnectionError.closed }
            guard textReceipts.count < 32, textReceipts[id] == nil else { throw ConnectionError.busy }
            textReceipts[id] = slot
        }
        defer { _ = lock.withLock { textReceipts.removeValue(forKey: id) } }
        do {
            try await writer.send(type: .text, payload: payload)
            _ = try await slot.wait(timeout: 30)
            return message
        } catch { close(); throw error }
    }

    /// Call once after consent. The connection stays open between files until disconnected.
    public func run(receiveDirectory: URL,
                    directoryProvider: (@Sendable () async -> URL)? = nil,
                    didReceive: @escaping @Sendable (FileMetadata, URL) async -> Void,
                    progress: @escaping @Sendable (FileMetadata, UInt64) async -> Void = { _, _ in },
                    didReceiveText: @escaping @Sendable (ChatText) async throws -> Void = { _ in }) async throws {
        try lock.withLock {
            guard !closed else { throw ConnectionError.closed }
            guard !running else { throw ConnectionError.busy }
            running = true
        }
        defer { close() }
        try await withTaskCancellationHandler {
            var file: IncomingFile?
            while true {
                try Task.checkCancellation()
                guard !lock.withLock({ closed }) else { throw ConnectionError.closed }
                if pending.isEmpty {
                    let bytes = try await transport.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength,
                                                            timeout: file == nil ? .infinity : 15)
                    guard !bytes.isEmpty else { throw ConnectionError.closed }
                    pending = try incoming.append(bytes)
                }
                let records = pending
                pending.removeAll(keepingCapacity: true)
                for record in records {
                    switch record.type {
                    case TransferMessageType.manifest.rawValue:
                        guard file == nil else { throw TransferCoordinatorError.wrongMessage }
                        let metadata = try JSONDecoder().decode(FileMetadata.self, from: record.plaintext)
                        let directory = await directoryProvider?() ?? receiveDirectory
                        let sink = try lock.withLock {
                            guard !closed else { throw ConnectionError.closed }
                            let sink = try TemporaryFileSink(directoryURL: directory, metadata: metadata)
                            activeSink = sink
                            return sink
                        }
                        file = IncomingFile(metadata: metadata, sink: sink)
                        try await writer.send(type: .decision, payload: JSONEncoder().encode(FileDecision(transferID: metadata.transferID, accepted: true)))
                        await progress(metadata, 0)

                    case TransferMessageType.decision.rawValue:
                        let value = try JSONDecoder().decode(FileDecision.self, from: record.plaintext)
                        let slot: ResponseSlot<Bool> = try lock.withLock {
                            guard value.transferID == outgoingID, let slot = decision else { throw TransferCoordinatorError.wrongMessage }
                            decision = nil
                            return slot
                        }
                        slot.resolve(.success(value.accepted))

                    case TransferMessageType.chunk.rawValue:
                        guard let file, record.plaintext.count > 24,
                              record.plaintext.count <= SecureRecordCipher.maxPlaintextLength else { throw TransferCoordinatorError.wrongMessage }
                        let id = Self.readID(record.plaintext)
                        let offset = record.plaintext[16..<24].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
                        let bytes = Data(record.plaintext.dropFirst(24))
                        guard id == file.metadata.transferID, offset == file.received,
                              UInt64(bytes.count) <= file.metadata.size - file.received else { throw TransferCoordinatorError.invalidManifest }
                        try file.sink.write(bytes)
                        file.hasher.update(data: bytes)
                        file.received += UInt64(bytes.count)
                        await progress(file.metadata, file.received)

                    case TransferMessageType.finish.rawValue:
                        let value = try JSONDecoder().decode(FileDigest.self, from: record.plaintext)
                        guard let complete = file, value.transferID == complete.metadata.transferID,
                              complete.received == complete.metadata.size, value.sha256.count == 32 else { throw TransferCoordinatorError.interrupted }
                        guard Data(complete.hasher.finalize()) == value.sha256 else { throw TransferCoordinatorError.checksumMismatch }
                        try complete.sink.commit()
                        guard let url = complete.sink.savedURL else { throw TransferSessionError.ioFailure }
                        file = nil
                        lock.withLock { activeSink = nil }
                        try await writer.send(type: .receipt, payload: JSONEncoder().encode(value))
                        await didReceive(complete.metadata, url)

                    case TransferMessageType.receipt.rawValue:
                        let value = try JSONDecoder().decode(FileDigest.self, from: record.plaintext)
                        let slot: ResponseSlot<Data> = try lock.withLock {
                            guard value.sha256.count == 32, value.transferID == outgoingID,
                                  let slot = receipt else { throw TransferCoordinatorError.wrongMessage }
                            receipt = nil
                            return slot
                        }
                        slot.resolve(.success(value.sha256))

                    case TransferMessageType.text.rawValue:
                        let message = try JSONDecoder().decode(ChatText.self, from: record.plaintext)
                        try await didReceiveText(message)
                        try await writer.send(type: .textReceipt, payload: JSONEncoder().encode(message.id))

                    case TransferMessageType.textReceipt.rawValue:
                        let id = try JSONDecoder().decode(UUID.self, from: record.plaintext)
                        let slot: ResponseSlot<Bool> = try lock.withLock {
                            guard let slot = textReceipts.removeValue(forKey: id) else { throw TransferCoordinatorError.wrongMessage }
                            return slot
                        }
                        slot.resolve(.success(true))

                    default: throw TransferSessionError.unexpectedRecord(record.type)
                    }
                }
            }
        } onCancel: { self.close() }
    }

    private static func idBytes(_ id: UUID) -> Data {
        var bytes = id.uuid
        return withUnsafeBytes(of: &bytes) { Data($0) }
    }

    private static func readID(_ data: Data) -> UUID {
        var bytes: uuid_t = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        _ = withUnsafeMutableBytes(of: &bytes) { data.copyBytes(to: $0, from: 0..<16) }
        return UUID(uuid: bytes)
    }
}

private final class IncomingFile {
    let metadata: FileMetadata
    let sink: TemporaryFileSink
    var received: UInt64 = 0
    var hasher = SHA256()
    init(metadata: FileMetadata, sink: TemporaryFileSink) { self.metadata = metadata; self.sink = sink }
}

/// Serialize encoding AND actual writes across simultaneous sends and receive receipts.
private final class ConnectionWriter: @unchecked Sendable {
    private let lock = NSLock()
    private let stream: AuthenticatedRecordStream
    private let transport: ByteTransport
    private var tail: Task<Void, Error>?
    init(transport: ByteTransport, session: HandshakeSession) {
        self.transport = transport
        stream = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: session, direction: .send), finishClosesStream: false)
    }
    func send(type: TransferMessageType, payload: Data) async throws {
        let job: Task<Void, Error> = try lock.withLock {
            let frame = try stream.encode(type: type.rawValue, payload: payload)
            let previous = tail
            let next = Task { [transport] in
                if let previous { try await previous.value }
                try Task.checkCancellation()
                try await transport.send(frame, timeout: 15)
            }
            tail = next
            return next
        }
        try await withTaskCancellationHandler { try await job.value } onCancel: { job.cancel(); self.transport.close() }
    }
}

private final class ResponseSlot<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<Value, Error>?
    private var continuation: CheckedContinuation<Value, Error>?
    func resolve(_ value: Result<Value, Error>) {
        let waiter = lock.withLock { () -> CheckedContinuation<Value, Error>? in
            guard result == nil else { return nil }
            result = value
            let waiter = continuation
            continuation = nil
            return waiter
        }
        waiter?.resume(with: value)
    }
    func wait(timeout: TimeInterval) async throws -> Value {
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            let timer = Task {
                do { try await Task.sleep(for: .seconds(timeout)) } catch { return }
                self.resolve(.failure(AuthenticatedStreamError.timeout))
            }
            defer { timer.cancel() }
            return try await withCheckedThrowingContinuation { waiter in
                let immediate = lock.withLock { () -> Result<Value, Error>? in
                    if let result { return result }
                    continuation = waiter
                    return nil
                }
                if let immediate { waiter.resume(with: immediate) }
            }
        } onCancel: { self.resolve(.failure(CancellationError())) }
    }
}
