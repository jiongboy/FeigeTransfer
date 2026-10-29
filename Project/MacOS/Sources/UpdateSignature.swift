import CryptoKit
import Foundation

/// Signature verification for the update manifest.
///
/// Uses CryptoKit's P-256 and Ed25519 support (both available on macOS 13). Only the envelope
/// structure is shared with the Windows updater; algorithms, download formats and trust roots
/// stay platform specific.
enum UpdateSignature {
    /// The exact bytes a publisher signs: `Feige.Update.v1\n<keyId>\n` followed by the raw payload.
    static func signingBytes(keyId: String, payload: Data) -> Data {
        var data = Data("Feige.Update.v1\n\(keyId)\n".utf8)
        data.append(payload)
        return data
    }

    /// Verifies `signature` over `message` with a base64-encoded public key (`kind`: `p256` or `ed25519`).
    ///
    /// Any malformed key or signature throws; callers treat every failure as fatal and never
    /// fall back to an unsigned payload.
    static func verify(message: Data, signature: Data, publicKeyBase64: String, kind: String) throws {
        guard let keyData = Data(base64Encoded: publicKeyBase64), !keyData.isEmpty else {
            throw UpdateManifestError.invalidKey
        }
        switch kind {
        case "p256":
            let key = try p256PublicKey(keyData)
            let value = try P256.Signing.ECDSASignature(derRepresentation: signature)
            guard key.isValidSignature(value, for: message) else { throw UpdateManifestError.invalidSignature }
        case "ed25519":
            guard keyData.count == 32 else { throw UpdateManifestError.invalidKey }
            let key = try Curve25519.Signing.PublicKey(rawRepresentation: keyData)
            guard key.isValidSignature(signature, for: message) else { throw UpdateManifestError.invalidSignature }
        default:
            throw UpdateManifestError.invalidKey
        }
    }

    private static func p256PublicKey(_ data: Data) throws -> P256.Signing.PublicKey {
        if data.count == 65 || data.count == 33 {
            return try P256.Signing.PublicKey(x963Representation: data)
        }
        return try P256.Signing.PublicKey(rawRepresentation: data)
    }
}

enum UpdateManifestError: LocalizedError, Equatable {
    case invalidKey, invalidSignature, malformedEnvelope, invalidPayload, malformedRelease(String), noCompatibleRelease

    var errorDescription: String? {
        switch self {
        case .invalidKey: "更新签名公钥无效，已停止更新。"
        case .invalidSignature: "更新清单签名验证失败，可能被篡改，已停止更新。"
        case .malformedEnvelope: "更新清单格式不受支持。"
        case .invalidPayload: "更新清单内容无效。"
        case .malformedRelease(let detail): "更新清单中的发布信息无效：\(detail)"
        case .noCompatibleRelease: "当前系统暂无兼容的更新。"
        }
    }
}
