import CryptoKit
import Foundation
import Security

/// Persistent per-installation identity whose RSA private key never touches a Keychain ACL.
///
/// Why not store the key with `kSecClassKey`: Keychain key items are governed by an access control
/// list. Unless the process is Apple-signed on a stable identity, macOS shows the "wants to use your
/// confidential information stored in Keychain" password prompt — and because an ad-hoc signature
/// changes with every rebuild, the prompt returns after each update. Removing the item type removes
/// the prompt; swapping the storage changes no wire format, because the identity is still the same
/// RSA-3072 SPKI and the same PSS signatures.
///
/// The private key is therefore stored as:
///
/// - `identity.pkcs1.bin` — PKCS#1 DER RSA private key, encrypted with AES-256-GCM;
/// - the 32-byte wrapping key — a data-protection Keychain generic-password item (`cku`), which is
///   bound to this login session and does not use the interactive ACL prompt path.
///
/// The trade-off is explicit: the key is exportable by any process running as the same user, so this
/// is weaker than a non-exportable Keychain key against local malware. The device identity is only
/// used to authenticate LAN peers to this user's own devices; it is not a signing key for released
/// software and it is not used to protect data at rest. See `Project/MacOS/README.md`.
public struct EncryptedIdentityStore: Sendable {
    public static let defaultServiceIdentifier = "com.feige.transfer.identity"

    private let directory: URL
    private let serviceIdentifier: String
    private let wrappingKeyAccount = "identity-wrapping-key"

    public init(directory: URL, serviceIdentifier: String = EncryptedIdentityStore.defaultServiceIdentifier) {
        self.directory = directory
        self.serviceIdentifier = serviceIdentifier
    }

    private var keyFile: URL { directory.appendingPathComponent("identity.pkcs1.bin") }

    /// Loads the stored identity, creating one on first use. Throws rather than silently rotating a
    /// damaged identity, so a peer never sees a changed fingerprint without an explicit reset.
    public func loadOrCreate() throws -> DeviceIdentity {
        if FileManager.default.fileExists(atPath: keyFile.path) {
            return try loadExisting()
        }
        let created = try createNew()
        return created
    }

    /// Adopts the identity of a release that still kept the key in a Keychain key item.
    ///
    /// Must run once before the first `loadOrCreate` on an upgraded install. Without it, upgrading
    /// would silently generate a new key and every peer would see this Mac as a different device.
    /// The legacy key is removed only after the encrypted copy is safely written; any failure keeps
    /// both copies, so a later attempt can retry instead of losing the fingerprint.
    @discardableResult
    public func migrateLegacyKeyIfNeeded() -> Bool {
        guard !FileManager.default.fileExists(atPath: keyFile.path) else { return false }
        let legacy = LegacyKeychainKeyStore(serviceIdentifier: serviceIdentifier)
        guard let identity = legacy.legacyIdentity() else { return false }
        var error: Unmanaged<CFError>?
        guard let der = SecKeyCopyExternalRepresentation(identity.privateKey, &error) as Data?,
              (try? store(der)) != nil else {
            return false
        }
        legacy.delete()
        return true
    }

    /// Destroys the local identity. The next `loadOrCreate` generates a new one with a new fingerprint.
    public func delete() {
        try? FileManager.default.removeItem(at: keyFile)
        Self.deleteKeychainItem(service: serviceIdentifier, account: wrappingKeyAccount)
        // Keys written by releases that still used a Keychain key item are removed too, so an
        // upgraded install never leaves an orphaned non-exportable key behind.
        _ = LegacyKeychainKeyStore(serviceIdentifier: serviceIdentifier).delete()
    }

    // MARK: creation and loading

    private func createNew() throws -> DeviceIdentity {
        var error: Unmanaged<CFError>?
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeySizeInBits as String: 3072,
        ]
        guard let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else {
            throw DeviceIdentityError.keyGeneration(OSStatus((error?.takeRetainedValue() as Error? as NSError?)?.code ?? Int(errSecInternalComponent)))
        }
        guard let der = SecKeyCopyExternalRepresentation(key, &error) as Data? else {
            throw DeviceIdentityError.publicKeyExport(errSecDecode)
        }
        try store(der)
        return try Self.identity(fromPrivateKeyDER: der)
    }

    private func loadExisting() throws -> DeviceIdentity {
        let sealed = try Data(contentsOf: keyFile)
        let wrappingKey = try wrappingKeyOrThrow()
        let plaintext = try Self.open(sealed, using: wrappingKey)
        return try Self.identity(fromPrivateKeyDER: plaintext)
    }

    private func store(_ privateKeyDER: Data) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let wrappingKey = try wrappingKeyOrCreate()
        let sealed = try Self.seal(privateKeyDER, using: wrappingKey)
        // Write to a sibling file and rename, so an interrupted write cannot destroy the identity.
        let temporary = directory.appendingPathComponent(".identity-\(UUID().uuidString).tmp")
        try sealed.write(to: temporary, options: [.atomic])
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        if FileManager.default.fileExists(atPath: keyFile.path) {
            try FileManager.default.removeItem(at: keyFile)
        }
        try FileManager.default.moveItem(at: temporary, to: keyFile)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: keyFile.path)
    }

    // MARK: crypto

    public static func seal(_ plaintext: Data, using key: SymmetricKey) throws -> Data {
        let box = try AES.GCM.seal(plaintext, using: key)
        guard let combined = box.combined else { throw DeviceIdentityError.privateKeyUnreadable(errSecDecode) }
        return combined
    }

    public static func open(_ sealed: Data, using key: SymmetricKey) throws -> Data {
        do {
            let box = try AES.GCM.SealedBox(combined: sealed)
            return try AES.GCM.open(box, using: key)
        } catch {
            throw DeviceIdentityError.privateKeyUnreadable(errSecDecode)
        }
    }

    public static func identity(fromPrivateKeyDER der: Data) throws -> DeviceIdentity {
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeyClass as String: kSecAttrKeyClassPrivate,
            kSecAttrKeySizeInBits as String: 3072,
        ]
        var error: Unmanaged<CFError>?
        guard let privateKey = SecKeyCreateWithData(der as CFData, attributes as CFDictionary, &error) else {
            throw DeviceIdentityError.privateKeyUnreadable(errSecDecode)
        }
        return try DeviceIdentity.make(privateKey: privateKey)
    }

    // MARK: silent wrapping key

    /// Reads the wrapping key from the data-protection Keychain; creates it when absent.
    private func wrappingKeyOrCreate() throws -> SymmetricKey {
        if let existing = Self.readKeychainItem(service: serviceIdentifier, account: wrappingKeyAccount) {
            guard existing.count == 32 else { throw DeviceIdentityError.privateKeyUnreadable(errSecDecode) }
            return SymmetricKey(data: existing)
        }
        let key = SymmetricKey(size: .bits256)
        let raw = key.withUnsafeBytes { Data($0) }
        var query: [String: Any] = baseQuery(account: wrappingKeyAccount)
        query[kSecValueData as String] = raw
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess || status == errSecDuplicateItem else {
            throw DeviceIdentityError.privateKeyUnreadable(status)
        }
        if status == errSecDuplicateItem, let existing = Self.readKeychainItem(service: serviceIdentifier, account: wrappingKeyAccount) {
            guard existing.count == 32 else { throw DeviceIdentityError.privateKeyUnreadable(errSecDecode) }
            return SymmetricKey(data: existing)
        }
        return key
    }

    private func wrappingKeyOrThrow() throws -> SymmetricKey {
        guard let raw = Self.readKeychainItem(service: serviceIdentifier, account: wrappingKeyAccount),
              raw.count == 32 else {
            throw DeviceIdentityError.wrappingKeyUnavailable
        }
        return SymmetricKey(data: raw)
    }

    private func baseQuery(account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: serviceIdentifier,
         kSecAttrAccount as String: account]
    }

    public static func readKeychainItem(service: String, account: String) -> Data? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account,
                                    kSecReturnData as String: true]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    public static func deleteKeychainItem(service: String, account: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        _ = SecItemDelete(query as CFDictionary)
    }
}

/// Reads and removes aliases left by releases that stored the identity as a Keychain key item.
///
/// Only migration support: new installs never create this item. `legacyIdentity()` lets an upgrade
/// keep its existing fingerprint instead of announcing a new device to peers.
public struct LegacyKeychainKeyStore: Sendable {
    private let applicationTag: Data

    public init(serviceIdentifier: String = EncryptedIdentityStore.defaultServiceIdentifier) {
        applicationTag = Data(serviceIdentifier.utf8)
    }

    public func legacyIdentity() -> DeviceIdentity? {
        let query: [String: Any] = [kSecClass as String: kSecClassKey,
                                    kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
                                    kSecAttrApplicationTag as String: applicationTag,
                                    kSecReturnRef as String: true]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let key = result, CFGetTypeID(key) == SecKeyGetTypeID() else { return nil }
        return try? DeviceIdentity.make(privateKey: key as! SecKey)
    }

    @discardableResult
    public func delete() -> OSStatus {
        let query: [String: Any] = [kSecClass as String: kSecClassKey,
                                    kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
                                    kSecAttrApplicationTag as String: applicationTag]
        let status = SecItemDelete(query as CFDictionary)
        return status == errSecItemNotFound ? errSecSuccess : status
    }
}
