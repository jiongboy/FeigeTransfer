import XCTest
import CryptoKit
import FeigeIdentity

final class HandshakeTests: XCTestCase {
    func testSharedV4HandshakeTranscriptAndDirectionalHKDFVectors() throws {
        let fixtureURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Docs/Public/TestVectors/v4-handshake.json")
        let data = try Data(contentsOf: fixtureURL)
        let fixture = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(fixture["format"] as? String, "feige-v4-handshake-primitives")
        XCTAssertEqual(fixture["version"] as? Int, 4)

        func offer(_ key: String) throws -> HandshakeOffer {
            let object = try XCTUnwrap(fixture[key] as? [String: Any])
            let role = try XCTUnwrap(HandshakeRole(rawValue: UInt8(try XCTUnwrap(object["role"] as? Int))) )
            return try HandshakeOffer(
                version: UInt16(try XCTUnwrap(object["version"] as? Int)), role: role,
                deviceName: try XCTUnwrap(object["deviceName"] as? String),
                identityPublicKeyDER: try parseHex(hex: XCTUnwrap(object["identityPublicKeyDERHex"] as? String)),
                ephemeralPublicKey: try parseHex(hex: XCTUnwrap(object["ephemeralPublicKeyHex"] as? String)),
                nonce: try parseHex(hex: XCTUnwrap(object["nonceHex"] as? String)),
                sessionCode: try XCTUnwrap(object["sessionCode"] as? String))
        }

        let initiator = try offer("initiatorOffer")
        let responder = try offer("responderOffer")
        let transcript = try HandshakeTranscript.validated(local: initiator, peer: responder)
        let transcriptBytes = transcript.encoded()
        XCTAssertEqual(transcriptBytes, try parseHex(hex: XCTUnwrap(fixture["transcriptHex"] as? String)))
        XCTAssertEqual(transcript.digest, try parseHex(hex: XCTUnwrap(fixture["transcriptSha256Hex"] as? String)))
        XCTAssertEqual(try HandshakeTranscript.validated(local: responder, peer: initiator).encoded(), transcriptBytes)

        let sharedSecret = SymmetricKey(data: try parseHex(hex: XCTUnwrap(fixture["sharedSecretHex"] as? String)))
        let salt = transcript.digest
        let outputByteCount = try XCTUnwrap(fixture["hkdfOutputBytes"] as? Int)
        let initiatorToResponder = HKDF<SHA256>.deriveKey(inputKeyMaterial: sharedSecret, salt: salt,
            info: Data(try XCTUnwrap(fixture["hkdfInfoInitiatorToResponder"] as? String).utf8),
            outputByteCount: outputByteCount)
        let responderToInitiator = HKDF<SHA256>.deriveKey(inputKeyMaterial: sharedSecret, salt: salt,
            info: Data(try XCTUnwrap(fixture["hkdfInfoResponderToInitiator"] as? String).utf8),
            outputByteCount: outputByteCount)
        XCTAssertEqual(initiatorToResponder.withUnsafeBytes { Data($0) },
                       try parseHex(hex: XCTUnwrap(fixture["initiatorToResponderKeyHex"] as? String)))
        XCTAssertEqual(responderToInitiator.withUnsafeBytes { Data($0) },
                       try parseHex(hex: XCTUnwrap(fixture["responderToInitiatorKeyHex"] as? String)))
    }

    func testSharedV5HandshakeTranscriptAndDirectionalHKDFVectors() throws {
        try checkSharedVector(version: 5)
    }

    func testSharedV6HandshakeTranscriptAndDirectionalHKDFVectors() throws {
        try checkSharedVector(version: 6)
    }

    private func checkSharedVector(version: Int) throws {
        let fixtureURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Docs/Public/TestVectors/v\(version)-handshake.json")
        let data = try Data(contentsOf: fixtureURL)
        let fixture = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(fixture["format"] as? String, "feige-v\(version)-handshake-primitives")
        XCTAssertEqual(fixture["version"] as? Int, version)

        func offer(_ key: String) throws -> HandshakeOffer {
            let object = try XCTUnwrap(fixture[key] as? [String: Any])
            let role = try XCTUnwrap(HandshakeRole(rawValue: UInt8(try XCTUnwrap(object["role"] as? Int))))
            return try HandshakeOffer(
                version: UInt16(try XCTUnwrap(object["version"] as? Int)), role: role,
                deviceName: try XCTUnwrap(object["deviceName"] as? String),
                identityPublicKeyDER: try parseHex(hex: XCTUnwrap(object["identityPublicKeyDERHex"] as? String)),
                ephemeralPublicKey: try parseHex(hex: XCTUnwrap(object["ephemeralPublicKeyHex"] as? String)),
                nonce: try parseHex(hex: XCTUnwrap(object["nonceHex"] as? String)),
                sessionCode: try XCTUnwrap(object["sessionCode"] as? String))
        }

        let initiator = try offer("initiatorOffer")
        let responder = try offer("responderOffer")
        let transcript = try HandshakeTranscript.validated(local: initiator, peer: responder)
        let transcriptBytes = transcript.encoded()
        XCTAssertEqual(transcriptBytes, try parseHex(hex: XCTUnwrap(fixture["transcriptHex"] as? String)))
        XCTAssertEqual(transcript.digest, try parseHex(hex: XCTUnwrap(fixture["transcriptSha256Hex"] as? String)))
        XCTAssertEqual(try HandshakeTranscript.validated(local: responder, peer: initiator).encoded(), transcriptBytes)

        let sharedBytes = try XCTUnwrap(fixture["sharedSecretBytes"] as? [Int]).map(UInt8.init)
        let sharedSecret = SymmetricKey(data: Data(sharedBytes))
        let outputByteCount = try XCTUnwrap(fixture["hkdfOutputBytes"] as? Int)
        let initiatorToResponder = HKDF<SHA256>.deriveKey(inputKeyMaterial: sharedSecret, salt: transcript.digest,
            info: Data(try XCTUnwrap(fixture["hkdfInfoInitiatorToResponder"] as? String).utf8),
            outputByteCount: outputByteCount)
        let responderToInitiator = HKDF<SHA256>.deriveKey(inputKeyMaterial: sharedSecret, salt: transcript.digest,
            info: Data(try XCTUnwrap(fixture["hkdfInfoResponderToInitiator"] as? String).utf8),
            outputByteCount: outputByteCount)
        let expectedInitiatorToResponder = Data(try XCTUnwrap(fixture["initiatorToResponderKeyBytes"] as? [Int]).map(UInt8.init))
        let expectedResponderToInitiator = Data(try XCTUnwrap(fixture["responderToInitiatorKeyBytes"] as? [Int]).map(UInt8.init))
        XCTAssertEqual(initiatorToResponder.withUnsafeBytes { Data($0) }, expectedInitiatorToResponder)
        XCTAssertEqual(responderToInitiator.withUnsafeBytes { Data($0) }, expectedResponderToInitiator)
    }

    private func parseHex(hex: String) throws -> Data {
        guard hex.count.isMultiple(of: 2) else { throw HandshakeError.invalidOffer }
        var result = Data()
        var index = hex.startIndex
        while index < hex.endIndex {
            let end = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<end], radix: 16) else { throw HandshakeError.invalidOffer }
            result.append(byte)
            index = end
        }
        return result
    }

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
