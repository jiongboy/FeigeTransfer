import XCTest
import FeigeIdentity
import Security

final class DeviceIdentityTests: XCTestCase {
    func testIdentityIsStableAcrossLoads() throws {
        let service = "com.feige.tests.\(UUID().uuidString)"
        let store = DeviceIdentityStore(serviceIdentifier: service)
        defer { XCTAssertEqual(store.delete(), errSecSuccess) }
        let first = try store.loadOrCreate()
        try first.validateSigningAccess()
        let second = try store.loadOrCreate()

        XCTAssertEqual(first.fingerprint, second.fingerprint)
        XCTAssertEqual(first.publicKeyDER, second.publicKeyDER)
        XCTAssertEqual(first.publicKeyDER.first, 0x30)
        XCTAssertTrue(first.publicKeyDER.contains(Data([0x06,0x09,0x2A,0x86,0x48,0x86,0xF7,0x0D,0x01,0x01,0x01])))
        XCTAssertEqual(first.fingerprint.filter(\.isHexDigit).count, 64)
        XCTAssertEqual(first.fingerprint, first.fingerprint.uppercased())
        XCTAssertFalse(first.fingerprint.hasPrefix(" "))
        XCTAssertFalse(first.fingerprint.hasSuffix(" "))

        let attributes = SecKeyCopyAttributes(first.publicKey) as? [String: Any]
        XCTAssertEqual(attributes?[kSecAttrKeySizeInBits as String] as? Int, 3072)
    }
}
