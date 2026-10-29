import Foundation
import Security
import CryptoKit

/// A device's public identity plus a handle to its Keychain-backed private key. `SecKey`
/// references are immutable and safe to use concurrently, so this is `Sendable` — callers
/// pass it across tasks to sign or verify without copying key material.
public struct DeviceIdentity: @unchecked Sendable {
    public let privateKey: SecKey
    public let publicKey: SecKey
    public let publicKeyDER: Data
    public let fingerprint: String

    /// Builds the identity from an already-resolved private key. Shared by the encrypted store and
    /// the legacy Keychain store so the public DER, fingerprint and grouped display stay identical.
    static func make(privateKey: SecKey) throws -> DeviceIdentity {
        guard let publicKey = SecKeyCopyPublicKey(privateKey) else {
            throw DeviceIdentityError.publicKeyExport(errSecDecode)
        }
        var error: Unmanaged<CFError>?
        guard let rawPublicKey = SecKeyCopyExternalRepresentation(publicKey, &error) as Data? else {
            let nsError = error?.takeRetainedValue() as Error? as NSError?
            throw DeviceIdentityError.publicKeyExport(OSStatus(nsError?.code ?? Int(errSecInternalComponent)))
        }
        let publicData = try DeviceIdentityStore.rsaSubjectPublicKeyInfo(from: rawPublicKey)
        let digest = SHA256.hash(data: publicData)
        let hex = digest.map { String(format: "%02X", $0) }.joined()
        guard !hex.isEmpty else { throw DeviceIdentityError.emptyFingerprint }
        let grouped = stride(from: 0, to: hex.count, by: 4).map { offset -> String in
            let start = hex.index(hex.startIndex, offsetBy: offset)
            let end = hex.index(start, offsetBy: min(4, hex.count - offset))
            return String(hex[start..<end])
        }.joined(separator: " ")
        return DeviceIdentity(privateKey: privateKey, publicKey: publicKey, publicKeyDER: publicData, fingerprint: grouped)
    }

    /// Trigger any system authorization before advertising or opening connections.
    /// A public-key lookup alone does not prove this process can use the private key.
    public func validateSigningAccess() throws {
        let algorithm = SecKeyAlgorithm.rsaSignatureMessagePSSSHA256
        guard SecKeyIsAlgorithmSupported(privateKey, .sign, algorithm) else {
            throw DeviceIdentityError.signingAuthorization(errSecUnimplemented)
        }
        let challenge = Data(("FEIGE-STARTUP-SIGNING-CHECK\0" + UUID().uuidString).utf8)
        var error: Unmanaged<CFError>?
        guard let signature = SecKeyCreateSignature(privateKey, algorithm, challenge as CFData, &error) else {
            let code = error?.takeRetainedValue() as Error? as NSError?
            throw DeviceIdentityError.signingAuthorization(OSStatus(code?.code ?? Int(errSecAuthFailed)))
        }
        guard SecKeyVerifySignature(publicKey, algorithm, challenge as CFData, signature, &error) else {
            if let error { _ = error.takeRetainedValue() }
            throw DeviceIdentityError.signingAuthorization(errSecVerifyFailed)
        }
    }
}

public enum DeviceIdentityError: LocalizedError {
    case keyGeneration(OSStatus)
    case publicKeyExport(OSStatus)
    case emptyFingerprint
    case signingAuthorization(OSStatus)
    case privateKeyUnreadable(OSStatus)
    case wrappingKeyUnavailable

    public var errorDescription: String? {
        switch self {
        case .keyGeneration(let status): "无法生成设备身份密钥（错误码 \(status)）。"
        case .publicKeyExport(let status): "无法导出设备公钥（错误码 \(status)）。"
        case .emptyFingerprint: "设备公钥指纹为空。"
        case .signingAuthorization(let status): "设备身份签名检查失败（错误码 \(status)），请重试或重新授权。"
        case .privateKeyUnreadable(let status):
            "本机设备身份无法读取（错误码 \(status)），原文件已保留。可备份资料后重置设备身份。"
        case .wrappingKeyUnavailable: "本机设备身份的加密密钥不可用，原身份文件已保留。"
        }
    }
}

/// Persistent per-installation identity backed by a non-exportable Keychain private key.
public struct DeviceIdentityStore: Sendable {
    private let applicationTag: Data

    public init(serviceIdentifier: String = "com.feige.transfer.identity") {
        applicationTag = Data(serviceIdentifier.utf8)
    }

    public func loadOrCreate() throws -> DeviceIdentity {
        if let existing = try loadPrivateKey() {
            return try DeviceIdentity.make(privateKey: existing)
        }
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeySizeInBits as String: 3072,
            kSecPrivateKeyAttrs as String: [
                kSecAttrIsPermanent as String: true,
                kSecAttrApplicationTag as String: applicationTag,
                kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            ]
        ]
        var error: Unmanaged<CFError>?
        guard let privateKey = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else {
            let nsError = error?.takeRetainedValue() as Error? as NSError?
            throw DeviceIdentityError.keyGeneration(OSStatus(nsError?.code ?? Int(errSecInternalComponent)))
        }
        return try DeviceIdentity.make(privateKey: privateKey)
    }

    /// Remove a dedicated Keychain identity. Intended for explicit account reset and isolated tests.
    @discardableResult
    public func delete() -> OSStatus {
        let query: [String: Any] = [kSecClass as String: kSecClassKey,
                                    kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
                                    kSecAttrApplicationTag as String: applicationTag]
        let status = SecItemDelete(query as CFDictionary)
        return status == errSecItemNotFound ? errSecSuccess : status
    }

    private func loadPrivateKey() throws -> SecKey? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrApplicationTag as String: applicationTag,
            kSecReturnRef as String: true
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw DeviceIdentityError.keyGeneration(status) }
        guard let key = result, CFGetTypeID(key) == SecKeyGetTypeID() else {
            throw DeviceIdentityError.keyGeneration(errSecDecode)
        }
        return (key as! SecKey)
    }

    public static func rsaSubjectPublicKeyInfo(from raw: Data) throws -> Data {
        guard raw.count > 0, raw.count <= 1024 else { throw DeviceIdentityError.publicKeyExport(errSecDecode) }
        let rsaEncryptionOID: [UInt8] = [0x30,0x0D,0x06,0x09,0x2A,0x86,0x48,0x86,0xF7,0x0D,0x01,0x01,0x01,0x05,0x00]
        var bitString = Data([0x03])
        bitString.append(derLength(raw.count + 1))
        bitString.append(0x00)
        bitString.append(raw)
        var body = Data(rsaEncryptionOID)
        body.append(bitString)
        var output = Data([0x30])
        output.append(derLength(body.count))
        output.append(body)
        return output
    }

    private static func derLength(_ length: Int) -> Data {
        if length < 128 { return Data([UInt8(length)]) }
        var value = length
        var bytes: [UInt8] = []
        while value > 0 {
            bytes.insert(UInt8(value & 0xff), at: 0)
            value >>= 8
        }
        return Data([0x80 | UInt8(bytes.count)] + bytes)
    }
}
