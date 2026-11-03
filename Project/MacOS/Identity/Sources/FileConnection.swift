import Foundation
import CryptoKit

public struct FileMetadata: Codable, Equatable, Sendable {
    public enum Kind: String, Codable, Sendable { case file, image }
    public let kind: Kind
    public let transferID: UUID
    public let fileName: String
    public let size: UInt64
    /// v6 only: stable while the sender's file is unchanged; enables resuming a partial file.
    public let resumeID: UUID?
    /// v6 only: present on every file of a folder send.
    public let folder: FolderInfo?
    private enum CodingKeys: String, CodingKey { case transferID, fileName, size, kind, resumeID, folder }

    public init(transferID: UUID = UUID(), fileName: String, size: UInt64, kind: Kind = .file,
                resumeID: UUID? = nil, folder: FolderInfo? = nil) throws {
        guard !fileName.isEmpty, fileName != ".", fileName != "..", fileName.utf8.count <= 255,
              !fileName.contains("/"), !fileName.contains("\\"),
              !fileName.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              size <= UInt64(Int64.max), folder.map({ $0.components.last == fileName && size <= $0.totalBytes }) ?? true
        else { throw TransferCoordinatorError.invalidManifest }
        self.kind = kind
        self.transferID = transferID
        self.fileName = fileName
        self.size = size
        self.resumeID = resumeID
        self.folder = folder
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(transferID: values.decode(UUID.self, forKey: .transferID),
                      fileName: values.decode(String.self, forKey: .fileName),
                      size: values.decode(UInt64.self, forKey: .size),
                      kind: values.decodeIfPresent(Kind.self, forKey: .kind) ?? .file,
                      resumeID: values.decodeIfPresent(UUID.self, forKey: .resumeID),
                      folder: values.decodeIfPresent(FolderInfo.self, forKey: .folder))
    }
}

public struct GroupFileManifestWire: Codable, Equatable, Sendable {
    public let groupID: UUID
    public let messageID: UUID
    public let senderFingerprint: String
    public let memberRevision: UInt64
    public let metadata: FileMetadata
    public let sha256: Data

    public init(groupID: UUID, messageID: UUID, senderFingerprint: String, memberRevision: UInt64,
                metadata: FileMetadata, sha256: Data) throws {
        guard !senderFingerprint.isEmpty, senderFingerprint.utf8.count <= 96, sha256.count == 32 else {
            throw TransferCoordinatorError.invalidManifest
        }
        self.groupID = groupID
        self.messageID = messageID
        self.senderFingerprint = senderFingerprint
        self.memberRevision = memberRevision
        self.metadata = metadata
        self.sha256 = sha256
    }

    public static func decodeStrict(_ data: Data) throws -> Self {
        try validateWireObject(data, allowed: ["groupID", "messageID", "senderFingerprint", "memberRevision", "metadata", "sha256"],
                               required: ["groupID", "messageID", "senderFingerprint", "memberRevision", "metadata", "sha256"])
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let metadata = root["metadata"] as? [String: Any] else { throw TransferCoordinatorError.invalidManifest }
        try validateWireObject(metadata, allowed: ["transferID", "fileName", "size", "kind"],
                               required: ["transferID", "fileName", "size", "kind"])
        let decoded = try JSONDecoder().decode(Self.self, from: data)
        return try Self(groupID: decoded.groupID, messageID: decoded.messageID,
                        senderFingerprint: decoded.senderFingerprint, memberRevision: decoded.memberRevision,
                        metadata: decoded.metadata, sha256: decoded.sha256)
    }
}

public struct GroupFileDecisionWire: Codable, Equatable, Sendable {
    public let groupID: UUID
    public let transferID: UUID
    public let accepted: Bool
    public let reason: String?
    public init(groupID: UUID, transferID: UUID, accepted: Bool, reason: String? = nil) {
        self.groupID = groupID
        self.transferID = transferID
        self.accepted = accepted
        self.reason = reason
    }

    public static func decodeStrict(_ data: Data) throws -> Self {
        try validateWireObject(data, allowed: ["groupID", "transferID", "accepted", "reason"],
                               required: ["groupID", "transferID", "accepted"])
        let decoded = try JSONDecoder().decode(Self.self, from: data)
        guard decoded.accepted ? decoded.reason == nil : !(decoded.reason?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true),
              (decoded.reason?.utf8.count ?? 0) <= 256 else { throw TransferCoordinatorError.invalidManifest }
        return decoded
    }
}

public struct GroupFileReceiptWire: Codable, Equatable, Sendable {
    public let groupID: UUID
    public let transferID: UUID
    public let sha256: Data
    public init(groupID: UUID, transferID: UUID, sha256: Data) throws {
        guard sha256.count == 32 else { throw TransferCoordinatorError.checksumMismatch }
        self.groupID = groupID
        self.transferID = transferID
        self.sha256 = sha256
    }

    public static func decodeStrict(_ data: Data) throws -> Self {
        try validateWireObject(data, allowed: ["groupID", "transferID", "sha256"],
                               required: ["groupID", "transferID", "sha256"])
        let decoded = try JSONDecoder().decode(Self.self, from: data)
        return try Self(groupID: decoded.groupID, transferID: decoded.transferID, sha256: decoded.sha256)
    }
}

private func validateWireObject(_ data: Data, allowed: Set<String>, required: Set<String>) throws {
    guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
        throw TransferCoordinatorError.invalidManifest
    }
    try validateWireObject(object, allowed: allowed, required: required)
}

private func validateWireObject(_ object: [String: Any], allowed: Set<String>, required: Set<String>) throws {
    let keys = Set(object.keys)
    guard keys.isSubset(of: allowed), required.isSubset(of: keys) else {
        throw TransferCoordinatorError.invalidManifest
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

/// `resumeOffset` and `alreadyComplete` are v6 fields and are omitted on older channels.
private struct FileDecision: Codable {
    let transferID: UUID
    let accepted: Bool
    var resumeOffset: UInt64? = nil
    var alreadyComplete: Bool? = nil
}

/// Result of one folder send.
public struct FolderSendResult: Sendable, Equatable {
    public let rootName: String
    public let fileCount: Int
    public let totalBytes: UInt64
    /// Files the receiver already had from an earlier attempt.
    public let skippedFiles: Int
}
private struct FileDigest: Codable { let transferID: UUID; let sha256: Data }

/// One explicitly accepted v3 connection. Both directions may send files, including
/// simultaneously. Only one reader owns the socket; records are routed by transfer ID.
/// Files are hashed while streaming. No file-size or elapsed-transfer-time product cap.
public final class FileConnection: @unchecked Sendable {
    public let peerName: String
    public let peerFingerprint: String
    public let protocolVersion: Int
    public var supportsDurableMembershipCancellation: Bool { protocolVersion >= 5 }
    private static let chunkSize = SecureRecordCipher.maxPlaintextLength - 24
    private let transport: ByteTransport
    private let incoming: AuthenticatedRecordStream
    private let writer: ConnectionWriter
    private let lock = NSLock()
    private var closed = false
    private var running = false
    private var outgoingID: UUID?
    private var outgoingGroupID: UUID?
    private var decision: ResponseSlot<FileDecision>?
    private var receipt: ResponseSlot<Data>?
    private var groupDecision: ResponseSlot<GroupFileDecisionWire>?
    private var groupReceipt: ResponseSlot<GroupFileReceiptWire>?
    private var textReceipts: [UUID: ResponseSlot<Bool>] = [:]
    private var activeSink: IncomingFileSink?
    /// Receiver-side folder progress on this connection, keyed by folder ID.
    private var folders: [UUID: FolderReceiveState] = [:]
    public var supportsResume: Bool { protocolVersion >= 6 }
    private var pending: [OpenedRecord] = []

    init(session: HandshakeSession, transport: ByteTransport, peerName: String, fingerprint: String,
         protocolVersion: Int = 3) {
        self.transport = transport
        self.protocolVersion = protocolVersion
        self.peerName = peerName
        peerFingerprint = fingerprint
        incoming = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: session, direction: .receive), finishClosesStream: false)
        writer = ConnectionWriter(transport: transport, session: session)
    }

    func sendConsent(_ accepted: Bool) async throws {
        try await writer.send(type: .connectionDecision, payload: Data([accepted ? 1 : 0]))
    }

    public func sendGroupFrame(_ type: TransferMessageType, payload: Data) async throws {
        guard protocolVersion >= 4,
              (TransferMessageType.groupControl.rawValue...TransferMessageType.groupFileReceipt.rawValue).contains(type.rawValue) else {
            throw TransferCoordinatorError.wrongMessage
        }
        try await writer.send(type: type, payload: payload)
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
        let state = lock.withLock { () -> (ResponseSlot<FileDecision>?, ResponseSlot<Data>?, ResponseSlot<GroupFileDecisionWire>?, ResponseSlot<GroupFileReceiptWire>?, IncomingFileSink?) in
            closed = true
            let values = (decision, receipt, groupDecision, groupReceipt, activeSink)
            decision = nil; receipt = nil; groupDecision = nil; groupReceipt = nil; activeSink = nil
            return values
        }
        let texts = lock.withLock { let slots = Array(textReceipts.values); textReceipts.removeAll(); return slots }
        transport.close()
        for slot in texts { slot.resolve(.failure(ConnectionError.closed)) }
        state.0?.resolve(.failure(ConnectionError.closed))
        state.1?.resolve(.failure(ConnectionError.closed))
        state.2?.resolve(.failure(ConnectionError.closed))
        state.3?.resolve(.failure(ConnectionError.closed))
        state.4?.discard()
    }

    @discardableResult
    public func sendFile(_ fileURL: URL, transferID: UUID = UUID(), kind: FileMetadata.Kind = .file,
                         progress: @escaping @Sendable (UInt64, UInt64) async -> Void = { _, _ in }) async throws -> FileMetadata {
        let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey, .contentModificationDateKey])
        guard values.isRegularFile == true, let size = values.fileSize, size >= 0 else { throw TransferSessionError.ioFailure }
        let resumeID = supportsResume ? ResumeIdentifiers.resumeID(for: fileURL, size: UInt64(size),
                                                                   modified: values.contentModificationDate ?? .distantPast) : nil
        let metadata = try FileMetadata(transferID: transferID, fileName: fileURL.lastPathComponent, size: UInt64(size),
                                        kind: kind, resumeID: resumeID)
        _ = try await send(fileURL, metadata: metadata, progress: progress)
        return metadata
    }

    /// v6 only. Sends every regular file below `folderURL` in order under one transfer ID.
    /// `progress` reports bytes of the whole folder, including files the receiver already had.
    @discardableResult
    public func sendFolder(_ folderURL: URL, transferID: UUID = UUID(),
                           progress: @escaping @Sendable (UInt64, UInt64) async -> Void = { _, _ in }) async throws -> FolderSendResult {
        guard supportsResume else { throw TransferCoordinatorError.wrongMessage }
        let entries = try FolderEnumerator.entries(in: folderURL)
        let rootName = folderURL.standardizedFileURL.lastPathComponent
        let folderID = ResumeIdentifiers.folderID(for: folderURL)
        let totalBytes = entries.reduce(UInt64(0)) { $0 + $1.size }
        var done: UInt64 = 0
        var skipped = 0
        for entry in entries {
            try Task.checkCancellation()
            let folder = try FolderInfo(rootName: rootName, relativePath: entry.relativePath, folderID: folderID,
                                        fileCount: entries.count, totalBytes: totalBytes)
            let metadata = try FileMetadata(transferID: transferID, fileName: entry.url.lastPathComponent, size: entry.size,
                                            resumeID: ResumeIdentifiers.resumeID(for: entry.url, size: entry.size, modified: entry.modified),
                                            folder: folder)
            let base = done
            if try await send(entry.url, metadata: metadata, progress: { count, _ in await progress(base + count, totalBytes) }) {
                skipped += 1
            }
            done += entry.size
            await progress(done, totalBytes)
        }
        return FolderSendResult(rootName: rootName, fileCount: entries.count, totalBytes: totalBytes, skippedFiles: skipped)
    }

    /// Returns true when the receiver reported the file as already complete.
    private func send(_ fileURL: URL, metadata: FileMetadata,
                      progress: @escaping @Sendable (UInt64, UInt64) async -> Void) async throws -> Bool {
        let id = metadata.transferID
        try lock.withLock {
            guard !closed else { throw ConnectionError.closed }
            guard outgoingID == nil else { throw ConnectionError.busy }
            outgoingID = id
        }
        defer { lock.withLock { outgoingID = nil; decision = nil; receipt = nil } }
        return try await withTaskCancellationHandler {
            do {
                try Task.checkCancellation()
                let handle = try FileHandle(forReadingFrom: fileURL)
                defer { try? handle.close() }
                let accepted = ResponseSlot<FileDecision>()
                lock.withLock { decision = accepted }
                try await writer.send(type: .manifest, payload: JSONEncoder().encode(metadata))
                // The receiver may be waiting for a human, so allow the same 2 minutes 10 seconds the
                // Windows client uses; a shorter window would drop connections while the user decides.
                let answer = try await accepted.wait(timeout: 130)
                guard answer.accepted else { throw TransferSessionError.peerRejected }
                let resumeOffset = answer.resumeOffset ?? 0
                guard resumeOffset <= metadata.size, answer.alreadyComplete != true || metadata.folder != nil else {
                    throw TransferCoordinatorError.invalidDecision
                }
                if answer.alreadyComplete == true { return true }

                // The finish digest always covers the whole file, so the skipped prefix is hashed locally.
                var hasher = SHA256()
                var offset: UInt64 = 0
                while offset < resumeOffset {
                    try Task.checkCancellation()
                    let count = Int(min(1 << 20, resumeOffset - offset))
                    guard let bytes = try handle.read(upToCount: count), !bytes.isEmpty else { throw TransferSessionError.ioFailure }
                    hasher.update(data: bytes)
                    offset += UInt64(bytes.count)
                }
                await progress(offset, metadata.size)
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
                return false
            } catch {
                close()
                throw error
            }
        } onCancel: { self.close() }
    }

    @discardableResult
    public func sendGroupFile(_ fileURL: URL, groupID: UUID, messageID: UUID,
                              senderFingerprint: String, memberRevision: UInt64,
                              transferID: UUID = UUID(), kind: FileMetadata.Kind = .file,
                              progress: @escaping @Sendable (UInt64, UInt64) async -> Void = { _, _ in }) async throws -> GroupFileManifestWire {
        guard protocolVersion >= 4 else { throw TransferCoordinatorError.wrongMessage }
        try lock.withLock {
            guard !closed else { throw ConnectionError.closed }
            guard outgoingID == nil else { throw ConnectionError.busy }
            outgoingID = transferID
            outgoingGroupID = groupID
        }
        defer { lock.withLock { outgoingID = nil; outgoingGroupID = nil; groupDecision = nil; groupReceipt = nil } }
        return try await withTaskCancellationHandler {
            do {
                let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
                guard values.isRegularFile == true, let size = values.fileSize, size >= 0 else {
                    throw TransferSessionError.ioFailure
                }
                let metadata = try FileMetadata(transferID: transferID, fileName: fileURL.lastPathComponent,
                                                size: UInt64(size), kind: kind)
                let handle = try FileHandle(forReadingFrom: fileURL)
                defer { try? handle.close() }
                var wholeFile = SHA256()
                while true {
                    try Task.checkCancellation()
                    guard let bytes = try handle.read(upToCount: 1_048_576), !bytes.isEmpty else { break }
                    wholeFile.update(data: bytes)
                }
                let digest = Data(wholeFile.finalize())
                try handle.seek(toOffset: 0)
                let manifest = try GroupFileManifestWire(groupID: groupID, messageID: messageID,
                                                         senderFingerprint: senderFingerprint,
                                                         memberRevision: memberRevision,
                                                         metadata: metadata, sha256: digest)
                let decisionSlot = ResponseSlot<GroupFileDecisionWire>()
                lock.withLock { groupDecision = decisionSlot }
                try await writer.send(type: .groupManifest, payload: JSONEncoder().encode(manifest))
                let decision = try await decisionSlot.wait(timeout: 130)
                guard decision.groupID == groupID, decision.transferID == transferID else {
                    throw TransferCoordinatorError.invalidDecision
                }
                guard decision.accepted else { throw TransferSessionError.peerRejected }

                var offset: UInt64 = 0
                var chunkHasher = SHA256()
                while offset < metadata.size {
                    try Task.checkCancellation()
                    let count = Int(min(UInt64(Self.chunkSize), metadata.size - offset))
                    guard let bytes = try handle.read(upToCount: count), !bytes.isEmpty else {
                        throw TransferSessionError.ioFailure
                    }
                    var payload = Self.idBytes(transferID)
                    var position = offset.bigEndian
                    withUnsafeBytes(of: &position) { payload.append(contentsOf: $0) }
                    payload.append(bytes)
                    try await writer.send(type: .chunk, payload: payload)
                    chunkHasher.update(data: bytes)
                    offset += UInt64(bytes.count)
                    await progress(offset, metadata.size)
                }
                guard Data(chunkHasher.finalize()) == digest else { throw TransferCoordinatorError.checksumMismatch }
                let receiptSlot = ResponseSlot<GroupFileReceiptWire>()
                lock.withLock { groupReceipt = receiptSlot }
                try await writer.send(type: .finish, payload: JSONEncoder().encode(FileDigest(transferID: transferID, sha256: digest)))
                let receipt = try await receiptSlot.wait(timeout: 5 * 60)
                guard receipt.groupID == groupID, receipt.transferID == transferID,
                      receipt.sha256 == digest else { throw TransferCoordinatorError.checksumMismatch }
                return manifest
            } catch {
                if let transferError = error as? TransferSessionError, case .peerRejected = transferError {
                    throw error
                }
                close()
                throw error
            }
        } onCancel: { self.close() }
    }

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
    /// Runs the shared read loop.
    ///
    /// `fileApproval` is asked once per incoming file, before any temp file is created and before the
    /// decision is sent. Returning `false` sends a reject decision; throwing is treated as a refusal
    /// and closes the connection, matching the Windows client's behaviour.
    public func run(receiveDirectory: URL,
                    directoryProvider: (@Sendable () async -> URL)? = nil,
                    fileApproval: (@Sendable (FileMetadata) async throws -> Bool)? = nil,
                    didReceive: @escaping @Sendable (FileMetadata, URL) async -> Void,
                    progress: @escaping @Sendable (FileMetadata, UInt64) async -> Void = { _, _ in },
                    didReceiveText: @escaping @Sendable (ChatText) async throws -> Void = { _ in },
                                          groupFileApproval: @escaping @Sendable (GroupFileManifestWire) async throws -> Bool = { _ in false },
                     groupFileDidReceive: @escaping @Sendable (GroupFileManifestWire, URL) async throws -> Void = { _, _ in },
                     didReceiveGroupFrame: @escaping @Sendable (TransferMessageType, Data) async throws -> Void = { _, _ in
                         throw TransferCoordinatorError.wrongMessage
                     }) async throws {
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
                        guard supportsResume || (metadata.resumeID == nil && metadata.folder == nil),
                              metadata.folder == nil || metadata.resumeID != nil else { throw TransferCoordinatorError.invalidManifest }
                        let folderState = metadata.folder.flatMap { info in lock.withLock { folders[info.folderID] } }
                        if let info = metadata.folder, let folderState {
                            // Later files of an accepted folder stay within its declared limits without another prompt.
                            guard info.sharesFolder(with: folderState.info), folderState.acceptedFiles < info.fileCount,
                                  metadata.size <= info.totalBytes - folderState.acceptedBytes else {
                                throw TransferCoordinatorError.invalidManifest
                            }
                        }

                        // Ask the user before touching the disk. Refusing (or any failure while asking)
                        // sends a reject decision and ends the connection, exactly like Windows.
                        var approved = true
                        if let fileApproval, folderState == nil {
                            do { approved = try await fileApproval(metadata) }
                            catch {
                                try? await writer.send(type: .decision, payload: JSONEncoder().encode(
                                    FileDecision(transferID: metadata.transferID, accepted: false)))
                                throw error
                            }
                        }
                        guard approved else {
                            try await writer.send(type: .decision, payload: JSONEncoder().encode(
                                FileDecision(transferID: metadata.transferID, accepted: false)))
                            throw TransferCoordinatorError.rejectedByReceiver
                        }

                        let directory = await directoryProvider?() ?? receiveDirectory
                        if let resumeID = metadata.resumeID {
                            var folder = folderState
                            if folder == nil, let info = metadata.folder {
                                folder = try FolderReceiveState(info: info, receiveDirectoryURL: directory, peerFingerprint: peerFingerprint)
                                lock.withLock { folders[info.folderID] = folder }
                            }
                            folder?.acceptedFiles += 1
                            folder?.acceptedBytes += metadata.size
                            if let folder, folder.completed.contains(resumeID) {
                                try await writer.send(type: .decision, payload: JSONEncoder().encode(
                                    FileDecision(transferID: metadata.transferID, accepted: true, resumeOffset: 0, alreadyComplete: true)))
                                folder.finishedBytes += metadata.size
                                await progress(metadata, folder.finishedBytes)
                                try await finishFolderIfComplete(folder, metadata: metadata, didReceive: didReceive)
                                continue
                            }
                            var hasher = SHA256()
                            let target: (descriptor: Int32, url: URL)
                            if let folder, let info = metadata.folder { target = try folder.openParent(of: info) }
                            else {
                                let base = directory.resolvingSymlinksInPath()
                                let descriptor = open(base.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                                guard descriptor >= 0 else { throw TransferSessionError.ioFailure }
                                target = (descriptor, base)
                            }
                            defer { Darwin.close(target.descriptor) }
                            let sink = try ResumableFileSink(receiveDirectory: directory,
                                key: ResumeIdentifiers.storageKey(peerFingerprint: peerFingerprint, id: resumeID),
                                size: metadata.size, targetDirectory: target.descriptor, targetURL: target.url,
                                fileName: metadata.fileName, hasher: &hasher)
                            try lock.withLock {
                                guard !closed else { sink.discard(); throw ConnectionError.closed }
                                activeSink = sink
                            }
                            let incoming = IncomingFile(metadata: metadata, sink: sink, folder: folder)
                            incoming.hasher = hasher
                            incoming.received = sink.existingLength
                            file = incoming
                            try await writer.send(type: .decision, payload: JSONEncoder().encode(
                                FileDecision(transferID: metadata.transferID, accepted: true, resumeOffset: sink.existingLength)))
                            await progress(metadata, (folder?.finishedBytes ?? 0) + sink.existingLength)
                            continue
                        }
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
                        let slot: ResponseSlot<FileDecision> = try lock.withLock {
                            guard value.transferID == outgoingID, let slot = decision else { throw TransferCoordinatorError.wrongMessage }
                            decision = nil
                            return slot
                        }
                        guard supportsResume || (value.resumeOffset == nil && value.alreadyComplete == nil) else {
                            throw TransferCoordinatorError.invalidDecision
                        }
                        slot.resolve(.success(value))

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
                        if file.groupManifest == nil { await progress(file.metadata, (file.folder?.finishedBytes ?? 0) + file.received) }

                    case TransferMessageType.finish.rawValue:
                        let value = try JSONDecoder().decode(FileDigest.self, from: record.plaintext)
                        guard let complete = file, value.transferID == complete.metadata.transferID,
                              complete.received == complete.metadata.size, value.sha256.count == 32 else { throw TransferCoordinatorError.interrupted }
                        guard Data(complete.hasher.finalize()) == value.sha256,
                              complete.groupManifest.map({ $0.sha256 == value.sha256 }) ?? true else {
                            // A mismatching partial file is deleted so the next attempt starts at 0.
                            (complete.sink as? ResumableFileSink)?.removePartial()
                            throw TransferCoordinatorError.checksumMismatch
                        }
                        try complete.sink.commit()
                        guard let url = complete.sink.savedURL else { throw TransferSessionError.ioFailure }
                        file = nil
                        lock.withLock { activeSink = nil }
                        if let folder = complete.folder, let resumeID = complete.metadata.resumeID {
                            try folder.markCompleted(resumeID)
                            folder.finishedBytes += complete.metadata.size
                            try await writer.send(type: .receipt, payload: JSONEncoder().encode(value))
                            await progress(complete.metadata, folder.finishedBytes)
                            try await finishFolderIfComplete(folder, metadata: complete.metadata, didReceive: didReceive)
                        } else if let groupManifest = complete.groupManifest {
                            try await groupFileDidReceive(groupManifest, url)
                            let receipt = try GroupFileReceiptWire(groupID: groupManifest.groupID,
                                transferID: groupManifest.metadata.transferID, sha256: value.sha256)
                            try await writer.send(type: .groupFileReceipt, payload: JSONEncoder().encode(receipt))
                        } else {
                            try await writer.send(type: .receipt, payload: JSONEncoder().encode(value))
                            await didReceive(complete.metadata, url)
                        }

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

                    case TransferMessageType.groupManifest.rawValue:
                        guard protocolVersion >= 4, file == nil else { throw TransferCoordinatorError.wrongMessage }
                        let raw = try GroupFileManifestWire.decodeStrict(record.plaintext)
                        let manifest = try GroupFileManifestWire(groupID: raw.groupID, messageID: raw.messageID,
                            senderFingerprint: raw.senderFingerprint, memberRevision: raw.memberRevision,
                            metadata: raw.metadata, sha256: raw.sha256)
                        let approved: Bool
                        do { approved = try await groupFileApproval(manifest) }
                        catch { approved = false }
                        let response = GroupFileDecisionWire(groupID: manifest.groupID,
                            transferID: manifest.metadata.transferID, accepted: approved,
                            reason: approved ? nil : "接收方拒绝或无法处理该群文件。")
                        if !approved {
                            try await writer.send(type: .groupFileDecision, payload: JSONEncoder().encode(response))
                            continue
                        }
                        let directory = await directoryProvider?() ?? receiveDirectory
                        let sink = try lock.withLock {
                            guard !closed else { throw ConnectionError.closed }
                            let sink = try TemporaryFileSink(directoryURL: directory, metadata: manifest.metadata)
                            activeSink = sink
                            return sink
                        }
                        file = IncomingFile(metadata: manifest.metadata, sink: sink, groupManifest: manifest)
                        try await writer.send(type: .groupFileDecision, payload: JSONEncoder().encode(response))

                    case TransferMessageType.groupFileDecision.rawValue:
                        let value = try GroupFileDecisionWire.decodeStrict(record.plaintext)
                        let slot: ResponseSlot<GroupFileDecisionWire> = try lock.withLock {
                            guard value.transferID == outgoingID, value.groupID == outgoingGroupID,
                                  let slot = groupDecision else { throw TransferCoordinatorError.invalidDecision }
                            groupDecision = nil
                            return slot
                        }
                        slot.resolve(.success(value))

                    case TransferMessageType.groupFileReceipt.rawValue:
                        let value = try GroupFileReceiptWire.decodeStrict(record.plaintext)
                        let slot: ResponseSlot<GroupFileReceiptWire> = try lock.withLock {
                            guard value.transferID == outgoingID, value.groupID == outgoingGroupID,
                                  value.sha256.count == 32, let slot = groupReceipt else {
                                throw TransferCoordinatorError.checksumMismatch
                            }
                            groupReceipt = nil
                            return slot
                        }
                        slot.resolve(.success(value))

                    case TransferMessageType.groupControl.rawValue,
                         TransferMessageType.groupText.rawValue,
                         TransferMessageType.groupTextAck.rawValue:
                        guard protocolVersion >= 4,
                              let type = TransferMessageType(rawValue: record.type) else {
                            throw TransferSessionError.unexpectedRecord(record.type)
                        }
                        try await didReceiveGroupFrame(type, record.plaintext)

                    default: throw TransferSessionError.unexpectedRecord(record.type)
                    }
                }
            }
        } onCancel: { self.close() }
    }

    /// Reports a folder once, with its root directory, after its last declared file.
    private func finishFolderIfComplete(_ folder: FolderReceiveState, metadata: FileMetadata,
                                        didReceive: @Sendable (FileMetadata, URL) async -> Void) async throws {
        guard folder.acceptedFiles == folder.info.fileCount else { return }
        folder.removeState()
        lock.withLock { folders[folder.info.folderID] = nil }
        await didReceive(metadata, folder.rootURL)
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
    let sink: IncomingFileSink
    let groupManifest: GroupFileManifestWire?
    let folder: FolderReceiveState?
    var received: UInt64 = 0
    var hasher = SHA256()
    init(metadata: FileMetadata, sink: IncomingFileSink, groupManifest: GroupFileManifestWire? = nil,
         folder: FolderReceiveState? = nil) {
        self.metadata = metadata
        self.sink = sink
        self.groupManifest = groupManifest
        self.folder = folder
    }
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
