import XCTest
import FeigeIdentity
import Network
@testable import FeigeMac

/// A peer behind NAT cannot dial back, so the side that can must redial a direct peer whose connection dropped.
final class DropRedialTests: XCTestCase {
    @MainActor
    func testUnexpectedDropRedialsUntilManualDisconnect() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let a = "com.feige.redial.a.\(UUID())"
        let b = "com.feige.redial.b.\(UUID())"
        defer { try? FileManager.default.removeItem(at: root); EncryptedIdentityStore(directory: root.appendingPathComponent("a/Identity"), serviceIdentifier: a).delete(); EncryptedIdentityStore(directory: root.appendingPathComponent("b/Identity"), serviceIdentifier: b).delete() }
        let sender = TransferModel(supportDirectory: root.appendingPathComponent("a"), identityService: a)
        let receiver = TransferModel(supportDirectory: root.appendingPathComponent("b"), identityService: b)
        try sender.saveSettings(deviceName: "发送方", receiveDirectory: root.appendingPathComponent("unused"))
        try receiver.saveSettings(deviceName: "接收方", receiveDirectory: root.appendingPathComponent("received"))
        await sender.start()
        await receiver.start()
        let receiverID = try XCTUnwrap(AvatarID.normalizeFingerprint(receiver.identityFingerprint))
        let senderID = try XCTUnwrap(AvatarID.normalizeFingerprint(sender.identityFingerprint))

        let listener = try NWListener(using: .tcp, on: .any)
        defer { listener.cancel(); sender.shutdown(); receiver.shutdown() }
        let (ports, continuation) = AsyncStream<UInt16>.makeStream()
        listener.stateUpdateHandler = { [weak listener] state in
            if case .ready = state, let port = listener?.port?.rawValue { continuation.yield(port); continuation.finish() }
        }
        listener.newConnectionHandler = { connection in Task { @MainActor in receiver.accept(connection) } }
        listener.start(queue: DispatchQueue(label: "feige.redial.test.listener"))
        var iterator = ports.makeAsyncIterator()
        let nextPort = await iterator.next()
        let port = try XCTUnwrap(nextPort)
        // As probe discovery reports it: a nearby device carrying the peer's fingerprint.
        let device = LANDevice(serviceName: "Feige4-\(receiverID.prefix(20))", displayName: "接收方",
                               endpoint: .hostPort(host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: port)!),
                               protocolVersions: [3, 4, 5, 6], fingerprintHint: receiverID)

        var prompts = 0
        let watcher = Task { @MainActor in
            while !Task.isCancelled {
                if receiver.pendingConnectionPeerName != nil { prompts += 1; receiver.approveConnection(true) }
                do { try await Task.sleep(for: .milliseconds(10)) } catch { return }
            }
        }
        defer { watcher.cancel() }
        func waitUntil(_ condition: @MainActor () -> Bool) async throws {
            let deadline = Date().addingTimeInterval(30)
            while !condition(), Date() < deadline { try await Task.sleep(for: .milliseconds(10)) }
        }

        sender.nearbyDevices = [device]
        sender.requestConnection(to: device)
        try await waitUntil { sender.isConnected && receiver.isConnected }
        XCTAssertTrue(sender.isConnected)
        XCTAssertFalse(sender.autoDialPeerIDs().contains(receiverID), "a live connection needs no redial")

        // The receiver going away (as in a crash) is an unexpected drop for the sender, which arms the redial.
        XCTAssertEqual(AvatarID.normalizeFingerprint(receiver.connectedPeerID), senderID)
        receiver.disconnect(peerID: receiver.connectedPeerID)
        try await waitUntil { !sender.isConnected }
        XCTAssertFalse(sender.isConnected)
        XCTAssertTrue(sender.autoDialPeerIDs().contains(receiverID), "state: \(sender.connectionState)")
        XCTAssertFalse(sender.autoDialPeerIDs(now: Date().addingTimeInterval(TransferModel.dropRedialWindow + 1))
            .contains(receiverID), "the redial window ends")

        // Past the first backoff the discovered peer is dialed again without any user action.
        sender.nearbyDevices = [device]
        sender.connectTrustedPeers(now: Date().addingTimeInterval(31))
        try await waitUntil { sender.isConnected && receiver.isConnected }
        XCTAssertTrue(sender.isConnected, "state: \(sender.connectionState)")
        XCTAssertEqual(prompts, 1, "approval is asked once; the redial is approved without a prompt")
        XCTAssertTrue(receiver.isTrustedPeer(senderID), "accepting makes the peer a persistent contact")
        XCTAssertFalse(sender.autoDialPeerIDs().contains(receiverID), "connecting clears the redial")

        // A manual disconnect on this side never arms it.
        sender.disconnectByUser(peerID: sender.connectedPeerID)
        try await waitUntil { !sender.isConnected }
        XCTAssertFalse(sender.autoDialPeerIDs().contains(receiverID))
    }
}
