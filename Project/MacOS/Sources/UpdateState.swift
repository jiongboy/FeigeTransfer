import Foundation

/// Per-channel update state persisted next to the app data; deleting it only re-arms prompts.
struct UpdateState: Codable, Equatable {
    static let checkInterval: TimeInterval = 24 * 60 * 60

    var highestSeenBuild: Int = 0
    var lastAttemptAt: Date?
    var lastResult: String = ""
    var promptBuild: Int?
    var promptAt: Date?
    var skippedBuild: Int?
    var pendingRelease: UpdateRelease?

    /// Automatic checks are silent and rate limited: failures count exactly like successes.
    func isDue(now: Date) -> Bool {
        guard let lastAttemptAt else { return true }
        return now.timeIntervalSince(lastAttemptAt) >= Self.checkInterval
    }

    /// Same build prompts at most once a day; skipped builds never prompt again automatically.
    func shouldPrompt(build: Int, now: Date) -> Bool {
        if skippedBuild == build { return false }
        guard promptBuild == build, let promptAt else { return true }
        return now.timeIntervalSince(promptAt) >= Self.checkInterval
    }
}

/// The release read from the manifest survives restarts so "可以安装" is not lost on quit;
/// the downloaded file itself must still exist and match its digest before it may be used.
struct UpdateStateStore {
    let directory: URL
    private var url: URL { directory.appendingPathComponent("update-state.json") }

    func load() -> UpdateState {
        guard let data = try? Data(contentsOf: url),
              let state = try? JSONDecoder().decode(UpdateState.self, from: data) else { return UpdateState() }
        return state
    }

    func save(_ state: UpdateState) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let data = try JSONEncoder().encode(state)
        try data.write(to: url, options: .atomic)
    }
}

extension UpdateRelease: Codable {
    private enum CodingKeys: String, CodingKey {
        case version, build, publishedAt, minimumSystemVersion, summary, notesUrl, downloadUrl, size, sha256, protocolVersion
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(version: try values.decode(String.self, forKey: .version),
                  build: try values.decode(Int.self, forKey: .build),
                  publishedAt: try values.decode(String.self, forKey: .publishedAt),
                  minimumSystemVersion: try values.decode(String.self, forKey: .minimumSystemVersion),
                  summary: try values.decode(String.self, forKey: .summary),
                  notesUrl: try values.decode(URL.self, forKey: .notesUrl),
                  downloadUrl: try values.decode(URL.self, forKey: .downloadUrl),
                  size: try values.decode(Int64.self, forKey: .size),
                  sha256: try values.decode(String.self, forKey: .sha256),
                  protocolVersion: try values.decode(Int.self, forKey: .protocolVersion))
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(version, forKey: .version)
        try values.encode(build, forKey: .build)
        try values.encode(publishedAt, forKey: .publishedAt)
        try values.encode(minimumSystemVersion, forKey: .minimumSystemVersion)
        try values.encode(summary, forKey: .summary)
        try values.encode(notesUrl, forKey: .notesUrl)
        try values.encode(downloadUrl, forKey: .downloadUrl)
        try values.encode(size, forKey: .size)
        try values.encode(sha256, forKey: .sha256)
        try values.encode(protocolVersion, forKey: .protocolVersion)
    }
}
