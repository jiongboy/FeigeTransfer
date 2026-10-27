import XCTest
import CryptoKit
@testable import FeigeIdentity

final class AuthenticatedRecordStreamTests: XCTestCase {
    private func sessions() -> (HandshakeSession, HandshakeSession) {
        let aToB = SymmetricKey(data: Data(repeating: 0x21, count: 32))
        let bToA = SymmetricKey(data: Data(repeating: 0x42, count: 32))
        let digest = Data(repeating: 0x55, count: 32)
        let aPrefix = Data([1, 2, 3, 4, 0x49, 0x4E, 0x49, 0x54])
        let bPrefix = Data([1, 2, 3, 4, 0x52, 0x45, 0x53, 0x50])
        let a = HandshakeSession(sendKey: aToB, receiveKey: bToA, transcriptHash: digest, sas: "123456",
                                 sendNoncePrefix: aPrefix, receiveNoncePrefix: bPrefix)
        let b = HandshakeSession(sendKey: bToA, receiveKey: aToB, transcriptHash: digest, sas: "123456",
                                 sendNoncePrefix: bPrefix, receiveNoncePrefix: aPrefix)
        return (a, b)
    }

    func testFragmentedAndCoalescedRecordsRoundTripThenFinish() throws {
        let (a, b) = sessions()
        let sender = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: a, direction: .send))
        let receiver = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: b, direction: .receive))
        let first = try sender.encode(type: TransferMessageType.manifest.rawValue, payload: Data("offer".utf8))
        let second = try sender.encode(type: TransferMessageType.chunk.rawValue, payload: Data("chunk".utf8))
        let finish = try sender.encode(type: TransferMessageType.finish.rawValue, payload: Data("done".utf8))
        let coalesced = first + second + finish
        var decoded: [OpenedRecord] = []
        for byte in coalesced { decoded += try receiver.append(Data([byte])) }
        XCTAssertEqual(decoded.map(\.type), [1, 3, 7])
        XCTAssertEqual(decoded.map { String(data: $0.plaintext, encoding: .utf8)! }, ["offer", "chunk", "done"])
        XCTAssertNoThrow(try receiver.endOfStream())
        XCTAssertThrowsError(try receiver.append(Data([0])))
    }

    func testTruncatedFinishAndTamperedFrameFailClosed() throws {
        let (a, b) = sessions()
        let sender = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: a, direction: .send))
        let receiver = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: b, direction: .receive))
        let record = try sender.encode(type: TransferMessageType.manifest.rawValue, payload: Data("offer".utf8))
        XCTAssertNoThrow(try receiver.append(record.prefix(3)))
        XCTAssertThrowsError(try receiver.endOfStream())

        var bad = record
        bad[bad.index(before: bad.endIndex)] ^= 0x80
        let otherReceiver = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: b, direction: .receive))
        XCTAssertThrowsError(try otherReceiver.append(bad))
    }

    func testPartialFrameFollowedByMaximumReadBatchStaysBoundedAndDecodes() throws {
        let (a, b) = sessions()
        let sender = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: a, direction: .send))
        let receiver = AuthenticatedRecordStream(cipher: SecureRecordCipher(session: b, direction: .receive))
        var wire = Data()
        for _ in 0..<10 { wire += try sender.encode(type: 3, payload: Data(repeating: 42, count: 65536)) }
        wire += try sender.encode(type: 7, payload: Data())
        let split = 30_000
        let end = split + AuthenticatedRecordStream.maxReadBatchLength
        XCTAssertTrue(try receiver.append(Data(wire[..<split])).isEmpty)
        var records = try receiver.append(Data(wire[split..<end]))
        records += try receiver.append(Data(wire[end...]))
        XCTAssertEqual(records.count, 11)
        XCTAssertNoThrow(try receiver.endOfStream())
    }
}
