import Foundation

/// One published macOS release entry from a signed update manifest.
struct UpdateRelease: Equatable, Identifiable, Sendable {
    var id: String { "\(version)-\(build)" }
    let version: String
    let build: Int
    let publishedAt: String
    let minimumSystemVersion: String
    let summary: String
    let notesUrl: URL
    let downloadUrl: URL
    let size: Int64
    let sha256: String
    let protocolVersion: Int

    var releaseText: String {
        "\(version) · 构建 \(build) · \(publishedAt)\n\(SizeText.format(size))\n\(summary)"
    }
}

struct UpdateManifestValue: Equatable, Sendable {
    var schemaVersion: Int
    var releases: [UpdateRelease]
}

/// Parses and verifies the update envelope. Only the structure is shared with the Windows
/// updater; the signature algorithm and the package formats stay platform specific.
enum UpdateManifest {
    static let maximumPayloadBytes = 512 * 1024
    static let maximumReleases = 64

    /// Fetches `<base>/macos/<channel>/feige-macos-update.json` and returns the verified releases.
    static func load(session: URLSession, endpoint: URL, keyId: String, publicKeyBase64: String, kind: String) async throws -> UpdateManifestValue {
        var request = URLRequest(url: endpoint)
        request.timeoutInterval = 20
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw UpdateClientError.notFound }
        if http.statusCode == 404 { throw UpdateClientError.notFound }
        guard http.statusCode == 200 else { throw UpdateClientError.badStatus(http.statusCode) }
        guard data.count <= maximumPayloadBytes * 2 else { throw UpdateManifestError.malformedEnvelope }
        return try verify(envelope: data, keyId: keyId, publicKeyBase64: publicKeyBase64, kind: kind)
    }

    /// Verifies the signature envelope and decodes the payload it protects.
    static func verify(envelope: Data, keyId: String, publicKeyBase64: String, kind: String) throws -> UpdateManifestValue {
        guard let object = try? JSONSerialization.jsonObject(with: envelope) as? [String: Any],
              (object["format"] as? Int) == 1,
              let envelopeKeyId = object["keyId"] as? String, envelopeKeyId == keyId,
              let payloadBase64 = object["payload"] as? String,
              let signatureBase64 = object["signature"] as? String,
              let payload = Data(base64Encoded: payloadBase64),
              let signature = Data(base64Encoded: signatureBase64) else {
            throw UpdateManifestError.malformedEnvelope
        }
        guard payload.count <= maximumPayloadBytes else { throw UpdateManifestError.invalidPayload }
        try UpdateSignature.verify(message: UpdateSignature.signingBytes(keyId: keyId, payload: payload),
                                   signature: signature, publicKeyBase64: publicKeyBase64, kind: kind)
        return try decode(payload: payload)
    }

    static func decode(payload: Data) throws -> UpdateManifestValue {
        guard let object = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
              (object["schemaVersion"] as? Int) == 1,
              let rows = object["releases"] as? [[String: Any]], rows.count <= maximumReleases else {
            throw UpdateManifestError.invalidPayload
        }
        var releases: [UpdateRelease] = []
        for row in rows where (row["platform"] as? String) == "macos" {
            guard let channel = row["channel"] as? String, channel == AppVersionInfo.channel,
                  let installation = row["installation"] as? String, installation == "macos",
                  let version = row["version"] as? String, isNumericVersion(version),
                  let build = number(row["build"]), build > 0,
                  let publishedAt = row["publishedAt"] as? String,
                  let minimum = row["minimumSystemVersion"] as? String,
                  let summary = row["summary"] as? String,
                  let notesUrl = URL(string: row["notesUrl"] as? String ?? ""), notesUrl.isPublishableLink,
                  let downloadUrl = URL(string: row["downloadUrl"] as? String ?? ""), downloadUrl.isPublishableLink,
                  downloadUrl.pathExtension.lowercased() == "zip",
                  let size = number(row["size"]), size > 0,
                  let sha256 = row["sha256"] as? String, sha256.count == 64,
                  sha256.allSatisfy({ $0.isHexDigit }),
                  let protocolVersion = number(row["protocolVersion"]) else {
                throw UpdateManifestError.malformedRelease("字段缺失或不合法")
            }
            guard compareVersions(minimum, currentSystemVersion()) <= 0 else { continue }
            releases.append(UpdateRelease(version: version, build: Int(build), publishedAt: publishedAt,
                                          minimumSystemVersion: minimum, summary: summary,
                                          notesUrl: notesUrl, downloadUrl: downloadUrl, size: size,
                                          sha256: sha256.lowercased(), protocolVersion: Int(protocolVersion)))
        }
        guard !releases.isEmpty else { throw UpdateManifestError.noCompatibleRelease }
        return UpdateManifestValue(schemaVersion: 1, releases: releases)
    }

    /// Highest (version, build) strictly newer than the running build, or `nil` when current.
    static func selection(from manifest: UpdateManifestValue, current: AppVersionInfo) -> UpdateRelease? {
        manifest.releases
            .filter { update in
                let order = compareVersions(update.version, current.version)
                return order > 0 || (order == 0 && update.build > current.build)
            }
            .max { lhs, rhs in
                let order = compareVersions(lhs.version, rhs.version)
                return order == 0 ? lhs.build < rhs.build : order < 0
            }
    }

    static func compareVersions(_ lhs: String, _ rhs: String) -> Int { AppVersionInfo.compare(lhs, rhs) }

    static func currentSystemVersion() -> String {
        let value = ProcessInfo.processInfo.operatingSystemVersion
        return "\(value.majorVersion).\(value.minorVersion).\(value.patchVersion)"
    }

    private static func isNumericVersion(_ value: String) -> Bool {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        return !parts.isEmpty && parts.count <= 4 && parts.allSatisfy { !$0.isEmpty && $0.allSatisfy(\.isNumber) }
    }

    private static func number(_ value: Any?) -> Int64? {
        if let value = value as? Int64 { return value }
        if let value = value as? Int { return Int64(value) }
        if let value = value as? Double { return Int64(value) }
        if let value = value as? String { return Int64(value) }
        return nil
    }
}

extension URL {
    /// HTTP is parsed only for loopback rehearsal or the explicitly supported Mac mini endpoint;
    /// UpdateSource still verifies the exact configured origin and path before use.
    var isPublishableLink: Bool {
        if scheme == "https" { return true }
        guard scheme == "http" else { return false }
        if host == "127.0.0.1" || host == "localhost" { return true }
        return host == "192.168.50.229" && port == 8080
    }
}
