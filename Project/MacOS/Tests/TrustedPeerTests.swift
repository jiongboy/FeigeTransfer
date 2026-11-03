import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

final class TrustedPeerTests: XCTestCase {
    func testSettingsKeepOnlyNormalizedUniqueFingerprints() throws {
        let upper = String(repeating: "AB", count: 32)
        let json = """
        {"deviceName":"A","receiveDirectory":"file:///tmp/","firstRunComplete":true,
         "trustedPeers":["\(upper)","\(upper.lowercased())","not-a-fingerprint"]}
        """
        let settings = try JSONDecoder().decode(DeviceSettings.self, from: Data(json.utf8))
        XCTAssertEqual(settings.trustedPeers, [upper])
        let legacy = try JSONDecoder().decode(DeviceSettings.self, from: Data(
            #"{"deviceName":"A","receiveDirectory":"file:///tmp/","firstRunComplete":true}"#.utf8))
        XCTAssertEqual(legacy.trustedPeers, [])
    }

    @MainActor
    func testTrustedPeerSkipsConnectionPromptButNotFilePrompt() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.trusted.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.trusted.b.\(UUID())")
        defer { try? FileManager.default.removeItem(at: root); a.delete(); b.delete() }
        let sender = TransferModel(supportDirectory: root.appendingPathComponent("a"), identityStore: a)
        let receiver = TransferModel(supportDirectory: root.appendingPathComponent("b"), identityStore: b)
        try sender.saveSettings(deviceName: "发送方", receiveDirectory: root.appendingPathComponent("unused"))
        try receiver.saveSettings(deviceName: "接收方", receiveDirectory: root.appendingPathComponent("received"))
        await sender.start()
        await receiver.start()
        let senderID = try XCTUnwrap(AvatarID.normalizeFingerprint(sender.identityFingerprint))
        try receiver.setTrustedPeer(senderID, trusted: true)
        XCTAssertTrue(receiver.isTrustedPeer(senderID))
        XCTAssertEqual(TransferModel(supportDirectory: root.appendingPathComponent("b"), identityStore: b)
            .settings.trustedPeers, [senderID])

        let listener = try NWListener(using: .tcp, on: .any)
        defer { listener.cancel(); sender.shutdown(); receiver.shutdown() }
        let (ports, continuation) = AsyncStream<UInt16>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue { continuation.yield(port); continuation.finish() }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in receiver.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.trusted.test.listener"))
        var iterator = ports.makeAsyncIterator()
        let nextPort = await iterator.next()
        let port = try XCTUnwrap(nextPort)
        let device = try sender.manualDevice(host: "127.0.0.1", port: String(port))

        var prompted = false
        let watcher = Task { @MainActor in
            while !Task.isCancelled {
                if receiver.pendingConnectionPeerName != nil { prompted = true; receiver.approveConnection(true) }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { watcher.cancel() }
        sender.requestConnection(to: device)
        let deadline = Date().addingTimeInterval(30)
        while !(sender.isConnected && receiver.isConnected), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(receiver.isConnected)
        XCTAssertFalse(prompted, "a persistent contact must be approved without a prompt")

        let file = root.appendingPathComponent("a.bin")
        try Data(repeating: 7, count: 1000).write(to: file)
        sender.startSending([file])
        while !receiver.messages.contains(where: { $0.status == .awaitingApproval }), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(receiver.messages.contains(where: { $0.status == .awaitingApproval }),
                      "file approval stays required for persistent contacts")

        // Revoking keeps the existing connection; the next connection asks again.
        try receiver.setTrustedPeer(senderID, trusted: false)
        XCTAssertTrue(receiver.isConnected)
        for message in receiver.messages where message.status == .awaitingApproval {
            receiver.decideIncomingFile(message.id, accepted: false)
        }
        sender.disconnectByUser(peerID: sender.connectedPeerID)
        let reconnectDeadline = Date().addingTimeInterval(30)
        while receiver.isConnected, Date() < reconnectDeadline { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertFalse(receiver.isConnected)
        sender.requestConnection(to: device)
        while !prompted, Date() < reconnectDeadline { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(prompted, "state: \(sender.connectionState) / \(receiver.connectionState)")
    }
}
