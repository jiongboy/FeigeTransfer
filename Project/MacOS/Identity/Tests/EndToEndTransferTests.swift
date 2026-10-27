import XCTest
import CryptoKit
import FeigeIdentity

/// End-to-end integration over the in-memory duplex transport: two peers complete the
/// signed handshake, exchange SAS confirmations, derive matching sessions, then move an
/// authenticated file (manifest → approval → chunk → finish → digest). This exercises the
/// whole stack together without opening a real socket, which is the last thing to verify
/// before wiring the real Network.framework adapter and testing on two real devices.
final class EndToEndTransferTests: XCTestCase {
    func testCompleteHandshakeApprovalAndFileTransfer() async throws {
        let aStore = DeviceIdentityStore(serviceIdentifier: "com.feige.e2e.a.\(UUID())")
        let bStore = DeviceIdentityStore(serviceIdentifier: "com.feige.e2e.b.\(UUID())")
        defer { _ = aStore.delete(); _ = bStore.delete() }
        let identityA = try aStore.loadOrCreate()
        let identityB = try bStore.loadOrCreate()

        let handshakeA = try FeigeHandshake(identity: identityA, role: .initiator, deviceName: "A", sessionCode: "123456")
        let handshakeB = try FeigeHandshake(identity: identityB, role: .responder, deviceName: "B", sessionCode: "123456")

        let (transportA, transportB) = InMemoryDuplexTransport.pair()

        // Both peers run the signed handshake concurrently over the duplex transport.
        async let pendingA = AuthenticatedHandshakeExchange.perform(local: handshakeA, transport: transportA)
        async let pendingB = AuthenticatedHandshakeExchange.perform(local: handshakeB, transport: transportB)
        let (a, b) = try await (pendingA, pendingB)

        // Signed handshake must yield identical SAS and cross-verified fingerprints.
        XCTAssertEqual(a.sas, b.sas)
        XCTAssertEqual(a.peerFingerprint, identityB.fingerprint)
        XCTAssertEqual(b.peerFingerprint, identityA.fingerprint)

        // Each peer confirms locally with the matching SAS and expected fingerprint.
        let flowA = PairingApprovalCoordinator(transcript: a.transcript, localRole: .initiator,
                                               expiresAt: Date().addingTimeInterval(60))
        let flowB = PairingApprovalCoordinator(transcript: b.transcript, localRole: .responder,
                                               expiresAt: Date().addingTimeInterval(60))
        let approvalA = try flowA.confirm(userEnteredSAS: a.sas, expectedPeerFingerprint: a.peerFingerprint)
        let approvalB = try flowB.confirm(userEnteredSAS: b.sas, expectedPeerFingerprint: b.peerFingerprint)
        try flowA.consume(approvalA, for: a.transcript, peer: a.peer)
        try flowB.consume(approvalB, for: b.transcript, peer: b.peer)

        let sessionA = try handshakeA.deriveSession(peer: a.peer, transcript: a.transcript,
                                                     localSignature: a.localSignature, peerSignature: a.peerSignature,
                                                     approval: approvalA)
        let sessionB = try handshakeB.deriveSession(peer: b.peer, transcript: b.transcript,
                                                     localSignature: b.localSignature, peerSignature: b.peerSignature,
                                                     approval: approvalB)
        XCTAssertEqual(sessionA.sendKey, sessionB.receiveKey)
        XCTAssertEqual(sessionA.receiveKey, sessionB.sendKey)

        // Move an authenticated file from A to B over AES-GCM records.
        let content = Data((0..<120_000).map { UInt8($0 % 251) })
        let digest = Data(SHA256.hash(data: content))
        let manifest = try FileManifest(fileName: "photo.bin", size: UInt64(content.count), sha256: digest)

        let sendStream = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: sessionA, direction: .send))
        let recvStream = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: sessionB, direction: .receive))
        let coordinator = TransferCoordinator()

        // A → manifest; B receives and waits for approval.
        try await transportA.send(sendStream.encode(type: TransferMessageType.manifest.rawValue,
                                                     payload: try JSONEncoder().encode(manifest)), timeout: 5)
        var receivedBytes = try await transportB.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 5)
        let manifestRecords = try recvStream.append(receivedBytes)
        XCTAssertEqual(manifestRecords.count, 1)
        let receivedManifest = try JSONDecoder().decode(FileManifest.self, from: manifestRecords[0].plaintext)
        XCTAssertEqual(receivedManifest, manifest)
        XCTAssertEqual(try coordinator.receiveManifest(receivedManifest).kind, .manifest(receivedManifest))

        // B approves with a temporary sink, sends decision back to A.
        var sink = Data()
        XCTAssertEqual(try coordinator.approve(transferID: receivedManifest.transferID, sink: { sink.append($0) }).kind,
                       .progress(0, manifest.size))
        try await transportB.send(try recvStream.encode(type: TransferMessageType.decision.rawValue, payload: Data([1])), timeout: 5)
        receivedBytes = try await transportA.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 5)
        let decision = try sendStream.append(receivedBytes)
        XCTAssertEqual(decision.count, 1)
        XCTAssertEqual(decision[0].type, TransferMessageType.decision.rawValue)

        // A streams chunks; B feeds the coordinator and tracks progress.
        var offset: UInt64 = 0
        let chunkSize = 60_000
        while offset < UInt64(content.count) {
            let end = Int(offset) + min(chunkSize, content.count - Int(offset))
            let chunk = Data(content[Int(offset)..<end])
            try await transportA.send(sendStream.encode(type: TransferMessageType.chunk.rawValue, payload: chunk), timeout: 5)
            receivedBytes = try await transportB.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 5)
            let records = try recvStream.append(receivedBytes)
            for record in records {
                let event = try coordinator.receiveChunk(offset: offset, payload: record.plaintext)
                XCTAssertEqual(event.kind, .progress(offset + UInt64(record.plaintext.count), manifest.size))
                offset += UInt64(record.plaintext.count)
            }
        }
        XCTAssertEqual(coordinator.state, .verifying)

        // A sends finish; B verifies digest and completes.
        try await transportA.send(sendStream.encode(type: TransferMessageType.finish.rawValue, payload: Data()), timeout: 5)
        receivedBytes = try await transportB.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 5)
        let finishRecords = try recvStream.append(receivedBytes)
        XCTAssertEqual(finishRecords.count, 1)
        XCTAssertEqual(finishRecords[0].type, TransferMessageType.finish.rawValue)
        XCTAssertNoThrow(try recvStream.endOfStream())
        XCTAssertEqual(try coordinator.finish().kind, .completed("photo.bin"))
        XCTAssertEqual(sink, content)

        transportA.close()
        transportB.close()
    }
}
