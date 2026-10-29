import Foundation

/// Where update manifests and packages are published.
///
/// `UpdateSource.json` ships inside the app bundle (`Contents/Resources/Updates/`) so it can be
/// updated only together with a signed, notarised build. When no trustworthy source is
/// configured the client stays offline and says so instead of guessing an endpoint.
struct UpdateSource: Decodable, Equatable, Sendable {
    struct Origin: Decodable, Equatable, Sendable {
        let base: URL
        let hosts: [String]
        let allowInsecurePrivateNetwork: Bool

        private enum CodingKeys: String, CodingKey { case base, hosts, allowInsecurePrivateNetwork }

        init(base: URL, hosts: [String], allowInsecurePrivateNetwork: Bool = false) {
            self.base = base
            self.hosts = hosts
            self.allowInsecurePrivateNetwork = allowInsecurePrivateNetwork
        }

        init(from decoder: Decoder) throws {
            let values = try decoder.container(keyedBy: CodingKeys.self)
            base = try values.decode(URL.self, forKey: .base)
            hosts = try values.decode([String].self, forKey: .hosts)
            allowInsecurePrivateNetwork = try values.decodeIfPresent(Bool.self, forKey: .allowInsecurePrivateNetwork) ?? false
        }
    }

    let channel: String
    let keyId: String
    let publicKey: String
    let algorithm: String
    let origins: [Origin]

    /// `false` while the file carries placeholders; the client then never touches the network.
    var isConfigured: Bool {
        algorithm == "p256" || algorithm == "ed25519"
            ? !keyId.isEmpty && !publicKey.isEmpty && !origins.isEmpty
            : false
    }

    func validate() throws {
        guard channel == "stable" || channel == "preview" else {
            throw UpdateSourceError.invalid("更新渠道无效。")
        }
        guard algorithm == "p256" || algorithm == "ed25519" else {
            throw UpdateSourceError.invalid("更新签名算法不受支持。")
        }
        guard !keyId.isEmpty, !publicKey.isEmpty else {
            throw UpdateSourceError.invalid("更新签名公钥尚未配置。")
        }
        guard !origins.isEmpty else { throw UpdateSourceError.invalid("更新来源尚未配置。") }
        for origin in origins {
            let scheme = origin.base.scheme
            let host = origin.base.host
            guard scheme == "https" || scheme == "http", let host, origin.hosts.contains(host),
                  !origin.hosts.isEmpty,
                  origin.base.query == nil, origin.base.fragment == nil, origin.base.user == nil else {
                throw UpdateSourceError.invalid("更新来源地址无效或未列在可信主机中。")
            }
            if scheme == "http" {
                let isLoopback = host == "127.0.0.1" || host == "localhost"
                let isApprovedLAN = origin.allowInsecurePrivateNetwork
                    && host == "192.168.50.229"
                    && origin.base.port == 8080
                guard isLoopback || isApprovedLAN else {
                    throw UpdateSourceError.invalid("明文 HTTP 更新来源只允许回环地址或显式信任的局域网更新服务器。")
                }
            }
        }
    }

    /// Manifest address for an origin; only the configured host and channel path are allowed.
    func manifestURL(for origin: Origin) -> URL {
        origin.base.appendingPathComponent("macos").appendingPathComponent(channel)
            .appendingPathComponent("feige-macos-update.json")
    }

    func allows(_ url: URL, origin: Origin) -> Bool {
        guard url.scheme == origin.base.scheme,
              let host = url.host, origin.hosts.contains(host), host == origin.base.host,
              effectivePort(url) == effectivePort(origin.base),
              url.query == nil, url.fragment == nil, url.user == nil else { return false }
        let basePath = origin.base.path.hasSuffix("/") ? origin.base.path : origin.base.path + "/"
        return url.path.hasPrefix(basePath) && !url.path.contains("..")
    }

    private func effectivePort(_ url: URL) -> Int? {
        url.port ?? (url.scheme == "https" ? 443 : url.scheme == "http" ? 80 : nil)
    }

    static func load(bundle: Bundle = .main, developmentRoot: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent()) -> UpdateSource? {
        // The packaged app carries Resources/Updates/UpdateSource.json; `swift run` has no bundle, so
        // a development build reads the same file from the package folder. Both are repository files.
        let candidates = [
            bundle.url(forResource: "UpdateSource", withExtension: "json", subdirectory: "Updates"),
            bundle.url(forResource: "UpdateSource", withExtension: "json"),
            developmentRoot.appendingPathComponent("Updates/UpdateSource.json")
        ].compactMap { $0 }
        for url in candidates {
            guard let data = try? Data(contentsOf: url) else { continue }
            do { return try JSONDecoder().decode(UpdateSource.self, from: data) }
            catch { AppLogFile.write("update: config decode failed \(error)", in: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("FeigeTransfer")) }
        }
        return nil
    }
}

enum UpdateSourceError: LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        switch self { case .invalid(let message): message }
    }
}

enum UpdateClientError: LocalizedError, Equatable {
    case notFound, badStatus(Int), redirectRejected, sizeMismatch, digestMismatch, cancelled

    var errorDescription: String? {
        switch self {
        case .notFound: "尚无可用的更新发布信息"
        case .badStatus(let code): "更新服务返回异常状态（\(code)）"
        case .redirectRejected: "更新服务重定向到了未经许可的地址"
        case .sizeMismatch: "下载大小与发布信息不一致，已停止安装"
        case .digestMismatch: "下载内容校验失败，已停止安装"
        case .cancelled: "已取消"
        }
    }
}

/// Streams a package to an update-only folder and verifies size and SHA-256 before anything else runs.
enum UpdateDownloader {
    enum Progress { case downloading(received: Int64, total: Int64), verifying }
    typealias ProgressHandler = @Sendable (Progress) -> Void

    static func download(_ release: UpdateRelease, origin: UpdateSource.Origin, source: UpdateSource,
                         into directory: URL, session: URLSession,
                         progress: ProgressHandler?) async throws -> URL {
        guard source.allows(release.downloadUrl, origin: origin) else { throw UpdateClientError.redirectRejected }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let partial = directory.appendingPathComponent(".feige-update-\(UUID().uuidString).part")
        let final = directory.appendingPathComponent("FeigeMac-\(release.version)-\(release.build).zip")
        try? FileManager.default.removeItem(at: final)
        try Data().write(to: partial)

        var request = URLRequest(url: release.downloadUrl)
        request.timeoutInterval = 60
        request.cachePolicy = .reloadIgnoringLocalCacheData
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            try? FileManager.default.removeItem(at: partial)
            throw http(response)
        }
        // URLSession streams report the request URL here even after a redirect, so the URL checks
        // above and the size/SHA-256 checks below are the ones that hold a redirected download to
        // the signed manifest; the final package is never trusted on the strength of the address alone.
        if let finalURL = http.url, !source.allows(finalURL, origin: origin) {
            try? FileManager.default.removeItem(at: partial)
            throw UpdateClientError.redirectRejected
        }
        guard let handle = FileHandle(forWritingAtPath: partial.path) else {
            throw CocoaError(.fileWriteUnknown)
        }
        defer { try? handle.close() }
        var received: Int64 = 0
        var buffer = Data()
        buffer.reserveCapacity(64 * 1024)
        do {
            for try await byte in bytes {
                buffer.append(byte)
                if buffer.count >= 64 * 1024 {
                    try handle.write(contentsOf: buffer)
                    received += Int64(buffer.count)
                    progress?(.downloading(received: received, total: release.size))
                    buffer.removeAll(keepingCapacity: true)
                }
            }
            if !buffer.isEmpty {
                try handle.write(contentsOf: buffer)
                received += Int64(buffer.count)
            }
        } catch {
            try? FileManager.default.removeItem(at: partial)
            throw error
        }
        try handle.synchronize()
        try handle.close()
        guard received == release.size else {
            try? FileManager.default.removeItem(at: partial)
            throw UpdateClientError.sizeMismatch
        }
        progress?(.verifying)
        guard try digest(of: partial) == release.sha256 else {
            try? FileManager.default.removeItem(at: partial)
            throw UpdateClientError.digestMismatch
        }
        try FileManager.default.moveItem(at: partial, to: final)
        return final
    }

    /// Streams the SHA-256 so a large package never has to fit in memory.
    static func digest(of url: URL) throws -> String {
        try SHA256Text.hexDigest(of: url)
    }

    private static func http(_ response: URLResponse?) -> Error {
        guard let http = response as? HTTPURLResponse else { return UpdateClientError.badStatus(0) }
        return http.statusCode == 404 ? UpdateClientError.notFound : UpdateClientError.badStatus(http.statusCode)
    }
}

extension URL {
    /// HTTPS always; HTTP only for a loopback test origin, and never below the configured scheme.
    /// Distinct from `UpdateSource.scheme` so the member of the memberwise model is never picked.
    func isAllowedUpdateScheme(_ configured: String) -> Bool {
        guard let scheme else { return false }
        switch scheme {
        case "https": return configured == "https" || configured == "http"
        case "http": return configured == "http" && (host == "127.0.0.1" || host == "localhost")
        default: return false
        }
    }
}
