import XCTest
import CryptoKit
@testable import FeigeIdentity

final class ResumableTransferTests: XCTestCase {
    private var root: URL!
    private var sendDirectory: URL!
    private var receiveDirectory: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        sendDirectory = root.appendingPathComponent("Send")
        receiveDirectory = root.appendingPathComponent("Receive")
        try FileManager.default.createDirectory(at: sendDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: receiveDirectory, withIntermediateDirectories: true)
    }

    override func tearDown() { try? FileManager.default.removeItem(at: root) }

    private struct Pair {
        let sender: FileConnection
        let receiver: FileConnection
        let reader: Task<Void, Error>
        let senderReader: Task<Void, Error>
        func close() { sender.close(); receiver.close(); reader.cancel(); senderReader.cancel() }
    }

    // Signing needs the stored keys, so the stores live until the whole class finishes.
    private static let stores = (DeviceIdentityStore(serviceIdentifier: "com.feige.v6.a.\(UUID())"),
                                 DeviceIdentityStore(serviceIdentifier: "com.feige.v6.b.\(UUID())"))
    private static let identities: (DeviceIdentity, DeviceIdentity) = (try! stores.0.loadOrCreate(), try! stores.1.loadOrCreate())

    override class func tearDown() {
        stores.0.delete()
        stores.1.delete()
        super.tearDown()
    }

    /// Same identities every time, so receiver storage keys survive reconnects like they do between real devices.
    private func connect(version: UInt16 = 6, approvals: Approvals = Approvals()) async throws -> Pair {
        let (identityA, identityB) = Self.identities
        let (first, second) = InMemoryDuplexTransport.pair()
        async let connecting = ConnectionService.establish(transport: first, identity: identityA, role: .initiator,
                                                           deviceName: "A", protocolVersion: version) { _ in false }
        let receiver = try await ConnectionService.establish(transport: second, identity: identityB, role: .responder,
                                                             deviceName: "B", protocolVersion: version) { _ in true }
        let sender = try await connecting
        let directory = receiveDirectory!
        let reader = Task {
            try await receiver.run(receiveDirectory: directory, fileApproval: { metadata in
                await approvals.record(metadata)
                return true
            }, didReceive: { metadata, url in await approvals.received(metadata, url) })
        }
        // The sender's read loop delivers decisions and receipts.
        let unused = root.appendingPathComponent("Unused")
        let senderReader = Task { try await sender.run(receiveDirectory: unused, didReceive: { _, _ in }) }
        return Pair(sender: sender, receiver: receiver, reader: reader, senderReader: senderReader)
    }

    actor Approvals {
        var prompts: [FileMetadata] = []
        var deliveries: [(FileMetadata, URL)] = []
        func record(_ metadata: FileMetadata) { prompts.append(metadata) }
        func received(_ metadata: FileMetadata, _ url: URL) { deliveries.append((metadata, url)) }
    }

    actor Offsets {
        var values: [UInt64] = []
        func add(_ value: UInt64) { values.append(value) }
    }

    private func partURL(for file: URL, receiverPeer: String) throws -> URL {
        let values = try file.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
        let id = ResumeIdentifiers.resumeID(for: file, size: UInt64(values.fileSize!), modified: values.contentModificationDate!)
        return receiveDirectory.appendingPathComponent(".feige-resume-\(ResumeIdentifiers.storageKey(peerFingerprint: receiverPeer, id: id)).part")
    }

    private func bytes(_ count: Int, seed: UInt8 = 1) -> Data {
        Data((0..<count).map { UInt8(truncatingIfNeeded: $0 &* 31 &+ Int(seed)) })
    }

    func testSendResumesFromRetainedPrefix() async throws {
        let content = bytes(300_000)
        let source = sendDirectory.appendingPathComponent("big.bin")
        try content.write(to: source)
        let pair = try await connect()
        defer { pair.close() }
        try content.prefix(120_000).write(to: partURL(for: source, receiverPeer: pair.receiver.peerFingerprint))
        let offsets = Offsets()
        _ = try await pair.sender.sendFile(source) { count, _ in await offsets.add(count) }
        XCTAssertEqual(try Data(contentsOf: receiveDirectory.appendingPathComponent("big.bin")), content)
        let first = await offsets.values.first
        XCTAssertEqual(first, 120_000, "the first progress report starts at the resume offset")
        XCTAssertFalse(FileManager.default.fileExists(atPath: try partURL(for: source, receiverPeer: pair.receiver.peerFingerprint).path))
    }

    func testCorruptPrefixFailsOnceAndThenRestartsFromZero() async throws {
        let content = bytes(200_000)
        let source = sendDirectory.appendingPathComponent("c.bin")
        try content.write(to: source)
        var pair = try await connect()
        let part = try partURL(for: source, receiverPeer: pair.receiver.peerFingerprint)
        try bytes(50_000, seed: 9).write(to: part)
        do {
            _ = try await pair.sender.sendFile(source)
            XCTFail("a corrupt prefix must fail the digest")
        } catch {}
        pair.close()
        _ = try? await pair.reader.value
        XCTAssertFalse(FileManager.default.fileExists(atPath: part.path), "a mismatching partial file is deleted")
        XCTAssertFalse(FileManager.default.fileExists(atPath: receiveDirectory.appendingPathComponent("c.bin").path))

        pair = try await connect()
        defer { pair.close() }
        _ = try await pair.sender.sendFile(source)
        XCTAssertEqual(try Data(contentsOf: receiveDirectory.appendingPathComponent("c.bin")), content)
    }

    func testOversizedPartialIsDiscarded() async throws {
        let content = bytes(10_000)
        let source = sendDirectory.appendingPathComponent("s.bin")
        try content.write(to: source)
        let pair = try await connect()
        defer { pair.close() }
        try bytes(20_000, seed: 3).write(to: partURL(for: source, receiverPeer: pair.receiver.peerFingerprint))
        let offsets = Offsets()
        _ = try await pair.sender.sendFile(source) { count, _ in await offsets.add(count) }
        XCTAssertEqual(try Data(contentsOf: receiveDirectory.appendingPathComponent("s.bin")), content)
        let first = await offsets.values.first
        XCTAssertEqual(first, 0)
    }

    private func makeFolder() throws -> URL {
        let folder = sendDirectory.appendingPathComponent("Photos")
        let nested = folder.appendingPathComponent("2026/May")
        try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true)
        try bytes(70_000).write(to: folder.appendingPathComponent("a.jpg"))
        try bytes(0).write(to: folder.appendingPathComponent("empty.txt"))
        try bytes(90_000, seed: 5).write(to: nested.appendingPathComponent("b.jpg"))
        try Data("x".utf8).write(to: folder.appendingPathComponent(".DS_Store"))
        let outside = sendDirectory.appendingPathComponent("secret.txt")
        try Data("secret".utf8).write(to: outside)
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("link.txt"), withDestinationURL: outside)
        return folder
    }

    func testFolderIsApprovedOnceAndNeverMergedIntoExistingFolder() async throws {
        let folder = try makeFolder()
        // An unrelated folder with the same name already exists at the receiver.
        try FileManager.default.createDirectory(at: receiveDirectory.appendingPathComponent("Photos"), withIntermediateDirectories: true)
        let approvals = Approvals()
        let pair = try await connect(approvals: approvals)
        defer { pair.close() }
        let result = try await pair.sender.sendFolder(folder)
        XCTAssertEqual(result.fileCount, 3)
        XCTAssertEqual(result.totalBytes, 160_000)
        XCTAssertEqual(result.skippedFiles, 0)
        let received = receiveDirectory.appendingPathComponent("Photos (1)")
        XCTAssertEqual(try Data(contentsOf: received.appendingPathComponent("a.jpg")), bytes(70_000))
        XCTAssertEqual(try Data(contentsOf: received.appendingPathComponent("2026/May/b.jpg")), bytes(90_000, seed: 5))
        XCTAssertEqual(try Data(contentsOf: received.appendingPathComponent("empty.txt")).count, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: received.appendingPathComponent("link.txt").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: received.appendingPathComponent(".DS_Store").path))
        let prompts = await approvals.prompts
        XCTAssertEqual(prompts.count, 1, "a folder is approved once")
        let deliveries = await approvals.deliveries
        XCTAssertEqual(deliveries.count, 1, "a folder is reported once, with its root")
        XCTAssertEqual(deliveries.first?.1.lastPathComponent, "Photos (1)")
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: receiveDirectory.path).filter { $0.hasPrefix(".feige-") }
        XCTAssertEqual(leftovers, [], "folder state is removed when it completes")
    }

    func testFolderResumeSkipsCompletedFiles() async throws {
        let folder = try makeFolder()
        let firstFile = folder.appendingPathComponent("2026/May/b.jpg")
        let values = try firstFile.resourceValues(forKeys: [.contentModificationDateKey])
        let completedID = ResumeIdentifiers.resumeID(for: firstFile, size: 90_000, modified: values.contentModificationDate!)
        let pair = try await connect()
        defer { pair.close() }
        // State left by an interrupted attempt: the root exists and the first file finished.
        let key = ResumeIdentifiers.storageKey(peerFingerprint: pair.receiver.peerFingerprint, id: ResumeIdentifiers.folderID(for: folder))
        let existingRoot = receiveDirectory.appendingPathComponent("Photos")
        try FileManager.default.createDirectory(at: existingRoot.appendingPathComponent("2026/May"), withIntermediateDirectories: true)
        try bytes(90_000, seed: 5).write(to: existingRoot.appendingPathComponent("2026/May/b.jpg"))
        try "Photos\n\(completedID.uuidString)\n".write(to: receiveDirectory.appendingPathComponent(".feige-folder-\(key).state"),
                                                        atomically: true, encoding: .utf8)
        let result = try await pair.sender.sendFolder(folder)
        XCTAssertEqual(result.skippedFiles, 1)
        let names = try FileManager.default.subpathsOfDirectory(atPath: existingRoot.path).sorted()
        XCTAssertEqual(names, ["2026", "2026/May", "2026/May/b.jpg", "a.jpg", "empty.txt"])
    }

    func testOlderChannelsRefuseFoldersAndOmitV6Fields() async throws {
        let folder = try makeFolder()
        let pair = try await connect(version: 5)
        defer { pair.close() }
        XCTAssertFalse(pair.sender.supportsResume)
        do { _ = try await pair.sender.sendFolder(folder); XCTFail("v5 cannot send folders") } catch {}
        let metadata = try await pair.sender.sendFile(folder.appendingPathComponent("a.jpg"))
        XCTAssertNil(metadata.resumeID)
        let encoded = String(decoding: try JSONEncoder().encode(metadata), as: UTF8.self)
        XCTAssertFalse(encoded.contains("resumeID") || encoded.contains("folder"))
    }

    func testFolderInfoRejectsTraversalAndLimits() throws {
        let id = UUID()
        func info(_ path: String, root: String = "R", count: Int = 1) throws -> FolderInfo {
            try FolderInfo(rootName: root, relativePath: path, folderID: id, fileCount: count, totalBytes: 10)
        }
        XCTAssertNoThrow(try info("a/b.txt"))
        for bad in ["../a", "a/../b", "/a", "a//b", "a/./b", "a\\b", "a/\u{1}", ""] {
            XCTAssertThrowsError(try info(bad), bad)
        }
        XCTAssertThrowsError(try info("a", root: ".."))
        XCTAssertThrowsError(try info("a", count: 0))
        XCTAssertThrowsError(try info("a", count: 10_001))
        XCTAssertThrowsError(try info(Array(repeating: "d", count: 33).joined(separator: "/")))
        XCTAssertThrowsError(try info(String(repeating: "a/", count: 400) + "b"))
        // The last component must be the manifest's file name.
        XCTAssertThrowsError(try FileMetadata(fileName: "x.txt", size: 1, resumeID: UUID(), folder: info("a/b.txt")))
        XCTAssertThrowsError(try FileMetadata(fileName: "b.txt", size: 11, resumeID: UUID(), folder: info("a/b.txt")))
    }

    func testExpiredResumeStorageIsRemoved() throws {
        let part = receiveDirectory.appendingPathComponent(".feige-resume-0123.part")
        let state = receiveDirectory.appendingPathComponent(".feige-folder-0123.state")
        let fresh = receiveDirectory.appendingPathComponent(".feige-resume-4567.part")
        let user = receiveDirectory.appendingPathComponent("keep.part")
        for url in [part, state, fresh, user] { try Data("x".utf8).write(to: url) }
        let old = Date().addingTimeInterval(-8 * 24 * 60 * 60)
        for url in [part, state, user] { try FileManager.default.setAttributes([.modificationDate: old], ofItemAtPath: url.path) }
        let removed = ResumableStorageCleanup.removeExpired(in: receiveDirectory)
        XCTAssertEqual(Set(removed), [part.lastPathComponent, state.lastPathComponent])
        XCTAssertTrue(FileManager.default.fileExists(atPath: fresh.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: user.path))
    }
}
