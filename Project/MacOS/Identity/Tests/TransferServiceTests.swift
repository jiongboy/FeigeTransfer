import XCTest
import FeigeIdentity

/// Verifies the application-layer orchestration: `TransferService.establishSession` runs the
/// signed handshake over a transport, hands the verified peer identity + SAS to a confirmation
/// callback (the UI bridge), and derives a session only after that callback approves. Both the
/// happy path and the reject path are covered over the in-memory duplex transport.
final class TransferServiceTests: XCTestCase {
    private struct UserRejected: Error {}

    func testHandshakeDeadlineTerminatesSilentPeer() async throws {
        let store = DeviceIdentityStore(serviceIdentifier: "com.feige.deadline.\(UUID())")
        defer { store.delete() }
        let identity = try store.loadOrCreate()
        let (transport, silentPeer) = InMemoryDuplexTransport.pair()
        defer { transport.close(); silentPeer.close() }
        let start = Date()
        do {
            _ = try await TransferService.establishSession(transport: transport, identity: identity,
                                                           role: .initiator, deviceName: "A", sessionCode: "123456",
                                                           handshakeTimeout: 0.05) { _ in
                XCTFail("silent peer must not reach confirmation")
                throw UserRejected()
            }
            XCTFail("expected timeout")
        } catch { XCTAssertLessThan(Date().timeIntervalSince(start), 2) }
    }

    // Identities must stay alive (and their Keychain items undeleted) for the whole test,
    // because the handshake signs with the private key. Stores are created per-test and
    // cleaned up with defer at the end of each test body.
    func testEstablishSessionDerivesMatchingKeysAfterUserConfirmation() async throws {
        let storeA = DeviceIdentityStore(serviceIdentifier: "com.feige.service.a.\(UUID())")
        let storeB = DeviceIdentityStore(serviceIdentifier: "com.feige.service.b.\(UUID())")
        defer { _ = storeA.delete(); _ = storeB.delete() }
        let identityA = try storeA.loadOrCreate()
        let identityB = try storeB.loadOrCreate()
        let fingerprintA = identityA.fingerprint
        let fingerprintB = identityB.fingerprint
        let (transportA, transportB) = InMemoryDuplexTransport.pair()

        // B confirms using the fingerprint it expects for A, and the SAS it displays.
        async let sessionB = TransferService.establishSession(
            transport: transportB, identity: identityB, role: .responder, deviceName: "B", sessionCode: "123456"
        ) { pending in
            // The UI would show pending.peerFingerprint + pending.sas and wait for the user.
            // Here we auto-confirm with the fingerprint we expect for the peer (A).
            UserConfirmation(peerFingerprint: fingerprintA, enteredSAS: pending.sas)
        }
        let sessionA = try await TransferService.establishSession(
            transport: transportA, identity: identityA, role: .initiator, deviceName: "A", sessionCode: "123456"
        ) { pending in
            UserConfirmation(peerFingerprint: fingerprintB, enteredSAS: pending.sas)
        }
        let confirmedB = try await sessionB

        XCTAssertEqual(sessionA.sendKey, confirmedB.receiveKey)
        XCTAssertEqual(sessionA.receiveKey, confirmedB.sendKey)
        XCTAssertEqual(sessionA.sas, confirmedB.sas)
        transportA.close()
        transportB.close()
    }

    func testEstablishSessionRejectsWhenUserDeclines() async throws {
        let storeA = DeviceIdentityStore(serviceIdentifier: "com.feige.service.ra.\(UUID())")
        let storeB = DeviceIdentityStore(serviceIdentifier: "com.feige.service.rb.\(UUID())")
        defer { _ = storeA.delete(); _ = storeB.delete() }
        let identityA = try storeA.loadOrCreate()
        let identityB = try storeB.loadOrCreate()
        let fingerprintB = identityB.fingerprint
        let (transportA, transportB) = InMemoryDuplexTransport.pair()

        // Both peers must run the handshake concurrently; B then rejects at the
        // confirmation stage, so no session is derived on B's side.
        async let sessionA = TransferService.establishSession(
            transport: transportA, identity: identityA, role: .initiator, deviceName: "A", sessionCode: "654321"
        ) { pending in
            UserConfirmation(peerFingerprint: fingerprintB, enteredSAS: pending.sas)
        }
        do {
            _ = try await TransferService.establishSession(
                transport: transportB, identity: identityB, role: .responder, deviceName: "B", sessionCode: "654321"
            ) { _ in
                throw UserRejected()
            }
            XCTFail("expected rejection to propagate")
        } catch is UserRejected {
            // expected
        }
        _ = try? await sessionA
        transportA.close()
        transportB.close()
    }

    func testWrongSASIsRejectedBeforeSessionDerivation() async throws {
        let storeA = DeviceIdentityStore(serviceIdentifier: "com.feige.service.wa.\(UUID())")
        let storeB = DeviceIdentityStore(serviceIdentifier: "com.feige.service.wb.\(UUID())")
        defer { _ = storeA.delete(); _ = storeB.delete() }
        let identityA = try storeA.loadOrCreate()
        let identityB = try storeB.loadOrCreate()
        let fingerprintA = identityA.fingerprint
        let fingerprintB = identityB.fingerprint
        let (transportA, transportB) = InMemoryDuplexTransport.pair()

        // A attempts with an incorrect SAS; the service must reject and never derive a session.
        async let sessionB = TransferService.establishSession(
            transport: transportB, identity: identityB, role: .responder, deviceName: "B", sessionCode: "111111"
        ) { pending in
            UserConfirmation(peerFingerprint: fingerprintA, enteredSAS: pending.sas)
        }
        do {
            _ = try await TransferService.establishSession(
                transport: transportA, identity: identityA, role: .initiator, deviceName: "A", sessionCode: "111111"
            ) { _ in
                UserConfirmation(peerFingerprint: fingerprintB, enteredSAS: "000000")
            }
            XCTFail("expected wrong SAS to be rejected")
        } catch {
            // expected: userApprovalRequired
        }
        _ = try? await sessionB
        transportA.close()
        transportB.close()
    }
}
