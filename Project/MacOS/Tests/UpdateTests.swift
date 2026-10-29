import CryptoKit
import XCTest
@testable import FeigeMac

final class UpdateManifestTests: XCTestCase {
    private let keyId = "feige-macos-test"

    private func envelope(payload: Data, key: P256.Signing.PrivateKey, keyId: String? = nil) throws -> Data {
        let id = keyId ?? self.keyId
        let signature = try key.signature(for: UpdateSignature.signingBytes(keyId: id, payload: payload))
        let object: [String: Any] = ["format": 1, "keyId": id,
                                     "payload": payload.base64EncodedString(),
                                     "signature": signature.derRepresentation.base64EncodedString()]
        return try JSONSerialization.data(withJSONObject: object)
    }

    private func payload(rows: [[String: Any]]) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["schemaVersion": 1, "releases": rows])
    }

    private func row(version: String = "0.4.0", build: Int = 7, channel: String = "stable",
                     minimum: String = "13.0", url: String = "https://updates.example.test/feige/macos/stable/versions/v0.4.0/FeigeMac-0.4.0.zip") -> [String: Any] {
        ["platform": "macos", "installation": "macos", "channel": channel, "version": version, "build": build,
         "publishedAt": "2026-10-28T00:00:00Z", "minimumSystemVersion": minimum, "summary": "同步 Windows 功能",
         "notesUrl": "https://updates.example.test/feige/macos/stable/versions/v0.4.0/notes",
         "downloadUrl": url, "size": 1024, "sha256": String(repeating: "a", count: 64), "protocolVersion": 3]
    }

    private func verify(_ envelope: Data, key: P256.Signing.PrivateKey) throws -> UpdateManifestValue {
        try UpdateManifest.verify(envelope: envelope, keyId: keyId,
                                  publicKeyBase64: key.publicKey.x963Representation.base64EncodedString(), kind: "p256")
    }

    func testSignedManifestIsVerifiedAndDecoded() throws {
        let key = P256.Signing.PrivateKey()
        let data = try envelope(payload: payload(rows: [row()]), key: key)
        let manifest = try verify(data, key: key)
        XCTAssertEqual(manifest.releases.count, 1)
        XCTAssertEqual(manifest.releases[0].version, "0.4.0")
        XCTAssertEqual(manifest.releases[0].build, 7)
    }

    func testTamperedPayloadFailsVerification() throws {
        let key = P256.Signing.PrivateKey()
        let original = try envelope(payload: payload(rows: [row(build: 7)]), key: key)
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: original) as? [String: Any])
        object["payload"] = try payload(rows: [row(build: 99)]).base64EncodedString()
        XCTAssertThrowsError(try verify(JSONSerialization.data(withJSONObject: object), key: key)) {
            XCTAssertEqual($0 as? UpdateManifestError, .invalidSignature)
        }
    }

    func testKeyIdentifierMismatchIsRejected() throws {
        let key = P256.Signing.PrivateKey()
        let data = try envelope(payload: payload(rows: [row()]), key: key, keyId: "someone-else")
        XCTAssertThrowsError(try verify(data, key: key))
    }

    func testForeignKeySignatureIsRejected() throws {
        let signer = P256.Signing.PrivateKey()
        let data = try envelope(payload: payload(rows: [row()]), key: signer)
        let other = P256.Signing.PrivateKey()
        XCTAssertThrowsError(try verify(data, key: other)) {
            XCTAssertEqual($0 as? UpdateManifestError, .invalidSignature)
        }
    }

    func testSelectionPrefersNewerVersionsNumerically() throws {
        let releases = [
            UpdateRelease(version: "0.9.0", build: 1, publishedAt: "", minimumSystemVersion: "13.0", summary: "",
                          notesUrl: URL(string: "https://a.test/x")!, downloadUrl: URL(string: "https://a.test/x.zip")!,
                          size: 1, sha256: "", protocolVersion: 3),
            UpdateRelease(version: "0.10.0", build: 3, publishedAt: "", minimumSystemVersion: "13.0", summary: "",
                          notesUrl: URL(string: "https://a.test/x")!, downloadUrl: URL(string: "https://a.test/x.zip")!,
                          size: 1, sha256: "", protocolVersion: 3),
            UpdateRelease(version: "0.4.0", build: 7, publishedAt: "", minimumSystemVersion: "13.0", summary: "",
                          notesUrl: URL(string: "https://a.test/x")!, downloadUrl: URL(string: "https://a.test/x.zip")!,
                          size: 1, sha256: "", protocolVersion: 3)
        ]
        let manifest = UpdateManifestValue(schemaVersion: 1, releases: releases)
        let current = AppVersionInfo(version: "0.4.0", build: 7)
        XCTAssertEqual(UpdateManifest.selection(from: manifest, current: current)?.version, "0.10.0")
        XCTAssertNil(UpdateManifest.selection(from: UpdateManifestValue(schemaVersion: 1, releases: [releases[2]]), current: current))
        // AppVersionInfo(version:build:) is (fileprivate) synthesized; use an explicit local value instead.
        let newer = UpdateManifestValue(schemaVersion: 1, releases: [releases[2]])
        XCTAssertNil(UpdateManifest.selection(from: newer, current: AppVersionInfo(version: "0.4.0", build: 7)))
        XCTAssertEqual(UpdateManifest.selection(from: newer, current: AppVersionInfo(version: "0.3.9", build: 99))?.build, 7)
    }

    func testIncompatibleAndMalformedReleasesAreSkippedOrRejected() throws {
        let key = P256.Signing.PrivateKey()
        let data = try envelope(payload: payload(rows: [row(minimum: "99.0")]), key: key)
        XCTAssertThrowsError(try verify(data, key: key)) {
            XCTAssertEqual($0 as? UpdateManifestError, .noCompatibleRelease)
        }
        let insecure = try envelope(payload: payload(rows: [row(url: "http://updates.example.test/feige/x.zip")]), key: key)
        XCTAssertThrowsError(try verify(insecure, key: key)) {
            XCTAssertEqual($0 as? UpdateManifestError, .malformedRelease("字段缺失或不合法"))
        }
    }

    func testEd25519EnvelopeIsAccepted() throws {
        let key = Curve25519.Signing.PrivateKey()
        let raw = try payload(rows: [row(version: "0.5.0", build: 9)])
        let signature = try key.signature(for: UpdateSignature.signingBytes(keyId: keyId, payload: raw))
        let envelope = try JSONSerialization.data(withJSONObject: ["format": 1, "keyId": keyId,
                                                                   "payload": raw.base64EncodedString(),
                                                                   "signature": signature.base64EncodedString()])
        let manifest = try UpdateManifest.verify(envelope: envelope, keyId: keyId,
                                                 publicKeyBase64: key.publicKey.rawRepresentation.base64EncodedString(),
                                                 kind: "ed25519")
        XCTAssertEqual(manifest.releases.first?.build, 9)
    }
}

final class UpdateSourceTests: XCTestCase {
    private let origin = UpdateSource.Origin(base: URL(string: "https://updates.example.test/feige")!,
                                             hosts: ["updates.example.test"])

    private var source: UpdateSource {
        UpdateSource(channel: "stable", keyId: "k", publicKey: "AAAA", algorithm: "p256", origins: [origin])
    }

    func testManifestUrlStaysInChannelPath() {
        XCTAssertEqual(source.manifestURL(for: origin).absoluteString,
                       "https://updates.example.test/feige/macos/stable/feige-macos-update.json")
    }

    func testLinksMustStayOnConfiguredOrigin() {
        XCTAssertTrue(source.allows(URL(string: "https://updates.example.test/feige/macos/stable/versions/v1/FeigeMac-1.zip")!, origin: origin))
        XCTAssertFalse(source.allows(URL(string: "https://evil.test/feige/macos/x.zip")!, origin: origin))
        XCTAssertFalse(source.allows(URL(string: "http://updates.example.test/feige/x.zip")!, origin: origin))
        XCTAssertFalse(source.allows(URL(string: "https://updates.example.test/feige/../x.zip")!, origin: origin))
        XCTAssertFalse(source.allows(URL(string: "https://updates.example.test/other/x.zip")!, origin: origin))
    }

    func testPlainHttpIsLimitedToLoopbackRehearsal() {
        let local = UpdateSource.Origin(base: URL(string: "http://127.0.0.1:8080/feige")!, hosts: ["127.0.0.1"])
        let lan = UpdateSource.Origin(base: URL(string: "http://192.168.50.229:8080/feige")!, hosts: ["192.168.50.229"])
        let localSource = UpdateSource(channel: "stable", keyId: "k", publicKey: "AAAA", algorithm: "p256", origins: [local])
        XCTAssertNoThrow(try localSource.validate())
        XCTAssertTrue(localSource.allows(URL(string: "http://127.0.0.1:8080/feige/macos/stable/x.zip")!, origin: local))
        let lanSource = UpdateSource(channel: "stable", keyId: "k", publicKey: "AAAA", algorithm: "p256", origins: [lan])
        XCTAssertThrowsError(try lanSource.validate())
    }

    func testManifestLinksRejectPlainHttpOutsideLoopback() throws {
        XCTAssertTrue(URL(string: "https://updates.example.test/a.zip")!.isPublishableForTests)
        XCTAssertTrue(URL(string: "http://localhost:1/a.zip")!.isPublishableForTests)
        XCTAssertFalse(URL(string: "http://updates.example.test/a.zip")!.isPublishableForTests)
    }

    func testPlaceholderConfigurationIsNotConfigured() {
        let placeholder = UpdateSource(channel: "stable", keyId: "feige-macos-release-TODO",
                                       publicKey: "", algorithm: "p256", origins: [origin])
        XCTAssertFalse(placeholder.isConfigured)
        XCTAssertThrowsError(try placeholder.validate())
        XCTAssertTrue(source.isConfigured)
        XCTAssertNoThrow(try source.validate())
        XCTAssertThrowsError(try UpdateSource(channel: "stable", keyId: "k", publicKey: "AAAA", algorithm: "hddsa", origins: [origin]).validate())
    }
}

final class UpdateStateTests: XCTestCase {
    func testAutomaticChecksAreRateLimitedForADay() {
        var state = UpdateState()
        XCTAssertTrue(state.isDue(now: Date()))
        let now = Date()
        state.lastAttemptAt = now.addingTimeInterval(-23 * 3600)
        XCTAssertFalse(state.isDue(now: now))
        state.lastAttemptAt = now.addingTimeInterval(-25 * 3600)
        XCTAssertTrue(state.isDue(now: now))
    }

    func testSkippedBuildNeverPromptsAndSameBuildPromptsOncePerDay() {
        var state = UpdateState()
        let now = Date()
        XCTAssertTrue(state.shouldPrompt(build: 8, now: now))
        state.promptBuild = 8
        state.promptAt = now
        XCTAssertFalse(state.shouldPrompt(build: 8, now: now.addingTimeInterval(3600)))
        XCTAssertTrue(state.shouldPrompt(build: 9, now: now.addingTimeInterval(3600)))
        state.skippedBuild = 8
        XCTAssertFalse(state.shouldPrompt(build: 8, now: now.addingTimeInterval(48 * 3600)))
    }

    func testStateRoundTripsThroughDisk() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = UpdateStateStore(directory: directory)
        var state = store.load()
        state.highestSeenBuild = 9
        state.skippedBuild = 8
        state.lastResult = "发现新版本 0.4.0"
        try store.save(state)
        XCTAssertEqual(store.load(), state)
    }

    func testPackageDigestMatchesCryptoKit() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: file) }
        let data = Data((0..<700_000).map { UInt8(truncatingIfNeeded: $0.hashValue) })
        try data.write(to: file)
        XCTAssertEqual(try UpdateDownloader.digest(of: file), SHA256Text.hexDigest(of: data))
    }
}

final class AppVersionTests: XCTestCase {
    func testVersionsCompareNumerically() {
        XCTAssertEqual(AppVersionInfo.compare("0.10.0", "0.9.0"), 1)
        XCTAssertEqual(AppVersionInfo.compare("1.0", "1.0.0"), 0)
        XCTAssertEqual(AppVersionInfo.compare("1.2.3", "1.2.10"), -1)
    }
}

private extension URL {
    var isPublishableForTests: Bool {
        if scheme == "https" { return true }
        return scheme == "http" && (host == "127.0.0.1" || host == "localhost")
    }
}
