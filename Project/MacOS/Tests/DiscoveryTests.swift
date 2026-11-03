import XCTest
import Network
@testable import FeigeMac

final class DiscoveryTests: XCTestCase {
    func testBonjourProtocolMetadataRecognizesDualStackAndWindowsV4Service() {
        XCTAssertEqual(LANService.protocolVersions(from: ["v": "3", "caps": "3,4,5", "fp": String(repeating: "A", count: 64)]), [3, 4, 5])
        XCTAssertEqual(LANService.protocolVersions(from: ["version": "4", "capabilities": "4"]), [4])
        XCTAssertNil(LANService.protocolVersions(from: ["version": "2"]))
    }

    func testAvatarTXTIsOptionalValidatedAndBackwardCompatible() {
        let fingerprint = String(repeating: "A", count: 64)
        let current = LANService.txtRecordValues(deviceName: "Mac", fingerprint: fingerprint, avatarId: "fox")
        XCTAssertEqual(current["avatar"], "fox")
        XCTAssertEqual(current["fp"], fingerprint)
        XCTAssertEqual(LANService.avatarId(from: current), "fox")

        let legacy = LANService.txtRecordValues(deviceName: "Old Mac", fingerprint: fingerprint, avatarId: nil)
        XCTAssertNil(legacy["avatar"])
        XCTAssertNil(LANService.avatarId(from: legacy))
        XCTAssertNil(LANService.avatarId(from: ["avatar": "unknown"]))
    }

    @MainActor
    func testSpoofedBonjourAvatarHintDoesNotPersistForKnownConversationFingerprint() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        let fingerprint = String(repeating: "A", count: 64)
        model.messages = [ChatMessage(peerID: fingerprint, peerName: "Known peer", direction: .received,
                                      kind: .text, text: "previous conversation", status: .received)]
        let endpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: 54321)
        model.nearbyDevices = [LANDevice(serviceName: "Feige-\(fingerprint.prefix(20))", displayName: "Spoofed",
                                         endpoint: endpoint, protocolVersions: [4], fingerprintHint: fingerprint,
                                         avatarId: "fox")]

        XCTAssertEqual(model.avatarId(for: fingerprint, hint: "fox"), "fox", "Pre-auth hint may be displayed transiently")
        XCTAssertNil(model.settings.peerAvatarIds[fingerprint], "Unauthenticated mDNS data must never be persisted as a trusted peer avatar")
    }

    @MainActor
    func testBonjourDiscoversOtherInstanceAndFiltersSelf() async throws {
        guard ProcessInfo.processInfo.environment["FEIGE_TEST_BONJOUR"] == "1" else {
            throw XCTSkip("Opt in with FEIGE_TEST_BONJOUR=1; requires local network permission and mDNS.")
        }
        let first = LANService()
        let second = LANService()
        defer { first.stop(); second.stop() }
        let one = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        let two = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        var foundByFirst: [LANDevice] = []
        var foundBySecond: [LANDevice] = []
        first.onDevices = { foundByFirst = $0 }
        second.onDevices = { foundBySecond = $0 }
        try first.start(deviceName: "Test-One", fingerprint: one)
        try second.start(deviceName: "Test-Two", fingerprint: two)
        let deadline = Date().addingTimeInterval(10)
        while Date() < deadline {
            if foundByFirst.contains(where: { $0.displayName == "Test-Two" }),
               foundBySecond.contains(where: { $0.displayName == "Test-One" }) { break }
            try await Task.sleep(for: .milliseconds(20))
        }
        XCTAssertTrue(foundByFirst.contains(where: { $0.displayName == "Test-Two" }))
        XCTAssertTrue(foundBySecond.contains(where: { $0.displayName == "Test-One" }))
        XCTAssertFalse(foundByFirst.contains(where: { $0.serviceName == "Feige-" + one.prefix(20) }))
        XCTAssertFalse(foundBySecond.contains(where: { $0.serviceName == "Feige-" + two.prefix(20) }))
    }
}
