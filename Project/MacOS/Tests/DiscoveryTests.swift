import XCTest
@testable import FeigeMac

final class DiscoveryTests: XCTestCase {
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
