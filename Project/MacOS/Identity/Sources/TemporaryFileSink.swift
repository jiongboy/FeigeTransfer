import Foundation
import Darwin

/// Pins the chosen directory with a descriptor. Creation and promotion never follow
/// an attacker-controlled child symlink, and the final rename never overwrites a file.
public final class TemporaryFileSink: @unchecked Sendable {
    private let lock = NSLock()
    private var file: Int32 = -1
    private var directory: Int32 = -1
    private let temporaryName = ".feige-\(UUID().uuidString).part"
    private let root: URL
    private let fileName: String
    private var committedURL: URL?

    public convenience init(directoryURL: URL, manifest: FileManifest) throws {
        try self.init(directoryURL: directoryURL, metadata: FileMetadata(transferID: manifest.transferID, fileName: manifest.fileName, size: manifest.size))
    }

    public init(directoryURL: URL, metadata: FileMetadata) throws {
        root = directoryURL.resolvingSymlinksInPath().standardizedFileURL
        fileName = metadata.fileName
        directory = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard directory >= 0 else { throw Self.ioError() }
        file = openat(directory, temporaryName, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard file >= 0 else {
            let error = Self.ioError()
            Darwin.close(directory)
            directory = -1
            throw error
        }
    }

    public var savedURL: URL? { lock.withLock { committedURL } }

    public func write(_ data: Data) throws {
        try lock.withLock {
            guard file >= 0, committedURL == nil else { throw TransferSessionError.ioFailure }
            try data.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    let count = Darwin.write(file, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                    if count < 0 && errno == EINTR { continue }
                    guard count > 0 else { throw Self.ioError() }
                    offset += count
                }
            }
        }
    }

    public func commit() throws {
        try lock.withLock {
            guard file >= 0, committedURL == nil else { throw TransferSessionError.ioFailure }
            guard fsync(file) == 0 else { throw Self.ioError() }
            let nameURL = URL(fileURLWithPath: fileName)
            let ext = nameURL.pathExtension
            let stem = nameURL.deletingPathExtension().lastPathComponent
            for suffix in 0..<1000 {
                var candidate = fileName
                if suffix > 0 {
                    var tail = " (\(suffix))" + (ext.isEmpty ? "" : ".\(ext)")
                    var base = stem
                    if tail.utf8.count > 254 { tail = " (\(suffix))"; base = fileName }
                    while base.utf8.count + tail.utf8.count > 255 { base.removeLast() }
                    candidate = base + tail
                }
                if renameatx_np(directory, temporaryName, directory, candidate, UInt32(RENAME_EXCL)) == 0 {
                    committedURL = root.appendingPathComponent(candidate)
                    Darwin.close(file)
                    file = -1
                    return
                }
                guard errno == EEXIST else { throw Self.ioError() }
            }
            throw CocoaError(.fileWriteFileExists)
        }
    }

    public func discard() {
        lock.withLock {
            if file >= 0 { Darwin.close(file); file = -1 }
            if directory >= 0 {
                if committedURL == nil { unlinkat(directory, temporaryName, 0) }
                Darwin.close(directory)
                directory = -1
            }
        }
    }

    deinit { discard() }

    private static func ioError() -> Error { NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
}
