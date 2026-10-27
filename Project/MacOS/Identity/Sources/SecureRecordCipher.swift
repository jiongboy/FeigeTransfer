import Foundation
import CryptoKit

public enum SecureRecordError: LocalizedError {
    case invalidLength, invalidType, unexpectedSequence, authenticationFailed, sequenceExhausted, noProgress
    public var errorDescription: String? {
        switch self {
        case .invalidLength: "加密记录长度无效。"
        case .invalidType: "加密记录类型未知。"
        case .unexpectedSequence: "检测到重复、跳号或乱序记录。"
        case .authenticationFailed: "加密记录认证失败。"
        case .sequenceExhausted: "当前会话序列号已用尽，请重新握手。"
        case .noProgress: "加密记录流未取得进展。"
        }
    }
}

public struct OpenedRecord: Equatable {
    public let type: UInt8
    public let sequence: UInt64
    public let plaintext: Data
}

/// AES-GCM records for one direction of one fresh handshake session.
/// Instantiate only from a direction-specific HandshakeSession key. Never reuse an instance
/// for another connection or copy/restart one with the same key and sequence origin.
public final class SecureRecordCipher: @unchecked Sendable {
    public static let headerLength = 13
    public static let tagLength = 16
    public static let maxPlaintextLength = 64 * 1024

    private let key: SymmetricKey
    private let noncePrefix: Data
    private let lock = NSLock()
    private var nextSequence: UInt64 = 0

    public init(session: HandshakeSession, direction: HandshakeDirection) {
        switch direction {
        case .send:
            key = session.sendKey
            noncePrefix = session.sendNoncePrefix
        case .receive:
            key = session.receiveKey
            noncePrefix = session.receiveNoncePrefix
        }
    }

    public func seal(type: UInt8, plaintext: Data) throws -> Data {
        lock.lock()
        defer { lock.unlock() }
        guard Self.isKnownType(type) else { throw SecureRecordError.invalidType }
        guard plaintext.count <= Self.maxPlaintextLength,
              type == TransferMessageType.finish.rawValue || !plaintext.isEmpty else { throw SecureRecordError.invalidLength }
        guard nextSequence < UInt64.max else { throw SecureRecordError.sequenceExhausted }
        let sequence = nextSequence
        let ciphertextLength = UInt32(plaintext.count + Self.tagLength)
        var header = Data([type])
        header.appendUInt64(sequence)
        header.appendUInt32(ciphertextLength)
        let nonce = try makeNonce(sequence)
        let sealed: AES.GCM.SealedBox
        do { sealed = try AES.GCM.seal(plaintext, using: key, nonce: nonce, authenticating: header) }
        catch { throw SecureRecordError.authenticationFailed }
        let combined = sealed.ciphertext + sealed.tag
        guard combined.count == ciphertextLength else { throw SecureRecordError.invalidLength }
        nextSequence += 1
        return header + combined
    }

    public func open(_ frame: Data) throws -> OpenedRecord {
        lock.lock()
        defer { lock.unlock() }
        guard frame.count >= Self.headerLength + Self.tagLength,
              frame.count <= Self.headerLength + Self.maxPlaintextLength + Self.tagLength else {
            throw SecureRecordError.invalidLength
        }
        let type = frame[frame.startIndex]
        guard Self.isKnownType(type) else { throw SecureRecordError.invalidType }
        let sequence = frame.readUInt64(at: 1)
        let ciphertextLength = Int(frame.readUInt32(at: 9))
        guard ciphertextLength >= Self.tagLength,
              ciphertextLength <= Self.maxPlaintextLength + Self.tagLength,
              frame.count == Self.headerLength + ciphertextLength else {
            throw SecureRecordError.invalidLength
        }
        guard sequence == nextSequence, nextSequence < UInt64.max else { throw SecureRecordError.unexpectedSequence }
        let header = frame.prefix(Self.headerLength)
        let combined = frame.suffix(ciphertextLength)
        let nonce = try makeNonce(sequence)
        let box: AES.GCM.SealedBox
        do {
            box = try AES.GCM.SealedBox(nonce: nonce,
                                        ciphertext: Data(combined.dropLast(Self.tagLength)),
                                        tag: Data(combined.suffix(Self.tagLength)))
        } catch { throw SecureRecordError.invalidLength }
        let plaintext: Data
        do { plaintext = try AES.GCM.open(box, using: key, authenticating: Data(header)) }
        catch { throw SecureRecordError.authenticationFailed }
        guard plaintext.count <= Self.maxPlaintextLength,
              type == TransferMessageType.finish.rawValue || !plaintext.isEmpty else { throw SecureRecordError.invalidLength }
        nextSequence += 1
        return OpenedRecord(type: type, sequence: sequence, plaintext: plaintext)
    }

    private func makeNonce(_ sequence: UInt64) throws -> AES.GCM.Nonce {
        var counter = sequence.bigEndian
        var nonceData = noncePrefix
        withUnsafeBytes(of: &counter) { nonceData.append(contentsOf: $0) }
        return try AES.GCM.Nonce(data: nonceData)
    }

    private static func isKnownType(_ type: UInt8) -> Bool { (TransferMessageType.manifest.rawValue...TransferMessageType.textReceipt.rawValue).contains(type) }
}

public enum HandshakeDirection { case send, receive }

private extension Data {
    mutating func appendUInt32(_ value: UInt32) {
        var bigEndian = value.bigEndian
        Swift.withUnsafeBytes(of: &bigEndian) { append(contentsOf: $0) }
    }
    mutating func appendUInt64(_ value: UInt64) {
        var bigEndian = value.bigEndian
        Swift.withUnsafeBytes(of: &bigEndian) { append(contentsOf: $0) }
    }
    func readUInt32(at offset: Int) -> UInt32 {
        self[offset..<(offset + 4)].reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
    }
    func readUInt64(at offset: Int) -> UInt64 {
        self[offset..<(offset + 8)].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
    }
}
