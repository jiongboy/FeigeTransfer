import CryptoKit
import Foundation
import Security
import XCTest
import FeigeIdentity

/// The encrypted identity store must survive restarts unchanged and never depend on a Keychain ACL.
final class EncryptedIdentityStoreTests: XCTestCase {
    private func temporaryDirectory() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func service() -> String { "com.feige.encrypted.\(UUID())" }

    func testIdentityStableAcrossReloadAndSignatureVerifies() throws {
        let root = temporaryDirectory()
        let service = service()
        defer {
            EncryptedIdentityStore(directory: root, serviceIdentifier: service).delete()
            try? FileManager.default.removeItem(at: root)
        }
        let first = try EncryptedIdentityStore(directory: root, serviceIdentifier: service).loadOrCreate()
        let second = try EncryptedIdentityStore(directory: root, serviceIdentifier: service).loadOrCreate()
        XCTAssertEqual(first.fingerprint, second.fingerprint)
        XCTAssertEqual(first.publicKeyDER, second.publicKeyDER)

        let challenge = Data("FEIGE-ENCRYPTED-STORE".utf8)
        let algorithm = SecKeyAlgorithm.rsaSignatureMessagePSSSHA256
        var error: Unmanaged<CFError>?
        let signature = try XCTUnwrap(SecKeyCreateSignature(second.privateKey, algorithm, challenge as CFData, &error) as Data?)
        XCTAssertTrue(SecKeyVerifySignature(first.publicKey, algorithm, challenge as CFData, signature as CFData, &error))
    }

    func testPrivateKeyFileIsEncryptedAtRest() throws {
        let root = temporaryDirectory()
        let service = service()
        defer {
            EncryptedIdentityStore(directory: root, serviceIdentifier: service).delete()
            try? FileManager.default.removeItem(at: root)
        }
        let identity = try EncryptedIdentityStore(directory: root, serviceIdentifier: service).loadOrCreate()
        let keyFile = root.appendingPathComponent("identity.pkcs1.bin")
        let bytes = try Data(contentsOf: keyFile)
        XCTAssertFalse(bytes.isEmpty)
        // A raw PKCS#1 key starts with 0x30 (SEQUENCE); AES-GCM output must not, and the plaintext
        // never appears verbatim inside the sealed file.
        XCTAssertNotEqual(bytes.first, 0x30)
        var error: Unmanaged<CFError>?
        let raw = try XCTUnwrap(SecKeyCopyExternalRepresentation(identity.privateKey, &error) as Data?)
        XCTAssertNil(bytes.range(of: raw))
        let permissions = try FileManager.default.attributesOfItem(atPath: keyFile.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.intValue, 0o600)
    }

    func testWrappingKeyUsesNonACLGenericPasswordItem() throws {
        let root = temporaryDirectory()
        let service = service()
        defer {
            EncryptedIdentityStore(directory: root, serviceIdentifier: service).delete()
            try? FileManager.default.removeItem(at: root)
        }
        _ = try EncryptedIdentityStore(directory: root, serviceIdentifier: service).loadOrCreate()
        let raw = EncryptedIdentityStore.readKeychainItem(service: service, account: "identity-wrapping-key")
        XCTAssertEqual(raw?.count, 32)
        // No key-class item may be created any more: that kind of item is what triggers the password prompt.
        let keyQuery: [String: Any] = [kSecClass as String: kSecClassKey,
                                       kSecAttrApplicationTag as String: Data(service.utf8),
                                       kSecReturnRef as String: true]
        var result: CFTypeRef?
        XCTAssertNotEqual(SecItemCopyMatching(keyQuery as CFDictionary, &result), errSecSuccess)
    }

    func testDeleteRemovesIdentityAndNextLoadCreatesDifferentFingerprint() throws {
        let root = temporaryDirectory()
        let service = service()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = EncryptedIdentityStore(directory: root, serviceIdentifier: service)
        let original = try store.loadOrCreate()
        store.delete()
        XCTAssertNil(EncryptedIdentityStore.readKeychainItem(service: service, account: "identity-wrapping-key"))
        let recreated = try EncryptedIdentityStore(directory: root, serviceIdentifier: service).loadOrCreate()
        XCTAssertNotEqual(original.fingerprint, recreated.fingerprint)
        EncryptedIdentityStore(directory: root, serviceIdentifier: service).delete()
    }

    func testDamagedFileFailsInsteadOfSilentlyRotatingIdentity() throws {
        let root = temporaryDirectory()
        let service = service()
        defer {
            EncryptedIdentityStore(directory: root, serviceIdentifier: service).delete()
            try? FileManager.default.removeItem(at: root)
        }
        let store = EncryptedIdentityStore(directory: root, serviceIdentifier: service)
        let original = try store.loadOrCreate()
        let keyFile = root.appendingPathComponent("identity.pkcs1.bin")
        var bytes = try Data(contentsOf: keyFile)
        bytes[bytes.count / 2] ^= 0xff
        try bytes.write(to: keyFile)
        XCTAssertThrowsError(try EncryptedIdentityStore(directory: root, serviceIdentifier: service).loadOrCreate())
        // The damaged file is preserved so the user can still restore or back it up.
        XCTAssertTrue(FileManager.default.fileExists(atPath: keyFile.path))
        XCTAssertFalse(original.fingerprint.isEmpty)
    }

    func testSealAndOpenRoundTripAndRejectForeignKey() throws {
        let key = SymmetricKey(size: .bits256)
        let payload = Data("private-key-material".utf8)
        let sealed = try EncryptedIdentityStore.seal(payload, using: key)
        XCTAssertEqual(try EncryptedIdentityStore.open(sealed, using: key), payload)
        XCTAssertThrowsError(try EncryptedIdentityStore.open(sealed, using: SymmetricKey(size: .bits256)))
    }

    func testLegacyKeyMigrationPreservesFingerprint() throws {
        let root = temporaryDirectory()
        let service = service()
        defer {
            EncryptedIdentityStore(directory: root, serviceIdentifier: service).delete()
            LegacyKeychainKeyStore(serviceIdentifier: service).delete()
            try? FileManager.default.removeItem(at: root)
        }
        let legacy = LegacyKeychainKeyStore(serviceIdentifier: service)
        legacy.delete()
        let legacyIdentity = try DeviceIdentityStore(serviceIdentifier: service).loadOrCreate()

        let store = EncryptedIdentityStore(directory: root, serviceIdentifier: service)
        XCTAssertTrue(store.migrateLegacyKeyIfNeeded())
        let migrated = try store.loadOrCreate()
        XCTAssertEqual(migrated.fingerprint, legacyIdentity.fingerprint)
        // The legacy Keychain item is gone, so nothing can prompt for it again.
        XCTAssertNil(legacy.legacyIdentity())
        XCTAssertFalse(EncryptedIdentityStore(directory: root, serviceIdentifier: service).migrateLegacyKeyIfNeeded())
    }
}
