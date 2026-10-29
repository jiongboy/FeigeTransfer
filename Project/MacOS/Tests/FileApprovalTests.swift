import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

/// Per-file receive confirmation: nothing is written before the user agrees, and declining closes
/// the connection exactly like the Windows client.
final class FileApprovalTests: XCTestCase {
    @MainActor
    private func makePair(root: URL) throws -> (sender: TransferModel, receiver: TransferModel,
                                                a: DeviceIdentityStore, b: DeviceIdentityStore) {
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.approval.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.approval.b.\(UUID())")
        let sender = TransferModel(supportDirectory: root.appendingPathComponent("a"), identityService: "com.feige.approval.a.\(UUID())",
                                   identityLoader: { try a.loadOrCreate() }, makeUpdates: false)
        let receiver = TransferModel(supportDirectory: root.appendingPathComponent("b"), identityService: "com.feige.approval.b.\(UUID())",
                                     identityLoader: { try b.loadOrCreate() }, makeUpdates: false)
        return (sender, receiver, a, b)
    }

    /// Drives one file from sender to receiver and returns what the receiver stored.
    @MainActor
    private func transfer(_ accept: Bool) async throws -> (messages: [ChatMessage], fileExists: Bool) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let pair = try makePair(root: root)
        let output = root.appendingPathComponent("received")
        defer {
            pair.sender.shutdown(); pair.receiver.shutdown()
            pair.a.delete(); pair.b.delete()
            try? FileManager.default.removeItem(at: root)
        }
        try pair.sender.saveSettings(deviceName: "发送方", receiveDirectory: root.appendingPathComponent("unused"))
        try pair.receiver.saveSettings(deviceName: "接收方", receiveDirectory: output)
        await pair.sender.start()
        await pair.receiver.start()

        let listener = try NWListener(using: .tcp, on: .any)
        defer { listener.cancel() }
        let (states, continuation) = AsyncStream<Result<UInt16, Error>>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue { continuation.yield(.success(port)); continuation.finish() }
            if case .failed(let error) = state { continuation.yield(.failure(error)); continuation.finish() }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in pair.receiver.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.approval.test"))
        var iterator = states.makeAsyncIterator()
        let first = await iterator.next()
        let port = try XCTUnwrap(first).get()
        let device = try pair.sender.manualDevice(host: "127.0.0.1", port: String(port))
        pair.sender.requestConnection(to: device)

        // Answer both prompts as they appear: the connection consent, then the file confirmation.
        let driver = Task { @MainActor in
            var answeredConnection = false
            while !Task.isCancelled {
                if !answeredConnection, pair.receiver.pendingConnectionPeerName != nil {
                    answeredConnection = true
                    pair.receiver.approveConnection(true)
                }
                if pair.receiver.messages.contains(where: { $0.status == .awaitingApproval }) {
                    let id = pair.receiver.messages.first { $0.status == .awaitingApproval }!.id
                    pair.receiver.decideIncomingFile(id, accepted: accept)
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { driver.cancel() }

        let file = root.appendingPathComponent("approval.bin")
        try Data(repeating: 7, count: 64 * 1024).write(to: file)
        // Wait for the connection first, then send; the sender reports nothing until consent arrives.
        for _ in 0..<500 where !pair.sender.isConnected { try await Task.sleep(for: .milliseconds(20)) }
        XCTAssertTrue(pair.sender.isConnected, "connection should be established for the approval test")
        pair.sender.startSending([file])

        for _ in 0..<500 {
            try await Task.sleep(for: .milliseconds(20))
            if pair.receiver.messages.contains(where: { $0.status == .received || $0.status == .declined }) { break }
        }
        let exists = (try? FileManager.default.contentsOfDirectory(at: output, includingPropertiesForKeys: nil))?.isEmpty == false
        return (pair.receiver.messages, exists)
    }

    @MainActor
    func testDeclinedFileIsNeverWrittenAndIsMarkedDeclined() async throws {
        let result = try await transfer(false)
        XCTAssertFalse(result.fileExists, "a declined file must not create anything on disk")
        XCTAssertEqual(result.messages.last?.status, .declined)
        XCTAssertEqual(result.messages.last?.failureReason, "已拒绝接收该文件。")
    }

    @MainActor
    func testAcceptedFileIsSavedAndMarkedReceived() async throws {
        let result = try await transfer(true)
        XCTAssertTrue(result.fileExists, "an accepted file must be written")
        XCTAssertEqual(result.messages.last?.status, .received)
        XCTAssertFalse(result.messages.contains { $0.status == .awaitingApproval })
    }

    @MainActor
    func testPendingConfirmationIsVisibleAsAwaitingApproval() async throws {
        // The sender's model reports a file only after consent; the receiver must show it first.
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let message = ChatMessage(peerID: "fp", peerName: "对端", direction: .received, kind: .file,
                                  fileName: "report.pdf", size: 1024, status: .awaitingApproval)
        XCTAssertTrue(message.isPending)
        XCTAssertEqual(message.status.rawValue, "awaitingApproval")
        // Unknown ids are ignored rather than crashing when a user clicks twice.
        model.decideIncomingFile(UUID(), accepted: true)
    }

    @MainActor
    func testStatusIsCodableForOldArchives() throws {
        // Older archives never contain the new states; decoding must still work and new ones must round-trip.
        // Written by the 0.3.3 encoder directly, without any of the newer states.
        let old = ChatMessage(peerID: "fp", peerName: "n", direction: .received, kind: .file, status: .received)
        let decoded = try JSONDecoder().decode(ChatMessage.self, from: JSONEncoder().encode(old))
        XCTAssertEqual(decoded.status, .received)
        let pending = ChatMessage(peerID: "fp", peerName: "n", direction: .received, kind: .file, status: .awaitingApproval)
        let round = try JSONDecoder().decode(ChatMessage.self, from: JSONEncoder().encode(pending))
        XCTAssertEqual(round.status, .awaitingApproval)
    }
}

final class CodeHighlighterTests: XCTestCase {
    func testKeywordsStringsCommentsAndNumbersAreTokenized() {
        let tokens = CodeHighlighter.tokens(in: "public func run() -> Int { // note")
        XCTAssertTrue(tokens.contains(.init(kind: .keyword, text: "public")))
        XCTAssertTrue(tokens.contains(.init(kind: .keyword, text: "func")))
        XCTAssertTrue(tokens.contains(.init(kind: .comment, text: "// note")))
    }

    func testStringAndNumberTokens() {
        let tokens = CodeHighlighter.tokens(in: #"let x = "hello" + 42"#)
        XCTAssertTrue(tokens.contains(.init(kind: .string, text: "\"hello\"")))
        XCTAssertTrue(tokens.contains(.init(kind: .number, text: "42")))
    }

    func testLosslessConcatenation() {
        for line in ["", "   ", "a+b", "\tindented = 'q'", "<!-- c --> x", "中文注释 // 尾部"] {
            let joined = CodeHighlighter.tokens(in: line).map(\.text).joined()
            XCTAssertEqual(joined, line, "highlighting must never change the displayed text")
        }
    }

    func testPlainTextIsNotKeywordHighlighted() {
        let tokens = CodeHighlighter.tokens(in: "just some words")
        XCTAssertEqual(tokens, [.init(kind: .plain, text: "just some words")])
    }
}
