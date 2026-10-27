import XCTest
import CryptoKit
@testable import FeigeIdentity

final class SecureRecordCipherTests: XCTestCase {
    private func testSession() -> HandshakeSession {
        HandshakeSession(sendKey: SymmetricKey(data: Data(repeating: 0x11, count: 32)),
                         receiveKey: SymmetricKey(data: Data(repeating: 0x22, count: 32)),
                         transcriptHash: Data(repeating: 0x33, count: 32), sas: "123456",
                         sendNoncePrefix: Data([0, 0, 0, 1, 0x49, 0x4E, 0x49, 0x54]),
                         receiveNoncePrefix: Data([0, 0, 0, 1, 0x52, 0x45, 0x53, 0x50]))
    }

    func testDirectionalRecordsRoundTripAndRejectReplay() throws {
        let session = testSession()
        let reverseSession = HandshakeSession(sendKey: session.receiveKey, receiveKey: session.sendKey,
                                              transcriptHash: session.transcriptHash, sas: session.sas,
                                              sendNoncePrefix: session.receiveNoncePrefix,
                                              receiveNoncePrefix: session.sendNoncePrefix)
        let sender = SecureRecordCipher(session: session, direction: .send)
        let receiver = SecureRecordCipher(session: reverseSession, direction: .receive)
        let frame = try sender.seal(type: 1, plaintext: Data("hello Feige".utf8))
        let opened = try receiver.open(frame)
        XCTAssertEqual(opened.type, 1)
        XCTAssertEqual(String(data: opened.plaintext, encoding: .utf8), "hello Feige")
        XCTAssertThrowsError(try receiver.open(frame))
    }

    func testHeaderTamperingAndWrongDirectionKeyFailAuthentication() throws {
        let session = testSession()
        let sender = SecureRecordCipher(session: session, direction: .send)
        let frame = try sender.seal(type: 2, plaintext: Data([1, 2, 3]))
        var tampered = frame
        tampered[0] = 99
        let receiver = SecureRecordCipher(session: session, direction: .receive)
        XCTAssertThrowsError(try receiver.open(tampered))

        let wrongSession = HandshakeSession(sendKey: session.sendKey,
                                            receiveKey: SymmetricKey(data: Data(repeating: 0x77, count: 32)),
                                            transcriptHash: session.transcriptHash, sas: session.sas,
                                            sendNoncePrefix: session.sendNoncePrefix,
                                            receiveNoncePrefix: session.receiveNoncePrefix)
        let wrongDirectionReceiver = SecureRecordCipher(session: wrongSession, direction: .receive)
        XCTAssertThrowsError(try wrongDirectionReceiver.open(frame))
    }

    func testOversizedRecordRejectedBeforeDecode() throws {
        let receiver = SecureRecordCipher(session: testSession(), direction: .receive)
        XCTAssertThrowsError(try receiver.open(Data(repeating: 0, count: 100_000)))
    }
}
