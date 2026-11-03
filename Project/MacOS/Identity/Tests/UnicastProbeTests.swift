import XCTest
@testable import FeigeIdentity

final class UnicastProbeTests: XCTestCase {
    private let advertisement = UnicastProbe.Advertisement(
        name: "Mac", fingerprint: String(repeating: "A", count: 64), capabilities: "3,4,5,6", port: 52000, avatarId: nil)

    func testTargetsAcceptOnlyPrivateAddressesAndSmallPrefixes() {
        XCTAssertEqual(ProbeTargets.hosts(in: "192.168.60.12"), [0xC0A8_3C0C])
        XCTAssertEqual(ProbeTargets.hosts(in: "10.0.0.0/24")?.count, 254)
        XCTAssertEqual(ProbeTargets.hosts(in: "172.16.1.4/31"), [0xAC10_0104, 0xAC10_0105])
        XCTAssertEqual(ProbeTargets.hosts(in: "169.254.1.1/32")?.count, 1)
        for invalid in ["8.8.8.8", "127.0.0.1", "172.32.0.1", "10.0.0.0/23", "10.0.0.0/33", "10.0.0.0/", "010.0.0.1",
                        "10.0", "10.0.", "8.8.8", "10.0.0.256", "host.local", "", "fe80::1"] {
            XCTAssertNil(ProbeTargets.hosts(in: invalid), invalid)
        }
    }

    func testThreeOctetShorthandMeansTheWhole24() {
        XCTAssertEqual(ProbeTargets.normalized(" 10.80.1 "), "10.80.1.0/24")
        XCTAssertEqual(ProbeTargets.normalized("10.80.1.5"), "10.80.1.5")
        XCTAssertEqual(ProbeTargets.normalized("10.80.1.0/28"), "10.80.1.0/28")
        XCTAssertEqual(ProbeTargets.hosts(in: "10.80.1"), ProbeTargets.hosts(in: "10.80.1.0/24"))
        XCTAssertNil(ProbeTargets.hosts(in: "10.80.1/24"))
    }

    func testCompanySubnetsApplyOnlyOnTheCompanyNetwork() {
        let office: UInt32 = 0x0A50_1E19, home: UInt32 = 0xC0A8_0105   // 10.80.30.25, 192.168.1.5
        XCTAssertEqual(ProbeTargets.automatic(for: [home, office]), ProbeTargets.company)
        XCTAssertEqual(ProbeTargets.automatic(for: [home, 0x0A51_0001]), [])   // 10.81.0.1
        XCTAssertEqual(ProbeTargets.roundHosts(saved: [], localAddresses: [home]), [])
        XCTAssertEqual(ProbeTargets.roundHosts(saved: [], localAddresses: [office]).count, 508)
        // A saved duplicate (in shorthand) is probed once; saved targets survive when the company ones do not fit.
        XCTAssertEqual(ProbeTargets.roundHosts(saved: ["10.80.1", "192.168.1.9"], localAddresses: [office]).count, 509)
        let full = (0..<16).map { "192.168.\($0).0/24" }
        XCTAssertEqual(ProbeTargets.roundHosts(saved: full, localAddresses: [office]), ProbeTargets.hosts(in: full))
        for target in ProbeTargets.company { XCTAssertEqual(ProbeTargets.hosts(in: target)?.count, 254) }
    }

    func testCommonTargetsAreDistinctValid24sThatFitTheLimits() {
        let targets = ProbeTargets.common.map(\.target)
        XCTAssertEqual(Set(targets).count, targets.count)
        for target in targets { XCTAssertEqual(ProbeTargets.hosts(in: target)?.count, 254, target) }
        XCTAssertNotNil(ProbeTargets.hosts(in: targets))
    }

    func testTargetListDeduplicatesAndEnforcesEntryLimit() {
        XCTAssertEqual(ProbeTargets.hosts(in: ["10.0.0.1", "10.0.0.0/30"])?.count, 2)
        XCTAssertNil(ProbeTargets.hosts(in: Array(repeating: "10.0.0.1", count: 17)))
        XCTAssertNil(ProbeTargets.hosts(in: ["10.0.0.1", "8.8.8.8"]))
    }

    func testRequestIsPaddedAndReplyNeverExceedsIt() throws {
        let nonce = Data(repeating: 7, count: 16).base64EncodedString()
        let request = try XCTUnwrap(ProbeMessage.request(nonce: nonce))
        XCTAssertEqual(request.count, ProbeMessage.datagramSize)
        let reply = try XCTUnwrap(ProbeMessage.reply(to: request, advertisement: advertisement))
        XCTAssertLessThanOrEqual(reply.count, request.count)
        let decoded = try XCTUnwrap(ProbeMessage.peer(from: reply, address: "10.0.0.2"))
        XCTAssertEqual(decoded.nonce, nonce)
        XCTAssertEqual(decoded.peer.protocolVersions, [4, 5, 6])
        XCTAssertEqual(decoded.peer.port, 52000)
    }

    func testUndersizedRequestGetsNoReply() throws {
        let nonce = Data(repeating: 7, count: 16).base64EncodedString()
        let request = try XCTUnwrap(ProbeMessage.request(nonce: nonce))
        XCTAssertNil(ProbeMessage.reply(to: request.prefix(100), advertisement: advertisement))
    }

    func testMalformedRepliesAreRejected() {
        func reply(_ name: String, _ fp: String, _ caps: String, _ port: Int = 1) -> Data {
            Data(#"{"feige":"reply","v":1,"nonce":"n","name":"\#(name)","fp":"\#(fp)","caps":"\#(caps)","port":\#(port)}"#.utf8)
        }
        let fp = String(repeating: "b", count: 64)
        XCTAssertEqual(ProbeMessage.peer(from: reply("A", fp, "4"), address: "10.0.0.1")?.peer.fingerprint, fp.uppercased())
        XCTAssertNil(ProbeMessage.peer(from: reply("", fp, "4"), address: "10.0.0.1"))
        XCTAssertNil(ProbeMessage.peer(from: reply("A", "zz", "4"), address: "10.0.0.1"))
        XCTAssertNil(ProbeMessage.peer(from: reply("A", fp, "3"), address: "10.0.0.1"))
        XCTAssertNil(ProbeMessage.peer(from: reply("A", fp, "4", 0), address: "10.0.0.1"))
        XCTAssertNil(ProbeMessage.peer(from: Data("not json".utf8), address: "10.0.0.1"))
    }

    func testLoopbackScanFindsResponderAndIgnoresOwnFingerprint() throws {
        let port: UInt16 = 52_800 + UInt16.random(in: 0..<100)
        let responder = UnicastProbe(advertisement: advertisement, respondsToProbes: true, port: port) { _ in }
        try responder.start()
        XCTAssertTrue(responder.canRespond)
        defer { responder.stop() }

        let found = expectation(description: "peer")
        var scannerAdvertisement = advertisement
        scannerAdvertisement.fingerprint = String(repeating: "B", count: 64)
        let scanner = UnicastProbe(advertisement: scannerAdvertisement, respondsToProbes: false, port: port) { peer in
            XCTAssertEqual(peer.fingerprint, String(repeating: "A", count: 64))
            XCTAssertEqual(peer.address, "127.0.0.1")
            found.fulfill()
        }
        try scanner.start()
        XCTAssertFalse(scanner.canRespond, "the port is taken and the scanner does not respond")
        defer { scanner.stop() }
        scanner.scan(hosts: [0x7F00_0001])
        wait(for: [found], timeout: 3)
    }
}
