import Foundation

/// Append-only diagnostic log in the application support folder, capped so it cannot grow without bound.
///
/// Only stages, version numbers and typed error names are recorded; never message bodies, file names or keys.
enum AppLogFile {
    private static let fileName = "app.log"
    private static let maxBytes = 256 * 1024
    private static let lock = NSLock()

    static func path(in directory: URL) -> URL { directory.appendingPathComponent(fileName) }

    /// Appends one line. Best effort: logging must never be the reason something fails.
    static func write(_ message: String, in directory: URL) {
        guard !message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        lock.lock()
        defer { lock.unlock() }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = path(in: directory)
            if let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber,
               size.intValue > maxBytes,
               let content = try? String(contentsOf: url, encoding: .utf8) {
                let lines = content.split(separator: "\n", omittingEmptySubsequences: false)
                let tail = lines.suffix(max(1, lines.count / 2)).joined(separator: "\n")
                try Data(tail.utf8).write(to: url, options: .atomic)
            }
            let formatter = ISO8601DateFormatter()
            formatter.timeZone = .current
            let line = "\(formatter.string(from: Date())) \(message)\n"
            guard let data = line.data(using: .utf8) else { return }
            if !FileManager.default.fileExists(atPath: url.path) {
                try data.write(to: url, options: .atomic)
                return
            }
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
        } catch {
            // Nothing useful can be done if even the log cannot be written.
        }
    }
}
