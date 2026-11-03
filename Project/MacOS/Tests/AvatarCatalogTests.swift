import XCTest
@testable import FeigeMac

final class AvatarCatalogTests: XCTestCase {
    func testSharedPaletteAndFallbackAreStable() {
        XCTAssertEqual(AvatarID.all, ["bird", "cat", "fox", "panda", "bear", "fish", "rocket", "sun", "star", "leaf"])
        XCTAssertEqual(AvatarID.normalize(nil), "bird")
        XCTAssertEqual(AvatarID.normalize("unknown"), "bird")
        XCTAssertEqual(AvatarID.normalize("fox"), "fox")
    }

    func testPersistedPeerAvatarKeyRequiresFullFingerprint() {
        let full = String(repeating: "aB", count: 32)
        XCTAssertEqual(AvatarID.normalizeFingerprint(full), full.uppercased())
        XCTAssertEqual(AvatarID.normalizeFingerprint(full.map { String($0) }.joined(separator: " ")), full.uppercased())
        XCTAssertNil(AvatarID.normalizeFingerprint("AABBCCDDEEFF0011"))
        XCTAssertNil(AvatarID.normalizeFingerprint("Feige-" + String(repeating: "A", count: 20)))
    }

    @MainActor
    func testEveryBundledAvatarResolvesToAnImage() {
        XCTAssertEqual(AvatarCatalog.choices.map(\.id), AvatarID.all)
        for choice in AvatarCatalog.choices {
            XCTAssertNotNil(AvatarCatalog.imageURL(for: choice.id), "Missing bundled resource URL for \(choice.id)")
            let image = AvatarCatalog.image(for: choice.id)
            XCTAssertGreaterThan(image.size.width, 0, "Missing bundled avatar \(choice.id)")
            XCTAssertGreaterThan(image.size.height, 0, "Missing bundled avatar \(choice.id)")
        }
    }
}
