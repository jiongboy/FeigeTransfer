import XCTest
import CryptoKit
@testable import FeigeIdentity

final class ChatConnectionTests: XCTestCase {
    private func connections() async throws -> (FileConnection, FileConnection) {
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.chat.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.chat.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let identityA = try a.loadOrCreate(), identityB = try b.loadOrCreate()
        let (first, second) = InMemoryDuplexTransport.pair()
        async let sender = ConnectionService.establish(transport: first, identity: identityA, role: .initiator, deviceName: "A")
        let receiver = try await ConnectionService.establish(transport: second, identity: identityB, role: .responder, deviceName: "B") { _ in true }
        return (try await sender, receiver)
    }

    func testTextAndOriginalImageCanTravelDuringDuplexFileTransfers() async throws {
        let (a, b) = try await connections()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let atA = root.appendingPathComponent("a"), atB = root.appendingPathComponent("b")
        try FileManager.default.createDirectory(at: atA, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: atB, withIntermediateDirectories: true)
        defer { a.close(); b.close(); try? FileManager.default.removeItem(at: root) }
        let bytes = Data(repeating: 42, count: 2_000_000)
        let file = root.appendingPathComponent("original.png")
        try bytes.write(to: file)
        let logA = MessageLog(), logB = MessageLog()
        let readerA = Task { try await a.run(receiveDirectory: atA, didReceive: { metadata, _ in
            await logA.file(metadata)
        }, didReceiveText: { await logA.text($0) }) }
        let readerB = Task { try await b.run(receiveDirectory: atB, didReceive: { metadata, _ in
            await logB.file(metadata)
        }, didReceiveText: { await logB.text($0) }) }
        defer { readerA.cancel(); readerB.cancel() }
        let body = "你好 👋\n保留换行、空格和 <标签> & 引号\""
        async let forward = a.sendFile(file, kind: .image)
        async let reverse = b.sendFile(file)
        async let textA = a.sendText(body)
        async let textB = b.sendText("收到，正在发送原图 🖼️")
        let (_, _, sentA, sentB) = try await (forward, reverse, textA, textB)
        let textsA = await logA.texts, textsB = await logB.texts
        XCTAssertEqual(textsB, [sentA]); XCTAssertEqual(textsA, [sentB])
        XCTAssertEqual(try Data(contentsOf: atB.appendingPathComponent("original.png")), bytes)
        XCTAssertEqual(try Data(contentsOf: atA.appendingPathComponent("original.png")), bytes)
        // File receipt precedes the local completion callback; drain it before assertions.
        a.close(); b.close()
        _ = try? await readerA.value; _ = try? await readerB.value
        let filesB = await logB.files, filesA = await logA.files
        XCTAssertEqual(filesB.first?.kind, .image); XCTAssertEqual(filesA.first?.kind, .file)
    }

    func testInvalidTextNeverStartsNetworkTrafficAndValidTextRemainsPossible() async throws {
        let (a, b) = try await connections()
        defer { a.close(); b.close() }
        let reader = Task { try await b.run(receiveDirectory: FileManager.default.temporaryDirectory, didReceive: { _, _ in }) }
        let otherReader = Task { try await a.run(receiveDirectory: FileManager.default.temporaryDirectory, didReceive: { _, _ in }) }
        defer { reader.cancel(); otherReader.cancel() }
        for invalid in [" \n\t", String(repeating: "中", count: ChatText.maxUTF8Bytes)] {
            do { _ = try await a.sendText(invalid); XCTFail("must reject invalid text") } catch is ChatError { }
        }
        let maxEscaping = String(repeating: "\u{0001}", count: ChatText.maxUTF8Bytes)
        XCTAssertLessThan(try JSONEncoder().encode(ChatText(text: maxEscaping)).count, SecureRecordCipher.maxPlaintextLength)
        let sent = try await a.sendText("仍然可以聊天")
        XCTAssertEqual(sent.text, "仍然可以聊天")
        a.close(); b.close()
        _ = try? await reader.value; _ = try? await otherReader.value
    }

    func testPeerAuthorizationCanExceedNetworkTimeoutWithoutDroppingConnection() async throws {
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.slowauth.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.slowauth.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let identityA = try a.loadOrCreate(), identityB = try b.loadOrCreate()
        let (first, second) = InMemoryDuplexTransport.pair()
        defer { first.close(); second.close() }
        async let sender = ConnectionService.establish(transport: first, identity: identityA, role: .initiator,
                                                       deviceName: "A", handshakeTimeout: 0.15, identityAuthorizationTimeout: 2)
        let receiver = try await ConnectionService.establish(transport: second, identity: identityB, role: .responder,
                                                              deviceName: "B", handshakeTimeout: 0.15,
                                                              identityAuthorizationTimeout: 2, onStage: { stage in
            if case .authorizingIdentity = stage { try? await Task.sleep(for: .milliseconds(450)) }
        }) { _ in true }
        let connected = try await sender
        XCTAssertEqual(connected.peerName, "B")
        connected.close(); receiver.close()
    }

    func testV3RefusesV2AndDecodingValidatesTextAndMediaKind() throws {
        let old = try HandshakeOffer(version: 2, role: .initiator, deviceName: "Old", identityPublicKeyDER: Data([1]), ephemeralPublicKey: Data(repeating: 1, count: 32), nonce: Data(repeating: 1, count: 32), sessionCode: "")
        let new = try HandshakeOffer(version: 3, role: .responder, deviceName: "New", identityPublicKeyDER: Data([2]), ephemeralPublicKey: Data(repeating: 2, count: 32), nonce: Data(repeating: 2, count: 32), sessionCode: "")
        XCTAssertThrowsError(try HandshakeTranscript.validated(local: old, peer: new))
        let badText = try JSONSerialization.data(withJSONObject: ["id": UUID().uuidString, "text": "   "])
        XCTAssertThrowsError(try JSONDecoder().decode(ChatText.self, from: badText))
        let badKind = try JSONSerialization.data(withJSONObject: ["transferID": UUID().uuidString, "fileName": "image.png", "size": 1, "kind": "executable"])
        XCTAssertThrowsError(try JSONDecoder().decode(FileMetadata.self, from: badKind))
    }
}

private actor MessageLog {
    var texts: [ChatText] = []
    var files: [FileMetadata] = []
    func text(_ value: ChatText) { texts.append(value) }
    func file(_ value: FileMetadata) { files.append(value) }
}
