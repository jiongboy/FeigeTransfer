import Foundation

/// The user's decision after being shown a peer's identity and SAS. Returned by the
/// confirmation callback that drives the UI; the service never fabricates this value.
public struct UserConfirmation: Sendable {
    public let peerFingerprint: String
    public let enteredSAS: String

    public init(peerFingerprint: String, enteredSAS: String) {
        self.peerFingerprint = peerFingerprint
        self.enteredSAS = enteredSAS
    }
}

/// Drives one connection from an already-open byte transport to an authenticated session:
/// signed handshake → local user confirmation of the peer fingerprint/SAS → session key
/// derivation. It opens no socket itself; the caller supplies a `ByteTransport` (the real
/// `NetworkByteStream` in production, an in-memory duplex pipe in tests). The confirmation
/// callback is the bridge to the UI: it is given the verified peer identity and SAS, and
/// must suspend until the local user confirms, throwing if they reject or it times out.
public enum TransferService {
    /// Establish an authenticated session over `transport`. `requestConfirmation` is awaited
    /// after signature verification succeeds and before any session key is derived, so the
    /// user always sees a cryptographically-verified peer before approving.
    public static func establishSession(
        transport: ByteTransport,
        identity: DeviceIdentity,
        role: HandshakeRole,
        deviceName: String,
        sessionCode: String,
        handshakeTimeout: TimeInterval = 15,
        confirmationTimeout: TimeInterval = 5 * 60,
        requestConfirmation: @escaping @Sendable (PendingPeerApproval) async throws -> UserConfirmation
    ) async throws -> HandshakeSession {
        do {
            guard handshakeTimeout.isFinite, handshakeTimeout > 0,
                  confirmationTimeout.isFinite, confirmationTimeout > 0 else { throw AuthenticatedStreamError.timeout }
            let handshake = try FeigeHandshake(identity: identity, role: role,
                                               deviceName: deviceName, sessionCode: sessionCode)
            let pending = try await withThrowingTaskGroup(of: PendingPeerApproval.self) { group in
                group.addTask {
                    try await AuthenticatedHandshakeExchange.perform(local: handshake, transport: transport, timeout: handshakeTimeout)
                }
                group.addTask {
                    try await Task.sleep(for: .seconds(handshakeTimeout))
                    transport.close()
                    throw AuthenticatedStreamError.timeout
                }
                defer { group.cancelAll() }
                guard let pending = try await group.next() else { throw NetworkTransportError.closed }
                return pending
            }
            let approval = PairingApprovalCoordinator(transcript: pending.transcript,
                                                      localRole: role,
                                                      expiresAt: Date().addingTimeInterval(confirmationTimeout))
            let confirmation = try await withThrowingTaskGroup(of: UserConfirmation.self) { group in
                group.addTask { try await requestConfirmation(pending) }
                group.addTask {
                    try await Task.sleep(for: .seconds(confirmationTimeout))
                    transport.close()
                    throw PairingFlowError.pairingCodeExpired
                }
                defer { group.cancelAll() }
                guard let confirmation = try await group.next() else { throw PairingFlowError.userRejected }
                return confirmation
            }
            let token = try approval.confirm(userEnteredSAS: confirmation.enteredSAS,
                                             expectedPeerFingerprint: confirmation.peerFingerprint)
            try approval.consume(token, for: pending.transcript, peer: pending.peer)
            return try handshake.deriveSession(peer: pending.peer, transcript: pending.transcript,
                                               localSignature: pending.localSignature,
                                               peerSignature: pending.peerSignature,
                                               approval: token)
        } catch {
            transport.close()
            throw error
        }
    }
}
