import XCTest
import FeigeIdentity

final class HandshakeTests: XCTestCase {
    func testPeersDeriveMatchingDirectionalKeysAfterSignatureAndSASChecks() throws {
        let leftStore = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.handshake.left.\(UUID())")
        let rightStore = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.handshake.right.\(UUID())")
        defer { _ = leftStore.delete(); _ = rightStore.delete() }
        let leftIdentity = try leftStore.loadOrCreate()
        let rightIdentity = try rightStore.loadOrCreate()
        let left = try FeigeHandshake(identity: leftIdentity, role: .initiator, deviceName: "Left", sessionCode: "123456")
        let right = try FeigeHandshake(identity: rightIdentity, role: .responder, deviceName: "Right", sessionCode: "123456")
        let transcript = try HandshakeTranscript.validated(local: left.offer, peer: right.offer)
        let leftSignature = try left.sign(transcript)
        let rightSignature = try right.sign(transcript)

        try FeigeHandshake.verify(leftSignature, transcript: transcript, signer: left.offer,
                                  expectedFingerprint: leftIdentity.fingerprint)
        try FeigeHandshake.verify(rightSignature, transcript: transcript, signer: right.offer,
                                  expectedFingerprint: rightIdentity.fingerprint)

        let leftApprovalFlow = PairingApprovalCoordinator(transcript: transcript, localRole: .initiator, expiresAt: Date().addingTimeInterval(60))
        let rightApprovalFlow = PairingApprovalCoordinator(transcript: transcript, localRole: .responder, expiresAt: Date().addingTimeInterval(60))
        let leftApproval = try leftApprovalFlow.confirm(userEnteredSAS: transcript.sas, expectedPeerFingerprint: rightIdentity.fingerprint)
        let rightApproval = try rightApprovalFlow.confirm(userEnteredSAS: transcript.sas, expectedPeerFingerprint: leftIdentity.fingerprint)
        let leftSession = try left.deriveSession(peer: right.offer, transcript: transcript,
                                                 localSignature: leftSignature, peerSignature: rightSignature,
                                                 approval: leftApproval)
        let rightSession = try right.deriveSession(peer: left.offer, transcript: transcript,
                                                   localSignature: rightSignature, peerSignature: leftSignature,
                                                   approval: rightApproval)
        XCTAssertThrowsError(try left.deriveSession(peer: right.offer, transcript: transcript,
                                                    localSignature: leftSignature, peerSignature: rightSignature,
                                                    approval: leftApproval))
        XCTAssertEqual(leftSession.transcriptHash, rightSession.transcriptHash)
        XCTAssertEqual(leftSession.sas, rightSession.sas)
        XCTAssertEqual(leftSession.sendKey, rightSession.receiveKey)
        XCTAssertEqual(leftSession.receiveKey, rightSession.sendKey)
        XCTAssertNotEqual(leftSession.sendKey, leftSession.receiveKey)
    }

    func testTranscriptMutationInvalidatesSignature() throws {
        let oneStore = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.mutation.one.\(UUID())")
        let twoStore = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.mutation.two.\(UUID())")
        defer { _ = oneStore.delete(); _ = twoStore.delete() }
        let one = try oneStore.loadOrCreate()
        let two = try twoStore.loadOrCreate()
        let left = try FeigeHandshake(identity: one, role: .initiator, deviceName: "One", sessionCode: "123456")
        let right = try FeigeHandshake(identity: two, role: .responder, deviceName: "Two", sessionCode: "123456")
        let transcript = try HandshakeTranscript.validated(local: left.offer, peer: right.offer)
        let signature = try left.sign(transcript)
        let changedPeer = try HandshakeOffer(role: .responder, deviceName: "Two", identityPublicKeyDER: right.offer.identityPublicKeyDER,
                                             ephemeralPublicKey: right.offer.ephemeralPublicKey, nonce: Data(repeating: 7, count: 32), sessionCode: "123456")
        let changedTranscript = try HandshakeTranscript.validated(local: left.offer, peer: changedPeer)
        XCTAssertThrowsError(try FeigeHandshake.verify(signature, transcript: changedTranscript, signer: left.offer,
                                                       expectedFingerprint: one.fingerprint))
    }

    func testBadFingerprintAndUnconfirmedSASAreRejected() throws {
        let oneStore = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.confirm.one.\(UUID())")
        let twoStore = DeviceIdentityStore(serviceIdentifier: "com.feige.tests.confirm.two.\(UUID())")
        defer { _ = oneStore.delete(); _ = twoStore.delete() }
        let one = try oneStore.loadOrCreate()
        let two = try twoStore.loadOrCreate()
        let left = try FeigeHandshake(identity: one, role: .initiator, deviceName: "One", sessionCode: "123456")
        let right = try FeigeHandshake(identity: two, role: .responder, deviceName: "Two", sessionCode: "123456")
        let transcript = try HandshakeTranscript.validated(local: left.offer, peer: right.offer)
        let a = try left.sign(transcript)
        let b = try right.sign(transcript)
        let coordinator = PairingApprovalCoordinator(transcript: transcript, localRole: .initiator,
                                                     expiresAt: Date().addingTimeInterval(60))
        let approval = try coordinator.confirm(userEnteredSAS: transcript.sas,
                                               expectedPeerFingerprint: two.fingerprint)
        XCTAssertNoThrow(try left.deriveSession(peer: right.offer, transcript: transcript,
                                                localSignature: a, peerSignature: b, approval: approval))
        XCTAssertThrowsError(try left.deriveSession(peer: right.offer, transcript: transcript,
                                                    localSignature: a, peerSignature: b, approval: approval))
    }
}
