import Foundation
import CryptoKit

public enum HandshakeExchangeError: LocalizedError {
    case malformedMessage, peerSignatureMismatch
    public var errorDescription: String? {
        switch self {
        case .malformedMessage: "握手网络消息格式无效。"
        case .peerSignatureMismatch: "对端握手签名无效。"
        }
    }
}

private struct OfferEnvelope: Codable { let offer: HandshakeOffer }
private struct ProofEnvelope: Codable { let offer: HandshakeOffer; let signature: Data }

public enum HandshakeStage: Sendable {
    case exchangingOffers, authorizingIdentity, waitingForPeer, verified
}

public struct PendingPeerApproval: Sendable {
    public let peer: HandshakeOffer
    public let peerFingerprint: String
    public let transcript: HandshakeTranscript
    public let localSignature: Data
    public let peerSignature: Data
    public let sas: String
}

public struct ResponderHandshakeResult: Sendable {
    public let handshake: FeigeHandshake
    public let approval: PendingPeerApproval
}

/// No session keys or application records are enabled here; callers must display peer
/// fingerprint/SAS and get local user approval before calling deriveSession.
public enum AuthenticatedHandshakeExchange {
    public static let maximumMessageLength = 16 * 1024

    public static func perform(local: FeigeHandshake, transport: ByteTransport,
                               timeout: TimeInterval = 15,
                               proofTimeout: TimeInterval? = nil,
                               onStage: @escaping @Sendable (HandshakeStage) async -> Void = { _ in }) async throws -> PendingPeerApproval {
        await onStage(.exchangingOffers)
        try await writeFrame(OfferEnvelope(offer: local.offer), transport: transport, timeout: timeout)
        let peerEnvelope: OfferEnvelope = try await readFrame(transport: transport, as: OfferEnvelope.self, timeout: timeout)
        let peer = peerEnvelope.offer
        let transcript = try HandshakeTranscript.validated(local: local.offer, peer: peer)
        await onStage(.authorizingIdentity)
        // Signing may show a system Keychain dialog. Keep that synchronous system
        // operation off the UI executor and allow the peer time to authorize it.
        let localSignature = try await Task.detached(priority: .userInitiated) {
            try local.sign(transcript)
        }.value
        try Task.checkCancellation()
        try await writeFrame(ProofEnvelope(offer: local.offer, signature: localSignature), transport: transport, timeout: timeout)
        await onStage(.waitingForPeer)
        let peerProof: ProofEnvelope = try await readFrame(transport: transport, as: ProofEnvelope.self, timeout: proofTimeout ?? timeout)
        guard peerProof.offer == peer else { throw HandshakeExchangeError.malformedMessage }
        try FeigeHandshake.verify(peerProof.signature, transcript: transcript, signer: peer,
                                  expectedFingerprint: HandshakeTranscript.fingerprint(peer.identityPublicKeyDER))
        await onStage(.verified)
        return PendingPeerApproval(peer: peer,
                                   peerFingerprint: HandshakeTranscript.fingerprint(peer.identityPublicKeyDER),
                                   transcript: transcript, localSignature: localSignature,
                                   peerSignature: peerProof.signature, sas: transcript.sas)
    }

    /// Responder-side exchange that accepts only explicitly supported initiator versions.
    /// The initiator's offer chooses the exact version; there is no retry or downgrade path.
    public static func performResponder(identity: DeviceIdentity, deviceName: String, transport: ByteTransport,
                                        timeout: TimeInterval = 15, proofTimeout: TimeInterval? = nil,
                                        onStage: @escaping @Sendable (HandshakeStage) async -> Void = { _ in }) async throws -> ResponderHandshakeResult {
        await onStage(.exchangingOffers)
        let peerEnvelope: OfferEnvelope = try await readFrame(transport: transport, as: OfferEnvelope.self, timeout: timeout)
        let peer = peerEnvelope.offer
        guard peer.role == .initiator, (3...6).contains(peer.version) else {
            throw HandshakeError.invalidOffer
        }
        let local = try FeigeHandshake(identity: identity, role: .responder, deviceName: deviceName,
                                       sessionCode: "", version: peer.version)
        try await writeFrame(OfferEnvelope(offer: local.offer), transport: transport, timeout: timeout)
        let transcript = try HandshakeTranscript.validated(local: local.offer, peer: peer)
        await onStage(.authorizingIdentity)
        let localSignature = try await Task.detached(priority: .userInitiated) {
            try local.sign(transcript)
        }.value
        try Task.checkCancellation()
        try await writeFrame(ProofEnvelope(offer: local.offer, signature: localSignature), transport: transport, timeout: timeout)
        await onStage(.waitingForPeer)
        let peerProof: ProofEnvelope = try await readFrame(transport: transport, as: ProofEnvelope.self,
                                                            timeout: proofTimeout ?? timeout)
        guard peerProof.offer == peer else { throw HandshakeExchangeError.malformedMessage }
        let fingerprint = HandshakeTranscript.fingerprint(peer.identityPublicKeyDER)
        try FeigeHandshake.verify(peerProof.signature, transcript: transcript, signer: peer,
                                  expectedFingerprint: fingerprint)
        await onStage(.verified)
        let approval = PendingPeerApproval(peer: peer, peerFingerprint: fingerprint,
                                           transcript: transcript, localSignature: localSignature,
                                           peerSignature: peerProof.signature, sas: transcript.sas)
        return ResponderHandshakeResult(handshake: local, approval: approval)
    }

    private static func writeFrame<T: Encodable>(_ message: T, transport: ByteTransport,
                                                   timeout: TimeInterval) async throws {
        let payload = try JSONEncoder().encode(message)
        guard !payload.isEmpty, payload.count <= maximumMessageLength else { throw HandshakeExchangeError.malformedMessage }
        var length = UInt32(payload.count).bigEndian
        var frame = Data()
        withUnsafeBytes(of: &length) { frame.append(contentsOf: $0) }
        frame.append(payload)
        try await transport.send(frame, timeout: timeout)
    }

    private static func readFrame<T: Decodable>(transport: ByteTransport, as type: T.Type,
                                                 timeout: TimeInterval) async throws -> T {
        let prefix = try await readExactly(4, transport: transport, timeout: timeout)
        let length = prefix.reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        guard length > 0, length <= maximumMessageLength else { throw HandshakeExchangeError.malformedMessage }
        let body = try await readExactly(Int(length), transport: transport, timeout: timeout)
        do { return try JSONDecoder().decode(T.self, from: body) }
        catch { throw HandshakeExchangeError.malformedMessage }
    }

    private static func readExactly(_ count: Int, transport: ByteTransport, timeout: TimeInterval) async throws -> Data {
        guard count > 0, count <= maximumMessageLength else { throw HandshakeExchangeError.malformedMessage }
        var bytes = Data()
        while bytes.count < count {
            let next = try await transport.receive(maximumLength: count - bytes.count, timeout: timeout)
            guard !next.isEmpty, bytes.count + next.count <= count else { throw HandshakeExchangeError.malformedMessage }
            bytes.append(next)
        }
        return bytes
    }
}
