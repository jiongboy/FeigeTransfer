import XCTest
import CryptoKit
@testable import FeigeIdentity

final class ConnectionFailureTests: XCTestCase {
    private func rawPair() -> (FileConnection, InMemoryDuplexTransport, AuthenticatedRecordStream) {
        let aKey = SymmetricKey(data: Data(repeating: 1, count: 32))
        let bKey = SymmetricKey(data: Data(repeating: 2, count: 32))
        let hash = Data(repeating: 3, count: 32)
        let a = HandshakeSession(sendKey: aKey, receiveKey: bKey, transcriptHash: hash, sas: "",
                                 sendNoncePrefix: Data([1, 2, 3, 4]), receiveNoncePrefix: Data([4, 3, 2, 1]))
        let b = HandshakeSession(sendKey: bKey, receiveKey: aKey, transcriptHash: hash, sas: "",
                                 sendNoncePrefix: Data([4, 3, 2, 1]), receiveNoncePrefix: Data([1, 2, 3, 4]))
        let (first, second) = InMemoryDuplexTransport.pair()
        let connection = FileConnection(session: b, transport: second, peerName: "Test", fingerprint: "Test")
        let raw = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: a, direction: .send), finishClosesStream: false)
        return (connection, first, raw)
    }

    func testCorruptionWrongOffsetAndMissingFinishRemovePartialFiles() async throws {
        for failure in ["hash", "offset", "finish"] {
            let (connection, sender, raw) = rawPair()
            let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            defer { connection.close(); sender.close(); try? FileManager.default.removeItem(at: root) }
            let receiving = Task {
                try await connection.run(receiveDirectory: root, didReceive: { _, _ in XCTFail("must not complete") })
            }
            let metadata = try FileMetadata(fileName: "test.bin", size: 1)
            try await sender.send(raw.encode(type: 1, payload: JSONEncoder().encode(metadata)), timeout: 1)
            _ = try await sender.receive(maximumLength: AuthenticatedRecordStream.maxReadBatchLength, timeout: 1)
            var uuid = metadata.transferID.uuid
            var chunk = withUnsafeBytes(of: &uuid) { Data($0) }
            var offset = UInt64(failure == "offset" ? 1 : 0).bigEndian
            withUnsafeBytes(of: &offset) { chunk.append(contentsOf: $0) }
            chunk.append(42)
            try await sender.send(raw.encode(type: 3, payload: chunk), timeout: 1)
            if failure == "hash" {
                let bad: [String: Any] = ["transferID": metadata.transferID.uuidString,
                                           "sha256": Data(repeating: 0, count: 32).base64EncodedString()]
                try await sender.send(raw.encode(type: 7, payload: JSONSerialization.data(withJSONObject: bad)), timeout: 1)
            } else if failure == "finish" { sender.close() }
            do { try await receiving.value; XCTFail("must reject \(failure)") } catch { }
            XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        }
    }

    func testInvalidNetworkMetadataNeverCreatesFile() async throws {
        let (connection, sender, raw) = rawPair()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { connection.close(); sender.close(); try? FileManager.default.removeItem(at: root) }
        let receiving = Task { try await connection.run(receiveDirectory: root, didReceive: { _, _ in XCTFail("must not complete") }) }
        let bad: [String: Any] = ["transferID": UUID().uuidString, "fileName": "../escape", "size": 1]
        try await sender.send(raw.encode(type: 1, payload: JSONSerialization.data(withJSONObject: bad)), timeout: 1)
        do { try await receiving.value; XCTFail("must reject traversal") } catch { }
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    }
}
