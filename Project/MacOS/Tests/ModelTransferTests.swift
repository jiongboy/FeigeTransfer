import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

final class ModelTransferTests: XCTestCase {
    @MainActor
    private func exercise(acceptConnection: Bool, count: Int) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.model.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.model.b.\(UUID())")
        defer { try? FileManager.default.removeItem(at: root); a.delete(); b.delete() }
        let senderLoader = IdentityLoadCounter(store: a), receiverLoader = IdentityLoadCounter(store: b)
        let sender = TransferModel(supportDirectory: root.appendingPathComponent("a"), identityStore: a,
                                   identityLoader: { try senderLoader.load() })
        let receiver = TransferModel(supportDirectory: root.appendingPathComponent("b"), identityStore: b,
                                     identityLoader: { try receiverLoader.load() })
        let output = root.appendingPathComponent("received")
        try sender.saveSettings(deviceName: "发送方", receiveDirectory: root.appendingPathComponent("unused"))
        try receiver.saveSettings(deviceName: "接收方", receiveDirectory: output)
        await sender.start()
        await receiver.start()

        let listener = try NWListener(using: .tcp, on: .any)
        defer { listener.cancel(); sender.shutdown(); receiver.shutdown() }
        let (states, stateContinuation) = AsyncStream<Result<UInt16, Error>>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue {
                stateContinuation.yield(.success(port)); stateContinuation.finish()
            }
            if case .failed(let error) = state {
                stateContinuation.yield(.failure(error)); stateContinuation.finish()
            }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in receiver.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.model.test.listener"))
        var iterator = states.makeAsyncIterator()
        let state = await iterator.next()
        let port = try XCTUnwrap(state).get()
        let device = try sender.manualDevice(host: "127.0.0.1", port: String(port))
        var files: [URL] = []
        let content = Data(repeating: 42, count: 120_000)
        for index in 0..<count {
            let file = root.appendingPathComponent("file-\(index).bin")
            try content.write(to: file)
            files.append(file)
        }

        // Simulate the single recipient click; the requester must never be prompted.
        var approvalsCount = 0
        let approvals = Task { @MainActor in
            while !Task.isCancelled {
                XCTAssertNil(sender.pendingConnectionPeerName)
                if receiver.pendingConnectionPeerName != nil {
                    approvalsCount += 1
                    receiver.approveConnection(acceptConnection)
                }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { approvals.cancel() }
        sender.requestConnection(to: device)
        let deadline = Date().addingTimeInterval(20)
        while (sender.isConnecting || receiver.isConnecting), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(approvalsCount, 1)
        if acceptConnection {
            XCTAssertTrue(sender.isConnected)
            XCTAssertTrue(receiver.isConnected)
            sender.startSending(files)
            while (sender.isSending || receiver.records.count < count), Date() < deadline {
                try await Task.sleep(for: .milliseconds(10))
            }
        }
        if acceptConnection {
            XCTAssertNil(sender.errorMessage)
            XCTAssertNil(receiver.errorMessage)
            XCTAssertEqual(sender.records.count, count)
            XCTAssertEqual(receiver.records.count, count)
            XCTAssertEqual(sender.queueCompleted, count)
            for file in files { XCTAssertEqual(try Data(contentsOf: output.appendingPathComponent(file.lastPathComponent)), content) }
            XCTAssertEqual(TransferModel(supportDirectory: root.appendingPathComponent("a"), identityStore: a).records.count, count)
            XCTAssertEqual(TransferModel(supportDirectory: root.appendingPathComponent("b"), identityStore: b).records.count, count)
            // Reuse the approved connection in the reverse direction without another prompt.
            receiver.startSending([files[0]])
            while (receiver.isSending || sender.records.count < count + 1), Date() < deadline {
                try await Task.sleep(for: .milliseconds(10))
            }
            XCTAssertEqual(approvalsCount, 1)
            XCTAssertEqual(sender.records.count, count + 1)
            XCTAssertEqual(receiver.records.count, count + 1)
            XCTAssertTrue(sender.isConnected)
            XCTAssertTrue(receiver.isConnected)
            // Chat, original image transfer and deduplicated retries share the same single approval.
            let body = "你好 👋\n这是文字聊天"
            XCTAssertTrue(sender.sendText(body))
            XCTAssertTrue(receiver.sendText("收到，图片也可以发过来"))
            while (sender.isSendingText || receiver.isSendingText), Date() < deadline {
                try await Task.sleep(for: .milliseconds(10))
            }
            let delivered = try XCTUnwrap(sender.messages.first { $0.kind == .text && $0.direction == .sent })
            XCTAssertEqual(delivered.status, .delivered)
            XCTAssertEqual(receiver.messages.first { $0.id == delivered.id }?.text, body)
            XCTAssertEqual(sender.messages.filter { $0.kind == .text }.count, 2)
            // Simulate a lost acknowledgement: reusing the message ID must not add a second bubble.
            let sentIndex = try XCTUnwrap(sender.messages.firstIndex { $0.id == delivered.id })
            sender.messages[sentIndex].status = .failed
            sender.retryMessage(sender.messages[sentIndex])
            while sender.isSendingText, Date() < deadline { try await Task.sleep(for: .milliseconds(10)) }
            XCTAssertEqual(receiver.messages.filter { $0.id == delivered.id }.count, 1)
            let image = root.appendingPathComponent("sample.png")
            let original = Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aVKsAAAAASUVORK5CYII=")!
            try original.write(to: image)
            sender.startSending([image])
            while (sender.isSending || receiver.records.count < count + 2), Date() < deadline {
                try await Task.sleep(for: .milliseconds(10))
            }
            let picture = try XCTUnwrap(receiver.messages.first { $0.kind == .image })
            XCTAssertEqual(picture.status, .received)
            XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: XCTUnwrap(picture.localPath))), original)
            let reloaded = TransferModel(supportDirectory: root.appendingPathComponent("b"), identityStore: b)
            XCTAssertEqual(reloaded.messages.filter { $0.kind == .text }.count, 2)
            XCTAssertEqual(reloaded.messages.first { $0.kind == .image }?.localPath, picture.localPath)
            XCTAssertEqual(reloaded.chatPeers.count, 1)
            XCTAssertEqual(approvalsCount, 1)
            XCTAssertNil(sender.errorMessage)
            XCTAssertNil(receiver.errorMessage)
            sender.disconnect()
            while receiver.isConnected, Date() < deadline { try await Task.sleep(for: .milliseconds(10)) }
            sender.requestConnection(to: device)
            while (sender.isConnecting || receiver.isConnecting), Date() < deadline {
                try await Task.sleep(for: .milliseconds(10))
            }
            XCTAssertTrue(sender.isConnected)
            XCTAssertTrue(receiver.isConnected)
            XCTAssertEqual(approvalsCount, 2)
            XCTAssertEqual(senderLoader.calls, 1)
            XCTAssertEqual(receiverLoader.calls, 1)
        } else {
            XCTAssertTrue(sender.records.isEmpty)
            XCTAssertTrue(receiver.records.isEmpty)
            XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: output.path).isEmpty)
            XCTAssertFalse(sender.isConnected)
            XCTAssertFalse(receiver.isConnected)
        }
    }

    @MainActor
    func testSingleConnectionApprovalAllowsMultipleFilesAndReverseSending() async throws {
        try await exercise(acceptConnection: true, count: 2)
    }

    @MainActor
    func testRejectingConnectionWritesNothingAndCreatesNoHistory() async throws {
        try await exercise(acceptConnection: false, count: 1)
    }
}

private final class IdentityLoadCounter: @unchecked Sendable {
    private let lock = NSLock()
    private let store: DeviceIdentityStore
    private var count = 0
    var calls: Int { lock.withLock { count } }
    init(store: DeviceIdentityStore) { self.store = store }
    func load() throws -> DeviceIdentity {
        lock.withLock { count += 1 }
        return try store.loadOrCreate()
    }
}
