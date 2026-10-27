import Foundation
import CryptoKit

public enum HandshakeRole: UInt8, Codable, Equatable, Sendable {
    case initiator = 1
    case responder = 2
}

public struct HandshakeOffer: Codable, Equatable, Sendable {
    public let version: UInt16
    public let role: HandshakeRole
    public let deviceName: String
    public let identityPublicKeyDER: Data
    public let ephemeralPublicKey: Data
    public let nonce: Data
    public let sessionCode: String

    private enum CodingKeys: String, CodingKey { case version, role, deviceName, identityPublicKeyDER, ephemeralPublicKey, nonce, sessionCode }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(version: values.decode(UInt16.self, forKey: .version),
                      role: values.decode(HandshakeRole.self, forKey: .role),
                      deviceName: values.decode(String.self, forKey: .deviceName),
                      identityPublicKeyDER: values.decode(Data.self, forKey: .identityPublicKeyDER),
                      ephemeralPublicKey: values.decode(Data.self, forKey: .ephemeralPublicKey),
                      nonce: values.decode(Data.self, forKey: .nonce),
                      sessionCode: values.decode(String.self, forKey: .sessionCode))
    }

    public func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(version, forKey: .version)
        try values.encode(role, forKey: .role)
        try values.encode(deviceName, forKey: .deviceName)
        try values.encode(identityPublicKeyDER, forKey: .identityPublicKeyDER)
        try values.encode(ephemeralPublicKey, forKey: .ephemeralPublicKey)
        try values.encode(nonce, forKey: .nonce)
        try values.encode(sessionCode, forKey: .sessionCode)
    }

    public init(version: UInt16 = 1, role: HandshakeRole, deviceName: String,
                identityPublicKeyDER: Data, ephemeralPublicKey: Data, nonce: Data, sessionCode: String) throws {
        guard (1...3).contains(version), !deviceName.isEmpty, deviceName.utf8.count <= 128,
              !identityPublicKeyDER.isEmpty, identityPublicKeyDER.count <= 1024, ephemeralPublicKey.count == 32,
              nonce.count == 32,
              version >= 2 ? sessionCode.isEmpty : Self.isValidCode(sessionCode) else { throw HandshakeError.invalidOffer }
        self.version = version
        self.role = role
        self.deviceName = deviceName
        self.identityPublicKeyDER = identityPublicKeyDER
        self.ephemeralPublicKey = ephemeralPublicKey
        self.nonce = nonce
        self.sessionCode = sessionCode
    }

    private static func isValidCode(_ code: String) -> Bool {
        code.utf8.count == 6 && code.utf8.allSatisfy { $0 >= 48 && $0 <= 57 }
    }
}

/// Immutable once created by `validated`; all stored offers are value types, so it is safe
/// to send across concurrency domains (e.g. into the SwiftUI confirmation bridge).
public final class HandshakeTranscript: @unchecked Sendable {
    public let initiator: HandshakeOffer
    public let responder: HandshakeOffer

    private init(initiator: HandshakeOffer, responder: HandshakeOffer) {
        self.initiator = initiator
        self.responder = responder
    }

    public static func validated(local: HandshakeOffer, peer: HandshakeOffer) throws -> HandshakeTranscript {
        guard local.version == peer.version, local.role != peer.role,
              local.role == .initiator ? peer.role == .responder : peer.role == .initiator,
              local.identityPublicKeyDER != peer.identityPublicKeyDER else { throw HandshakeError.invalidTranscript }
        guard local.sessionCode == peer.sessionCode else { throw HandshakeError.pairingCodeMismatch }
        return HandshakeTranscript(initiator: local.role == .initiator ? local : peer,
                                   responder: local.role == .responder ? local : peer)
    }

    public func encoded() -> Data {
        var data = Data("FEIGE-HANDSHAKE-V\(initiator.version)\0".utf8)
        data.append(initiator)
        data.append(responder)
        return data
    }

    public var digest: Data { Data(SHA256.hash(data: encoded())) }

    public var sas: String {
        let value = digest.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) } % 1_000_000
        return String(format: "%06u", value)
    }

    public func peerFingerprint(for localRole: HandshakeRole) -> String {
        Self.fingerprint(localRole == .initiator ? responder.identityPublicKeyDER : initiator.identityPublicKeyDER)
    }

    public static func fingerprint(_ spkiDER: Data) -> String {
        let hex = SHA256.hash(data: spkiDER).map { String(format: "%02X", $0) }.joined()
        return stride(from: 0, to: hex.count, by: 4).map { offset in
            let start = hex.index(hex.startIndex, offsetBy: offset)
            let end = hex.index(start, offsetBy: min(4, hex.count - offset))
            return String(hex[start..<end])
        }.joined(separator: " ")
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
