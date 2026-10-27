import Foundation

public enum PairingFlowError: LocalizedError {
    case pairingCodeExpired
    case transcriptMismatch
    case peerMismatch
    case approvalAlreadyConsumed
    case userRejected

    public var errorDescription: String? {
        switch self {
        case .pairingCodeExpired: "配对码已失效，请重新发起配对。"
        case .transcriptMismatch: "确认内容与当前握手记录不一致。"
        case .peerMismatch: "已确认的设备身份与当前对端不一致。"
        case .approvalAlreadyConsumed: "此配对确认已使用，请重新确认。"
        case .userRejected: "本机用户拒绝了本次连接。"
        }
    }
}

/// One-shot local approval coordinator for one immutable transcript.
/// UI calls confirm only after displaying the peer identity and SAS and the user enters
/// the same SAS; the resulting token cannot be reused or applied to another transcript.
public final class PairingApprovalCoordinator {
    public enum State: Equatable { case awaitingUser, confirmed, consumed, expired }
    public private(set) var state: State = .awaitingUser
    public let transcriptDigest: Data
    public let peerFingerprint: String
    public let sas: String
    public let localRole: HandshakeRole
    private let transcriptPeerFingerprint: String
    private let expiresAt: Date
    private let lock = NSLock()

    public init(transcript: HandshakeTranscript, localRole: HandshakeRole, expiresAt: Date) {
        transcriptDigest = transcript.digest
        peerFingerprint = transcript.peerFingerprint(for: localRole)
        transcriptPeerFingerprint = transcript.peerFingerprint(for: localRole)
        sas = transcript.sas
        self.localRole = localRole
        self.expiresAt = expiresAt
    }

    public func confirm(userEnteredSAS: String, expectedPeerFingerprint: String,
                        at now: Date = Date()) throws -> SASApproval {
        lock.lock()
        defer { lock.unlock() }
        guard now < expiresAt else { state = .expired; throw PairingFlowError.pairingCodeExpired }
        guard state == .awaitingUser else { throw PairingFlowError.approvalAlreadyConsumed }
        let actualPeer = normalizeFingerprint(transcriptPeerFingerprint)
        guard actualPeer == normalizeFingerprint(expectedPeerFingerprint),
              constantTimeEqual(Data(userEnteredSAS.utf8), Data(sas.utf8)) else {
            throw HandshakeError.userApprovalRequired
        }
        state = .confirmed
        return SASApproval(transcriptHash: transcriptDigest, peerFingerprint: actualPeer)
    }

    public func consume(_ approval: SASApproval, for transcript: HandshakeTranscript, peer: HandshakeOffer) throws {
        lock.lock()
        defer { lock.unlock() }
        guard Date() < expiresAt else { state = .expired; throw PairingFlowError.pairingCodeExpired }
        guard state == .confirmed else { throw PairingFlowError.approvalAlreadyConsumed }
        guard transcript.digest == transcriptDigest,
              transcript.peerFingerprint(for: localRole) == peerFingerprint else {
            throw PairingFlowError.transcriptMismatch
        }
        let expectedPeer = localRole == .initiator ? transcript.responder : transcript.initiator
        guard peer == expectedPeer else { throw PairingFlowError.peerMismatch }
        guard approval.isBound(to: transcriptDigest, peerFingerprint: peerFingerprint) else {
            throw PairingFlowError.transcriptMismatch
        }
        state = .consumed
    }

    private func normalizeFingerprint(_ value: String) -> String {
        let scalars = value.unicodeScalars
        guard scalars.allSatisfy({ CharacterSet(charactersIn: "0123456789abcdefABCDEF ").contains($0) }) else { return "" }
        return value.filter(\.isHexDigit).map { String($0).uppercased() }.joined()
    }

    private func constantTimeEqual(_ left: Data, _ right: Data) -> Bool {
        guard left.count == right.count else { return false }
        var difference: UInt8 = 0
        for (a, b) in zip(left, right) { difference |= a ^ b }
        return difference == 0
    }
}
