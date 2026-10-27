import XCTest
import CryptoKit
import FeigeIdentity

/// Thread-safe sink box so the `@Sendable` receive sink can append without capturing a
/// mutable local in concurrently-executing code.
private final class SinkBox: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    func append(_ chunk: Data) { lock.withLock { data.append(chunk) } }
    var bytes: Data { lock.withLock { data } }
}

/// Exercises `TransferSession` — the orchestrator that wires the directional record streams
/// to the `TransferCoordinator` state machine — over the in-memory duplex transport. This is
/// the reusable send/receive path the app will call once a real Network.framework adapter
/// connects two devices; here it runs without a socket so the whole flow is verifiable.
final class TransferSessionTests: XCTestCase {
    private func makeSessions() async throws -> (HandshakeSession, HandshakeSession, InMemoryDuplexTransport, InMemoryDuplexTransport) {
        let aStore = DeviceIdentityStore(serviceIdentifier: "com.feige.session.a.\(UUID())")
        let bStore = DeviceIdentityStore(serviceIdentifier: "com.feige.session.b.\(UUID())")
        defer { _ = aStore.delete(); _ = bStore.delete() }
        let identityA = try aStore.loadOrCreate()
        let identityB = try bStore.loadOrCreate()
        let handshakeA = try FeigeHandshake(identity: identityA, role: .initiator, deviceName: "A", sessionCode: "111111")
        let handshakeB = try FeigeHandshake(identity: identityB, role: .responder, deviceName: "B", sessionCode: "111111")
        let (transportA, transportB) = InMemoryDuplexTransport.pair()
        async let pendingA = AuthenticatedHandshakeExchange.perform(local: handshakeA, transport: transportA)
        async let pendingB = AuthenticatedHandshakeExchange.perform(local: handshakeB, transport: transportB)
        let (a, b) = try await (pendingA, pendingB)
        let flowA = PairingApprovalCoordinator(transcript: a.transcript, localRole: .initiator, expiresAt: Date().addingTimeInterval(60))
        let flowB = PairingApprovalCoordinator(transcript: b.transcript, localRole: .responder, expiresAt: Date().addingTimeInterval(60))
        let approvalA = try flowA.confirm(userEnteredSAS: a.sas, expectedPeerFingerprint: a.peerFingerprint)
        let approvalB = try flowB.confirm(userEnteredSAS: b.sas, expectedPeerFingerprint: b.peerFingerprint)
        let sessionA = try handshakeA.deriveSession(peer: a.peer, transcript: a.transcript, localSignature: a.localSignature, peerSignature: a.peerSignature, approval: approvalA)
        let sessionB = try handshakeB.deriveSession(peer: b.peer, transcript: b.transcript, localSignature: b.localSignature, peerSignature: b.peerSignature, approval: approvalB)
        return (sessionA, sessionB, transportA, transportB)
    }

    private func makeTempFile(bytes: Int) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("feige-\(UUID()).bin")
        let content = Data((0..<bytes).map { UInt8($0 % 251) })
        try content.write(to: url)
        return url
    }

    func testMissingFinishNeverCompletes() async throws {
        let (sessionA, sessionB, transportA, transportB) = try await makeSessions()
        let sender = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: sessionA, direction: .send))
        let receiver = TransferSession(session: sessionB, transport: transportB)
        let data = Data([42])
        let manifest = try FileManifest(fileName: "one.bin", size: 1, sha256: Data(SHA256.hash(data: data)))
        async let receiving = receiver.receive(approve: { _ in true }, sink: { _ in })
        try await transportA.send(sender.encode(type: 1, payload: JSONEncoder().encode(manifest)), timeout: 1)
        _ = try await transportA.receive(maximumLength: 1000, timeout: 1)
        try await transportA.send(sender.encode(type: 3, payload: data), timeout: 1)
        transportA.close()
        do { _ = try await receiving; XCTFail("missing finish must fail") } catch { }
    }

    func testMalformedDecisionAndReceiptAreRejected() async throws {
        for badDecision in [true, false] {
            let (sessionA, sessionB, a, b) = try await makeSessions()
            defer { a.close(); b.close() }
            let outgoing = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: sessionB, direction: .send))
            let incoming = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: sessionB, direction: .receive))
            let sender = TransferSession(session: sessionA, transport: a)
            let file = try makeTempFile(bytes: 1024)
            defer { try? FileManager.default.removeItem(at: file) }
            async let sending: Void = sender.send(fileURL: file)
            _ = try incoming.append(await b.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 1))
            try await b.send(outgoing.encode(type: 2, payload: badDecision ? Data([1, 0]) : Data([1])), timeout: 1)
            if !badDecision {
                var finished = false
                while !finished {
                    let bytes = try await b.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 1)
                    finished = try incoming.append(bytes).contains { $0.type == 7 }
                }
                try await b.send(outgoing.encode(type: 4, payload: Data([0])), timeout: 1)
            }
            do { try await sending; XCTFail("malformed control payload must fail") }
            catch let error as TransferCoordinatorError {
                guard case .invalidDecision = error else { return XCTFail("unexpected error") }
            }
        }
    }

    func testFinalizeFailureNeverSendsSuccessReceipt() async throws {
        let (sessionA, sessionB, transportA, transportB) = try await makeSessions()
        let sender = TransferSession(session: sessionA, transport: transportA)
        let receiver = TransferSession(session: sessionB, transport: transportB)
        let fileURL = try makeTempFile(bytes: 1024)
        defer { try? FileManager.default.removeItem(at: fileURL) }
        async let sending: Void = sender.send(fileURL: fileURL)
        do {
            _ = try await receiver.receive(approve: { _ in true }, sink: { _ in }, finalize: {
                throw TransferSessionError.ioFailure
            })
            XCTFail("failed promotion must fail receiving")
        } catch { }
        do { try await sending; XCTFail("failed promotion must fail sending") } catch { }
    }

    func testUnexpectedRecordFailsInsteadOfBufferingForever() async throws {
        let (sessionA, sessionB, transportA, transportB) = try await makeSessions()
        let stream = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: sessionA, direction: .send))
        let receiver = TransferSession(session: sessionB, transport: transportB)
        try await transportA.send(stream.encode(type: 5, payload: Data([1])), timeout: 1)
        do {
            _ = try await receiver.receive(approve: { _ in XCTFail("must not prompt"); return true }, sink: { _ in XCTFail("must not write") })
            XCTFail("reserved cancel record must terminate v1")
        } catch let error as TransferSessionError {
            guard case .unexpectedRecord(5) = error else { return XCTFail("unexpected error") }
        }
    }

    func testSessionTransfersFileAndReportsProgress() async throws {
        let (sessionA, sessionB, transportA, transportB) = try await makeSessions()
        let sender = TransferSession(session: sessionA, transport: transportA)
        let receiver = TransferSession(session: sessionB, transport: transportB)
        let fileURL = try makeTempFile(bytes: 250_000)
        defer { try? FileManager.default.removeItem(at: fileURL) }

        let sink = SinkBox()
        async let sendTask: Void = sender.send(fileURL: fileURL)
        let receivedFile = try await receiver.receive(approve: { _ in true }, sink: { sink.append($0) })
        try await sendTask

        XCTAssertEqual(receivedFile.fileName, fileURL.lastPathComponent)
        XCTAssertEqual(receivedFile.size, 250_000)
        XCTAssertEqual(sink.bytes, try Data(contentsOf: fileURL))
        transportA.close()
        transportB.close()
    }

    func testSessionRejectsWhenApproverDeclines() async throws {
        let (sessionA, sessionB, transportA, transportB) = try await makeSessions()
        let sender = TransferSession(session: sessionA, transport: transportA)
        let receiver = TransferSession(session: sessionB, transport: transportB)
        let fileURL = try makeTempFile(bytes: 1_024)
        defer { try? FileManager.default.removeItem(at: fileURL) }

        async let sendTask: Void = sender.send(fileURL: fileURL)
        do {
            _ = try await receiver.receive(approve: { _ in false }, sink: { _ in XCTFail("sink must not be written on reject") })
            XCTFail("expected peerRejected")
        } catch let error as TransferSessionError {
            guard case .peerRejected = error else { return XCTFail("unexpected \(error)") }
        }
        do {
            try await sendTask
            XCTFail("expected sender to observe rejection")
        } catch let error as TransferSessionError {
            guard case .peerRejected = error else { return XCTFail("unexpected \(error)") }
        }
        transportA.close()
        transportB.close()
    }
}
