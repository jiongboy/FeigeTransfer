import XCTest
import FeigeIdentity

final class PairingApprovalCoordinatorTests: XCTestCase {
    func testApprovalMustMatchPeerAndSASAndCanOnlyBeConsumedOnce() throws {
        let aStore = DeviceIdentityStore(serviceIdentifier: "com.feige.pairing.a.\(UUID())")
        let bStore = DeviceIdentityStore(serviceIdentifier: "com.feige.pairing.b.\(UUID())")
        defer { _ = aStore.delete(); _ = bStore.delete() }
        let a = try FeigeHandshake(identity: aStore.loadOrCreate(), role: .initiator, deviceName: "A", sessionCode: "123456")
        let b = try FeigeHandshake(identity: bStore.loadOrCreate(), role: .responder, deviceName: "B", sessionCode: "123456")
        let transcript = try HandshakeTranscript.validated(local: a.offer, peer: b.offer)
        let coordinator = PairingApprovalCoordinator(transcript: transcript, localRole: .initiator,
                                                     expiresAt: Date().addingTimeInterval(120))
        XCTAssertThrowsError(try coordinator.confirm(userEnteredSAS: "000000", expectedPeerFingerprint: bStore.loadOrCreate().fingerprint))
        XCTAssertThrowsError(try coordinator.confirm(userEnteredSAS: transcript.sas, expectedPeerFingerprint: aStore.loadOrCreate().fingerprint))
        let approval = try coordinator.confirm(userEnteredSAS: transcript.sas,
                                               expectedPeerFingerprint: bStore.loadOrCreate().fingerprint)
        XCTAssertEqual(coordinator.state, .confirmed)
        XCTAssertNoThrow(try coordinator.consume(approval, for: transcript, peer: b.offer))
        XCTAssertEqual(coordinator.state, .consumed)
        XCTAssertThrowsError(try coordinator.consume(approval, for: transcript, peer: b.offer))
    }

    func testApprovalExpiresAndIsTranscriptBound() throws {
        let aStore = DeviceIdentityStore(serviceIdentifier: "com.feige.pairing.exp.a.\(UUID())")
        let bStore = DeviceIdentityStore(serviceIdentifier: "com.feige.pairing.exp.b.\(UUID())")
        defer { _ = aStore.delete(); _ = bStore.delete() }
        let a = try FeigeHandshake(identity: aStore.loadOrCreate(), role: .initiator, deviceName: "A", sessionCode: "654321")
        let b = try FeigeHandshake(identity: bStore.loadOrCreate(), role: .responder, deviceName: "B", sessionCode: "654321")
        let transcript = try HandshakeTranscript.validated(local: a.offer, peer: b.offer)
        let expired = PairingApprovalCoordinator(transcript: transcript, localRole: .initiator, expiresAt: Date(timeIntervalSince1970: 1))
        XCTAssertThrowsError(try expired.confirm(userEnteredSAS: transcript.sas, expectedPeerFingerprint: bStore.loadOrCreate().fingerprint, at: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(expired.state, .expired)
        XCTAssertThrowsError(try HandshakeTranscript.validated(local: a.offer,
                                                                peer: try HandshakeOffer(role: .responder, deviceName: "B", identityPublicKeyDER: b.offer.identityPublicKeyDER,
                                                                                         ephemeralPublicKey: b.offer.ephemeralPublicKey, nonce: b.offer.nonce, sessionCode: "000000")))
    }
}
