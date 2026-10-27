import Foundation
import CryptoKit
import Security

public struct SASApproval {
    fileprivate let transcriptHash: Data
    fileprivate let peerFingerprint: String
    init(transcriptHash: Data, peerFingerprint: String) {
        self.transcriptHash = transcriptHash
        self.peerFingerprint = peerFingerprint
    }

    func isBound(to digest: Data, peerFingerprint: String) -> Bool {
        transcriptHash == digest && self.peerFingerprint == SASApproval.normalizeFingerprint(peerFingerprint)
    }

    fileprivate static func normalizeFingerprint(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.unicodeScalars.allSatisfy({ CharacterSet(charactersIn: "0123456789abcdefABCDEF ").contains($0) }) else { return "" }
        return trimmed.filter(\.isHexDigit).map { String($0).uppercased() }.joined()
    }

    private static func constantTimeEqual(_ lhs: Data, _ rhs: Data) -> Bool {
        guard lhs.count == rhs.count else { return false }
        var diff: UInt8 = 0
        for (a, b) in zip(lhs, rhs) { diff |= a ^ b }
        return diff == 0
    }
}

public struct HandshakeSession: Sendable {
    public let sendKey: SymmetricKey
    public let receiveKey: SymmetricKey
    public let transcriptHash: Data
    public let sas: String
    let sendNoncePrefix: Data
    let receiveNoncePrefix: Data

    init(sendKey: SymmetricKey, receiveKey: SymmetricKey, transcriptHash: Data, sas: String,
         sendNoncePrefix: Data, receiveNoncePrefix: Data) {
        self.sendKey = sendKey
        self.receiveKey = receiveKey
        self.transcriptHash = transcriptHash
        self.sas = sas
        self.sendNoncePrefix = sendNoncePrefix
        self.receiveNoncePrefix = receiveNoncePrefix
    }
}

public enum HandshakeError: LocalizedError {
    case invalidOffer, invalidTranscript, signatureFailure, fingerprintMismatch, keyAgreementFailure, randomSourceFailure, sessionAlreadyDerived, pairingCodeMismatch, userApprovalRequired
    public var errorDescription: String? {
        switch self {
        case .invalidOffer: "握手消息格式无效。"
        case .invalidTranscript: "握手 transcript 无效。"
        case .signatureFailure: "设备身份签名校验失败。"
        case .fingerprintMismatch: "对端身份指纹与预期不一致。"
        case .keyAgreementFailure: "无法建立临时加密会话。"
        case .randomSourceFailure: "系统安全随机数生成器不可用。"
        case .sessionAlreadyDerived: "此握手实例已派生过会话；必须重新握手。"
        case .pairingCodeMismatch: "双方输入的配对码不一致。"
        case .userApprovalRequired: "请核对对端指纹和双方相同的 SAS 配对码后再确认。"
        }
    }
}

/// Signed key agreement primitives. v1 uses SAS approval; v2 negotiates keys and
/// then requires an explicit receiver connection decision before application traffic.
public final class FeigeHandshake: @unchecked Sendable {
    public let offer: HandshakeOffer
    private let identityPrivateKey: SecKey
    private let ephemeralPrivateKey: Curve25519.KeyAgreement.PrivateKey
    private let derivationLock = NSLock()
    private var didDeriveSession = false

    public init(identity: DeviceIdentity, role: HandshakeRole, deviceName: String, sessionCode: String, version: UInt16 = 1) throws {
        let ephemeralBytes = try Self.randomBytes(count: 32)
        let ephemeral: Curve25519.KeyAgreement.PrivateKey
        do { ephemeral = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: ephemeralBytes) }
        catch { throw HandshakeError.keyAgreementFailure }
        offer = try HandshakeOffer(version: version, role: role, deviceName: deviceName,
                                   identityPublicKeyDER: identity.publicKeyDER,
                                   ephemeralPublicKey: ephemeral.publicKey.rawRepresentation,
                                   nonce: try Self.randomBytes(count: 32), sessionCode: sessionCode)
        identityPrivateKey = identity.privateKey
        ephemeralPrivateKey = ephemeral
    }

    public func sign(_ transcript: HandshakeTranscript) throws -> Data {
        let algorithm = SecKeyAlgorithm.rsaSignatureMessagePSSSHA256
        guard SecKeyIsAlgorithmSupported(identityPrivateKey, .sign, algorithm) else { throw HandshakeError.signatureFailure }
        var error: Unmanaged<CFError>?
        guard let signature = SecKeyCreateSignature(identityPrivateKey, algorithm, transcript.encoded() as CFData, &error) as Data? else {
            if let error { _ = error.takeRetainedValue() }
            throw HandshakeError.signatureFailure
        }
        return signature
    }

    public static func verify(_ signature: Data, transcript: HandshakeTranscript,
                              signer: HandshakeOffer, expectedFingerprint: String) throws {
        guard signer == transcript.initiator || signer == transcript.responder else { throw HandshakeError.invalidTranscript }
        let fingerprint = HandshakeTranscript.fingerprint(signer.identityPublicKeyDER)
        guard normalizeFingerprint(fingerprint) == normalizeFingerprint(expectedFingerprint) else {
            throw HandshakeError.fingerprintMismatch
        }
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeyClass as String: kSecAttrKeyClassPublic,
            kSecAttrKeySizeInBits as String: 3072
        ]
        var createError: Unmanaged<CFError>?
        guard let publicKey = SecKeyCreateWithData(signer.identityPublicKeyDER as CFData, attributes as CFDictionary, &createError),
              SecKeyIsAlgorithmSupported(publicKey, .verify, .rsaSignatureMessagePSSSHA256) else {
            if let createError { _ = createError.takeRetainedValue() }
            throw HandshakeError.signatureFailure
        }
        var verifyError: Unmanaged<CFError>?
        guard SecKeyVerifySignature(publicKey, .rsaSignatureMessagePSSSHA256,
                                    transcript.encoded() as CFData, signature as CFData, &verifyError) else {
            if let verifyError { _ = verifyError.takeRetainedValue() }
            throw HandshakeError.signatureFailure
        }
    }

    public func deriveSession(peer: HandshakeOffer, transcript: HandshakeTranscript,
                              localSignature: Data, peerSignature: Data,
                              approval: SASApproval) throws -> HandshakeSession {
        guard approval.transcriptHash == transcript.digest,
              approval.peerFingerprint == SASApproval.normalizeFingerprint(transcript.peerFingerprint(for: offer.role)) else {
            throw HandshakeError.userApprovalRequired
        }
        return try deriveVerifiedSession(peer: peer, transcript: transcript, localSignature: localSignature,
                                         peerSignature: peerSignature, expectedFingerprint: approval.peerFingerprint)
    }

    private func deriveVerifiedSession(peer: HandshakeOffer, transcript: HandshakeTranscript,
                                       localSignature: Data, peerSignature: Data,
                                       expectedFingerprint: String) throws -> HandshakeSession {
        derivationLock.lock()
        defer { derivationLock.unlock() }
        guard !didDeriveSession else { throw HandshakeError.sessionAlreadyDerived }
        didDeriveSession = true
        let transcriptLocal = offer.role == .initiator ? transcript.initiator : transcript.responder
        let transcriptPeer = offer.role == .initiator ? transcript.responder : transcript.initiator
        guard transcriptLocal == offer, transcriptPeer == peer else { throw HandshakeError.invalidTranscript }
        try Self.verify(peerSignature, transcript: transcript, signer: peer,
                        expectedFingerprint: expectedFingerprint)
        try Self.verify(localSignature, transcript: transcript, signer: offer,
                        expectedFingerprint: HandshakeTranscript.fingerprint(offer.identityPublicKeyDER))
        let peerEphemeral: Curve25519.KeyAgreement.PublicKey
        do { peerEphemeral = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: peer.ephemeralPublicKey) }
        catch { throw HandshakeError.keyAgreementFailure }
        let sharedSecret: SharedSecret
        do { sharedSecret = try ephemeralPrivateKey.sharedSecretFromKeyAgreement(with: peerEphemeral) }
        catch { throw HandshakeError.keyAgreementFailure }
        let transcriptHash = transcript.digest
        let initiatorKey = sharedSecret.hkdfDerivedSymmetricKey(using: SHA256.self, salt: transcriptHash,
            sharedInfo: Data("feige-v\(offer.version) initiator-to-responder".utf8), outputByteCount: 32)
        let responderKey = sharedSecret.hkdfDerivedSymmetricKey(using: SHA256.self, salt: transcriptHash,
            sharedInfo: Data("feige-v\(offer.version) responder-to-initiator".utf8), outputByteCount: 32)
        let initiatorPrefix = Data(transcriptHash.prefix(4)) + (offer.version == 1 ? Data([0x49, 0x4E, 0x49, 0x54]) : Data())
        let responderPrefix = Data(transcriptHash.prefix(4)) + (offer.version == 1 ? Data([0x52, 0x45, 0x53, 0x50]) : Data())
        let isInitiator = offer.role == .initiator
        return HandshakeSession(sendKey: isInitiator ? initiatorKey : responderKey,
                                receiveKey: isInitiator ? responderKey : initiatorKey,
                                transcriptHash: transcriptHash, sas: transcript.sas,
                                sendNoncePrefix: isInitiator ? initiatorPrefix : responderPrefix,
                                receiveNoncePrefix: isInitiator ? responderPrefix : initiatorPrefix)
    }

    /// v2 negotiates encryption before consent. Only ConnectionService may expose
    /// the resulting channel, and it does so after the receiver's explicit decision.
    func deriveConnectionKeys(_ pending: PendingPeerApproval) throws -> HandshakeSession {
        guard offer.version == 2 || offer.version == 3 else { throw HandshakeError.invalidOffer }
        return try deriveVerifiedSession(peer: pending.peer, transcript: pending.transcript,
                                         localSignature: pending.localSignature, peerSignature: pending.peerSignature,
                                         expectedFingerprint: pending.peerFingerprint)
    }

    private static func normalizeFingerprint(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.unicodeScalars.allSatisfy({ CharacterSet(charactersIn: "0123456789abcdefABCDEF ").contains($0) }) else { return "" }
        return trimmed.filter(\.isHexDigit).map { $0.uppercased() }.joined()
    }

    private static func randomBytes(count: Int) throws -> Data {
        guard count > 0, count <= 1024 else { throw HandshakeError.invalidOffer }
        var bytes = [UInt8](repeating: 0, count: count)
        let status = SecRandomCopyBytes(kSecRandomDefault, count, &bytes)
        guard status == errSecSuccess else { throw HandshakeError.randomSourceFailure }
        return Data(bytes)
    }
}

private extension Data {
    mutating func appendUInt16(_ value: UInt16) {
        var bigEndian = value.bigEndian
        Swift.withUnsafeBytes(of: &bigEndian) { append(contentsOf: $0) }
    }

    mutating func appendLengthPrefixed(_ value: Data) {
        var length = UInt32(value.count).bigEndian
        Swift.withUnsafeBytes(of: &length) { append(contentsOf: $0) }
        append(value)
    }

    mutating func append(_ offer: HandshakeOffer) {
        appendUInt16(offer.version)
        append(offer.role.rawValue)
        appendLengthPrefixed(Data(offer.deviceName.utf8))
        appendLengthPrefixed(offer.identityPublicKeyDER)
        appendLengthPrefixed(offer.ephemeralPublicKey)
        appendLengthPrefixed(offer.nonce)
        appendLengthPrefixed(Data(offer.sessionCode.utf8))
    }
}
