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

    func testGroupFileWireStrictDecodingRejectsUnknownFieldsAndInvalidDecisions() throws {
        let metadata = try FileMetadata(transferID: UUID(), fileName: "archive.bin", size: 9)
        let manifest = try GroupFileManifestWire(groupID: UUID(), messageID: UUID(),
            senderFingerprint: String(repeating: "A", count: 64), memberRevision: 1,
            metadata: metadata, sha256: Data(repeating: 5, count: 32))
        let valid = try JSONEncoder().encode(manifest)
        XCTAssertEqual(try GroupFileManifestWire.decodeStrict(valid), manifest)
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: valid) as? [String: Any])
        object["unexpected"] = true
        XCTAssertThrowsError(try GroupFileManifestWire.decodeStrict(JSONSerialization.data(withJSONObject: object)))

        let invalidDecision = GroupFileDecisionWire(groupID: UUID(), transferID: UUID(), accepted: false)
        XCTAssertThrowsError(try GroupFileDecisionWire.decodeStrict(JSONEncoder().encode(invalidDecision)))
        let badReceipt = try JSONSerialization.data(withJSONObject: [
            "groupID": UUID().uuidString, "transferID": UUID().uuidString,
            "sha256": Data(repeating: 1, count: 4).base64EncodedString()
        ])
        XCTAssertThrowsError(try GroupFileReceiptWire.decodeStrict(badReceipt))
    }

    func testV4GroupFileApprovalTransferAndDeclineKeepsConnectionOpen() async throws {
        let aStore = DeviceIdentityStore(serviceIdentifier: "com.feige.groupfile.a.\(UUID())")
        let bStore = DeviceIdentityStore(serviceIdentifier: "com.feige.groupfile.b.\(UUID())")
        defer { aStore.delete(); bStore.delete() }
        let identityA = try aStore.loadOrCreate(), identityB = try bStore.loadOrCreate()
        let (first, second) = InMemoryDuplexTransport.pair()
        async let sender = ConnectionService.establish(transport: first, identity: identityA, role: .initiator,
                                                       deviceName: "A", protocolVersion: 4)
        let receiver = try await ConnectionService.establish(transport: second, identity: identityB, role: .responder,
                                                              deviceName: "B", protocolVersion: 4) { _ in true }
        let a = try await sender
        let b = receiver
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let atA = root.appendingPathComponent("a", isDirectory: true)
        let atB = root.appendingPathComponent("b", isDirectory: true)
        try FileManager.default.createDirectory(at: atA, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: atB, withIntermediateDirectories: true)
        defer { a.close(); b.close(); try? FileManager.default.removeItem(at: root) }
        let probe = GroupFileProbe()
        let textOnB = MessageLog()
        let readerA = Task { try await a.run(receiveDirectory: atA, didReceive: { _, _ in }) }
        let readerB = Task { try await b.run(receiveDirectory: atB, didReceive: { _, _ in }, didReceiveText: {
            await textOnB.text($0)
        }, groupFileApproval: { _ in await probe.approveNext() },
            groupFileDidReceive: { manifest, url in await probe.received(manifest, url: url) }) }
        defer { readerA.cancel(); readerB.cancel() }

        let groupID = UUID()
        let firstBytes = Data((0..<900_000).map { UInt8($0 % 251) })
        let firstFile = root.appendingPathComponent("first.bin")
        try firstBytes.write(to: firstFile)
        let firstTransferID = UUID()
        let firstManifest = try await a.sendGroupFile(firstFile, groupID: groupID, messageID: UUID(),
            senderFingerprint: String(repeating: "A", count: 64), memberRevision: 1,
            transferID: firstTransferID)
        XCTAssertEqual(firstManifest.metadata.size, UInt64(firstBytes.count))
        XCTAssertEqual(try Data(contentsOf: atB.appendingPathComponent("first.bin")), firstBytes)
        let received = await probe.lastReceived
        XCTAssertEqual(received?.0.metadata.transferID, firstTransferID)
        XCTAssertEqual(try Data(contentsOf: try XCTUnwrap(received?.1)), firstBytes)

        await probe.setApproval(false)
        let secondFile = root.appendingPathComponent("second.bin")
        try Data("refused".utf8).write(to: secondFile)
        do {
            _ = try await a.sendGroupFile(secondFile, groupID: groupID, messageID: UUID(),
                senderFingerprint: String(repeating: "A", count: 64), memberRevision: 1)
            XCTFail("expected this recipient to reject the group file")
        } catch let error as TransferSessionError {
            guard case .peerRejected = error else { throw error }
        }
        let sentText = try await a.sendText("拒收文件后群聊连接继续工作")
        let texts = await textOnB.texts
        XCTAssertEqual(texts.map(\.id), [sentText.id])
        a.close(); b.close()
        _ = try? await readerA.value
        _ = try? await readerB.value
    }

    func testV4HandshakeAndGroupFrameVersionGate() async throws {
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.v4.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.v4.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let identityA = try a.loadOrCreate(), identityB = try b.loadOrCreate()
        let (first, second) = InMemoryDuplexTransport.pair()
        async let sender = ConnectionService.establish(transport: first, identity: identityA, role: .initiator,
                                                       deviceName: "A", protocolVersion: 4)
        let receiver = try await ConnectionService.establish(transport: second, identity: identityB,
                                                              role: .responder, deviceName: "B") { _ in true }
        let initiator = try await sender
        XCTAssertEqual(initiator.protocolVersion, 4)
        XCTAssertEqual(receiver.protocolVersion, 4)
        try await initiator.sendGroupFrame(.groupText, payload: Data("group-frame".utf8))
        initiator.close(); receiver.close()

        let (v3Sender, v3Receiver) = try await connections()
        defer { v3Sender.close(); v3Receiver.close() }
        do {
            try await v3Sender.sendGroupFrame(.groupText, payload: Data("must-not-send".utf8))
            XCTFail("group frames must be rejected on v3")
        } catch { }
    }

    func testV5HandshakeNegotiatesDurableMembershipCancellation() async throws {
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.v5.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.v5.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let identityA = try a.loadOrCreate(), identityB = try b.loadOrCreate()
        let (first, second) = InMemoryDuplexTransport.pair()
        async let sender = ConnectionService.establish(transport: first, identity: identityA, role: .initiator,
                                                       deviceName: "A", protocolVersion: 5)
        let receiver = try await ConnectionService.establish(transport: second, identity: identityB,
            role: .responder, deviceName: "B") { _ in true }
        let initiator = try await sender
        XCTAssertEqual(initiator.protocolVersion, 5)
        XCTAssertEqual(receiver.protocolVersion, 5)
        XCTAssertTrue(initiator.supportsDurableMembershipCancellation)
        XCTAssertTrue(receiver.supportsDurableMembershipCancellation)
        initiator.close(); receiver.close()
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

    func testIndependentConnectionsDoNotShareReadersOrCloseState() async throws {
        let firstPair = try await connections()
        let secondPair = try await connections()
        let (firstA, firstB) = firstPair
        let (secondA, secondB) = secondPair
        defer { firstA.close(); firstB.close(); secondA.close(); secondB.close() }

        let firstLog = MessageLog(), secondLog = MessageLog()
        let firstReaderA = Task { try await firstA.run(receiveDirectory: FileManager.default.temporaryDirectory,
                                                       didReceive: { _, _ in }, didReceiveText: { await firstLog.text($0) }) }
        let firstReaderB = Task { try await firstB.run(receiveDirectory: FileManager.default.temporaryDirectory,
                                                       didReceive: { _, _ in }, didReceiveText: { await firstLog.text($0) }) }
        let secondReaderA = Task { try await secondA.run(receiveDirectory: FileManager.default.temporaryDirectory,
                                                         didReceive: { _, _ in }, didReceiveText: { await secondLog.text($0) }) }
        let secondReaderB = Task { try await secondB.run(receiveDirectory: FileManager.default.temporaryDirectory,
                                                         didReceive: { _, _ in }, didReceiveText: { await secondLog.text($0) }) }
        defer { firstReaderA.cancel(); firstReaderB.cancel(); secondReaderA.cancel(); secondReaderB.cancel() }

        let firstMessage = try await firstA.sendText("第一条连接")
        let secondMessage = try await secondA.sendText("第二条连接")
        let firstTexts = await firstLog.texts.map(\.text)
        let secondTexts = await secondLog.texts.map(\.text)
        XCTAssertEqual(firstTexts, [firstMessage.text])
        XCTAssertEqual(secondTexts, [secondMessage.text])

        firstA.close()
        firstB.close()
        _ = try? await firstReaderA.value
        _ = try? await firstReaderB.value

        let afterClose = try await secondA.sendText("另一条连接仍然工作")
        let textsAfterClose = await secondLog.texts.map(\.text)
        XCTAssertEqual(textsAfterClose, [secondMessage.text, afterClose.text])
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

private actor GroupFileProbe {
    private var shouldApprove = true
    private(set) var lastReceived: (GroupFileManifestWire, URL)?
    func approveNext() -> Bool { shouldApprove }
    func setApproval(_ value: Bool) { shouldApprove = value }
    func received(_ manifest: GroupFileManifestWire, url: URL) {
        lastReceived = (manifest, url)
    }
}

private actor MessageLog {
    var texts: [ChatText] = []
    var files: [FileMetadata] = []
    func text(_ value: ChatText) { texts.append(value) }
    func file(_ value: FileMetadata) { files.append(value) }
}
