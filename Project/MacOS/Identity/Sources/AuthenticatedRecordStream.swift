import Foundation
import CryptoKit

public enum AuthenticatedStreamError: LocalizedError {
    case truncated, tooLarge, invalidFrame, unexpectedEOF, timeout, alreadyFinished
    public var errorDescription: String? {
        switch self {
        case .truncated: "认证流帧被截断。"
        case .tooLarge: "认证流帧超过长度上限。"
        case .invalidFrame: "认证流帧格式无效。"
        case .unexpectedEOF: "认证流在完成前结束。"
        case .timeout: "局域网传输超时。"
        case .alreadyFinished: "认证流已经结束。"
        }
    }
}

/// Incremental bounded record codec. It accepts only AES-GCM authenticated records and
/// rejects trailing data after an authenticated finish record.
public final class AuthenticatedRecordStream {
    public static let maxFrameLength = SecureRecordCipher.headerLength + SecureRecordCipher.maxPlaintextLength + SecureRecordCipher.tagLength
    public static let maxReadBatchLength = (maxFrameLength + 4) * 8
    private let cipher: SecureRecordCipher
    private var input = Data()
    private var sendFinished = false
    private var receiveFinished = false
    private let lock = NSLock()
    private let finishClosesStream: Bool

    public init(cipher: SecureRecordCipher, finishClosesStream: Bool = true) {
        self.cipher = cipher
        self.finishClosesStream = finishClosesStream
    }

    public func encode(type: UInt8, payload: Data) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        guard !sendFinished else { throw AuthenticatedStreamError.alreadyFinished }
        let sealed = try cipher.seal(type: type, plaintext: payload)
        guard sealed.count <= Self.maxFrameLength else { throw AuthenticatedStreamError.tooLarge }
        var length = UInt32(sealed.count).bigEndian
        var framed = Data()
        withUnsafeBytes(of: &length) { framed.append(contentsOf: $0) }
        framed.append(sealed)
        if type == TransferMessageType.finish.rawValue && finishClosesStream { sendFinished = true }
        return framed
    }

    /// Feed arbitrary TCP fragments. Complete authenticated records are emitted in sequence.
    public func append(_ bytes: Data) throws -> [OpenedRecord] {
        lock.lock(); defer { lock.unlock() }
        guard !receiveFinished else {
            if bytes.isEmpty { return [] }
            throw AuthenticatedStreamError.alreadyFinished
        }
        guard bytes.count <= Self.maxReadBatchLength,
              input.count + bytes.count <= Self.maxReadBatchLength + Self.maxFrameLength + 4 else {
            throw AuthenticatedStreamError.tooLarge
        }
        input.append(bytes)
        var records: [OpenedRecord] = []
        while input.count >= 4 {
            let length = Int(input.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) })
            guard length >= SecureRecordCipher.headerLength + SecureRecordCipher.tagLength,
                  length <= Self.maxFrameLength else { throw AuthenticatedStreamError.invalidFrame }
            guard input.count >= 4 + length else { break }
            let frame = Data(input[4..<(4 + length)])
            let record = try cipher.open(frame)
            input.removeSubrange(0..<(4 + length))
            if record.type == TransferMessageType.finish.rawValue && finishClosesStream {
                guard input.isEmpty else { throw AuthenticatedStreamError.invalidFrame }
                receiveFinished = true
            }
            records.append(record)
        }
        return records
    }

    public func endOfStream() throws {
        lock.lock(); defer { lock.unlock() }
        guard input.isEmpty, receiveFinished else { throw AuthenticatedStreamError.unexpectedEOF }
    }
}
