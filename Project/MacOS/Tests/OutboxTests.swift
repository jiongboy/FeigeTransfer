import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

final class OutboxTests: XCTestCase {
    @MainActor
    func testOfflineMessagesPersistExpireAndRespectTheLimit() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let peerID = String(repeating: "AB", count: 32)
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        try model.saveSettings(deviceName: "发送方", receiveDirectory: root.appendingPathComponent("received"))

        XCTAssertFalse(model.sendText("x", to: ""), "a conversation without a fingerprint cannot queue")
        XCTAssertTrue(model.sendText("你好", to: peerID))
        let file = root.appendingPathComponent("a.bin")
        try Data(repeating: 1, count: 10).write(to: file)
        model.startSending([file], to: peerID)
        XCTAssertEqual(model.outboxCount(peerID), 2)
        XCTAssertTrue(model.messages.allSatisfy { $0.isOutbox && $0.status == .queued })

        // Pending messages survive a restart; an interrupted delivery goes back to pending.
        var archive = try ConversationArchive.decode(Data(contentsOf: root.appendingPathComponent("messages.json")))
        archive.messages[0].status = .sending
        try JSONEncoder().encode(archive).write(to: root.appendingPathComponent("messages.json"))
        let reloaded = TransferModel(supportDirectory: root, makeUpdates: false)
        XCTAssertEqual(reloaded.outboxCount(peerID), 2)
        XCTAssertTrue(reloaded.messages.allSatisfy { $0.status == .queued })

        reloaded.cancelOutboxMessage(reloaded.messages[1])
        XCTAssertEqual(reloaded.outboxCount(peerID), 1)

        for index in 1..<TransferModel.outboxLimit { XCTAssertTrue(reloaded.sendText("\(index)", to: peerID)) }
        XCTAssertFalse(reloaded.sendText("over", to: peerID))
        XCTAssertNotNil(reloaded.errorMessage)

        reloaded.connectTrustedPeers(now: Date().addingTimeInterval(TransferModel.outboxLifetime + 60))
        XCTAssertEqual(reloaded.outboxCount(peerID), 0)
        XCTAssertTrue(reloaded.messages.allSatisfy { $0.status == .failed && $0.failureReason?.contains("7 天") == true })

        // A manual resend queues the message again.
        reloaded.retryMessage(reloaded.messages[0])
        XCTAssertEqual(reloaded.outboxCount(peerID), 1)
    }

    @MainActor
    func testOutboxDeliversInOrderAfterConnecting() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.outbox.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.outbox.b.\(UUID())")
        defer { try? FileManager.default.removeItem(at: root); a.delete(); b.delete() }
        let sender = TransferModel(supportDirectory: root.appendingPathComponent("a"), identityStore: a)
        let receiver = TransferModel(supportDirectory: root.appendingPathComponent("b"), identityStore: b)
        try sender.saveSettings(deviceName: "发送方", receiveDirectory: root.appendingPathComponent("unused"))
        try receiver.saveSettings(deviceName: "接收方", receiveDirectory: root.appendingPathComponent("received"))
        await sender.start()
        await receiver.start()
        let receiverID = receiver.identityFingerprint

        let missing = root.appendingPathComponent("missing.bin")
        try Data(repeating: 2, count: 10).write(to: missing)
        let file = root.appendingPathComponent("b.bin")
        try Data(repeating: 3, count: 1000).write(to: file)
        XCTAssertTrue(sender.sendText("离线消息", to: receiverID))
        sender.startSending([missing], to: receiverID)
        sender.startSending([file], to: receiverID)
        try FileManager.default.removeItem(at: missing)
        let textID = try XCTUnwrap(sender.messages.first { $0.kind == .text }?.id)

        let listener = try NWListener(using: .tcp, on: .any)
        defer { listener.cancel(); sender.shutdown(); receiver.shutdown() }
        let (ports, continuation) = AsyncStream<UInt16>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue { continuation.yield(port); continuation.finish() }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in receiver.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.outbox.test.listener"))
        var iterator = ports.makeAsyncIterator()
        let nextPort = await iterator.next()
        let port = try XCTUnwrap(nextPort)
        let device = try sender.manualDevice(host: "127.0.0.1", port: String(port))
        let watcher = Task { @MainActor in
            while !Task.isCancelled {
                if receiver.pendingConnectionPeerName != nil { receiver.approveConnection(true) }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { watcher.cancel() }
        sender.requestConnection(to: device)

        let deadline = Date().addingTimeInterval(30)
        while !receiver.messages.contains(where: { $0.status == .awaitingApproval }), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(sender.connectedPeerID, receiverID)
        XCTAssertEqual(receiver.messages.first { $0.kind == .text }?.id, textID, "text keeps its message ID")
        XCTAssertEqual(sender.messages.first { $0.id == textID }?.status, .delivered)
        let failed = try XCTUnwrap(sender.messages.first { $0.fileName == "missing.bin" })
        XCTAssertEqual(failed.status, .failed)
        XCTAssertEqual(failed.failureReason, "源文件已不存在，无法发送。")
        XCTAssertEqual(receiver.messages.first { $0.status == .awaitingApproval }?.fileName, "b.bin")

        // A declined file is final and is not queued again.
        for message in receiver.messages where message.status == .awaitingApproval {
            receiver.decideIncomingFile(message.id, accepted: false)
        }
        while sender.messages.first(where: { $0.fileName == "b.bin" })?.status != .declined, Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(sender.messages.first { $0.fileName == "b.bin" }?.status, .declined)
        XCTAssertEqual(sender.outboxCount(receiverID), 0)
    }
}
