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
                        "10.0.0", "10.0.0.256", "host.local", "", "fe80::1"] {
            XCTAssertNil(ProbeTargets.hosts(in: invalid), invalid)
        }
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
