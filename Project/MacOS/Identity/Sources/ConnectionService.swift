import Foundation

/// v3: the initiator requests a connection; only the recipient is asked to approve.
/// Files cannot be sent/received until the encrypted connection decision is accepted.
public enum ConnectionService {
    public static func establish(transport: ByteTransport, identity: DeviceIdentity,
                                 role: HandshakeRole, deviceName: String,
                                 approvalTimeout: TimeInterval = 5 * 60,
                                 handshakeTimeout: TimeInterval = 15,
                                 identityAuthorizationTimeout: TimeInterval = 5 * 60,
                                 requestApproval: @escaping @Sendable (PendingPeerApproval) async throws -> Bool = { _ in false }) async throws -> FileConnection {
        try await establish(transport: transport, identity: identity, role: role, deviceName: deviceName,
                            approvalTimeout: approvalTimeout, handshakeTimeout: handshakeTimeout,
                            identityAuthorizationTimeout: identityAuthorizationTimeout,
                            onStage: { _ in }, requestApproval: requestApproval)
    }

    public static func establish(transport: ByteTransport, identity: DeviceIdentity,
                                 role: HandshakeRole, deviceName: String,
                                 approvalTimeout: TimeInterval = 5 * 60,
                                 handshakeTimeout: TimeInterval = 15,
                                 identityAuthorizationTimeout: TimeInterval = 5 * 60,
                                 onStage: @escaping @Sendable (HandshakeStage) async -> Void,
                                 requestApproval: @escaping @Sendable (PendingPeerApproval) async throws -> Bool = { _ in false }) async throws -> FileConnection {
        do {
            guard handshakeTimeout.isFinite, handshakeTimeout > 0,
                  identityAuthorizationTimeout.isFinite, identityAuthorizationTimeout > 0 else {
                throw AuthenticatedStreamError.timeout
            }
            let handshake = try FeigeHandshake(identity: identity, role: role, deviceName: deviceName, sessionCode: "", version: 3)
            // The initial network exchange still has a short timeout. Proofs may
            // require a password on either Mac, independently of connection consent.
            let pending = try await deadline(identityAuthorizationTimeout + 2 * handshakeTimeout, transport: transport) {
                try await AuthenticatedHandshakeExchange.perform(local: handshake, transport: transport,
                                                                 timeout: handshakeTimeout,
                                                                 proofTimeout: identityAuthorizationTimeout,
                                                                 onStage: onStage)
            }
            let session = try handshake.deriveConnectionKeys(pending)
            let channel = FileConnection(session: session, transport: transport,
                                         peerName: pending.peer.deviceName, fingerprint: pending.peerFingerprint)
            if role == .responder {
                let accepted = try await deadline(approvalTimeout, transport: transport) { try await requestApproval(pending) }
                try await channel.sendConsent(accepted)
                guard accepted else { throw ConnectionError.rejected }
            } else {
                try await deadline(approvalTimeout, transport: transport) { try await channel.receiveConsent() }
            }
            return channel
        } catch { transport.close(); throw error }
    }

    private static func deadline<T: Sendable>(_ seconds: TimeInterval, transport: ByteTransport,
                                              action: @escaping @Sendable () async throws -> T) async throws -> T {
        guard seconds.isFinite, seconds > 0 else { throw AuthenticatedStreamError.timeout }
        return try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await action() }
            group.addTask {
                try await Task.sleep(for: .seconds(seconds))
                transport.close()
                throw AuthenticatedStreamError.timeout
            }
            defer { group.cancelAll() }
            guard let result = try await group.next() else { throw ConnectionError.closed }
            return result
        }
    }
}
