import Foundation
import CryptoKit
import Darwin

/// Folder fields of a v6 manifest. Every file of one folder send repeats the shared fields.
public struct FolderInfo: Codable, Equatable, Sendable {
    public static let maximumFileCount = 10_000
    public static let maximumComponents = 32
    public static let maximumPathBytes = 1024

    public let rootName: String
    public let relativePath: String
    public let folderID: UUID
    public let fileCount: Int
    public let totalBytes: UInt64

    public init(rootName: String, relativePath: String, folderID: UUID, fileCount: Int, totalBytes: UInt64) throws {
        guard Self.isValidComponent(rootName), (1...Self.maximumFileCount).contains(fileCount),
              totalBytes <= UInt64(Int64.max), relativePath.utf8.count <= Self.maximumPathBytes else {
            throw TransferCoordinatorError.invalidManifest
        }
        let components = relativePath.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard (1...Self.maximumComponents).contains(components.count), components.allSatisfy(Self.isValidComponent) else {
            throw TransferCoordinatorError.invalidManifest
        }
        self.rootName = rootName
        self.relativePath = relativePath
        self.folderID = folderID
        self.fileCount = fileCount
        self.totalBytes = totalBytes
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(rootName: values.decode(String.self, forKey: .rootName),
                      relativePath: values.decode(String.self, forKey: .relativePath),
                      folderID: values.decode(UUID.self, forKey: .folderID),
                      fileCount: values.decode(Int.self, forKey: .fileCount),
                      totalBytes: values.decode(UInt64.self, forKey: .totalBytes))
    }

    public var components: [String] { relativePath.split(separator: "/").map(String.init) }

    /// True when the shared fields match another file of the same folder.
    public func sharesFolder(with other: FolderInfo) -> Bool {
        rootName == other.rootName && folderID == other.folderID && fileCount == other.fileCount && totalBytes == other.totalBytes
    }

    public static func isValidComponent(_ value: String) -> Bool {
        !value.isEmpty && value != "." && value != ".." && value.utf8.count <= 255 &&
            !value.contains("/") && !value.contains("\\") &&
            !value.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
    }
}

/// Sender-side identifiers. They are hashes so the peer never learns local paths.
public enum ResumeIdentifiers {
    public static func resumeID(for fileURL: URL, size: UInt64, modified: Date) -> UUID {
        uuid(from: "file\0\(fileURL.standardizedFileURL.path)\0\(size)\0\(modified.timeIntervalSince1970)")
    }

    public static func folderID(for folderURL: URL) -> UUID {
        uuid(from: "folder\0\(folderURL.standardizedFileURL.path)")
    }

    /// Receiver storage key: the authenticated peer identity is part of it, so another device cannot extend the file.
    public static func storageKey(peerFingerprint: String, id: UUID) -> String {
        let peer = peerFingerprint.filter { !$0.isWhitespace }.uppercased()
        let digest = SHA256.hash(data: Data((peer + "\0" + id.uuidString.lowercased()).utf8))
        return digest.map { String(format: "%02x", $0) }.joined().prefix(32).description
    }

    private static func uuid(from text: String) -> UUID {
        var bytes = Array(SHA256.hash(data: Data(text.utf8)).prefix(16))
        bytes[6] = (bytes[6] & 0x0f) | 0x50
        bytes[8] = (bytes[8] & 0x3f) | 0x80
        return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                           bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
    }
}

/// One file of a folder send, in sending order.
public struct FolderEntry: Sendable, Equatable {
    public let url: URL
    public let relativePath: String
    public let size: UInt64
    public let modified: Date
}

public enum FolderEnumerationError: LocalizedError {
    case empty, tooManyFiles, pathTooLong
    public var errorDescription: String? {
        switch self {
        case .empty: "文件夹中没有可发送的文件。"
        case .tooManyFiles: "文件夹超过 10000 个文件，请分批发送。"
        case .pathTooLong: "文件夹层级超过 32 层或路径过长，请整理后再发送。"
        }
    }
}

public enum FolderEnumerator {
    private static let skippedNames: Set<String> = [".DS_Store", "Thumbs.db"]

    /// Regular files only: symbolic links are never followed and empty directories are not sent.
    public static func entries(in folderURL: URL) throws -> [FolderEntry] {
        let root = folderURL.standardizedFileURL
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
        guard let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: keys, options: [],
                                                              errorHandler: { _, _ in false }) else {
            throw TransferSessionError.ioFailure
        }
        var result: [FolderEntry] = []
        let rootComponents = root.pathComponents.count
        for case let url as URL in enumerator {
            let values = try url.resourceValues(forKeys: Set(keys))
            // The enumerator never descends into links; skipDescendants() here would skip the next directory instead.
            if values.isSymbolicLink == true { continue }
            guard values.isRegularFile == true, !skippedNames.contains(url.lastPathComponent) else { continue }
            let components = Array(url.standardizedFileURL.pathComponents.dropFirst(rootComponents))
            let relative = components.joined(separator: "/")
            guard components.count <= FolderInfo.maximumComponents, relative.utf8.count <= FolderInfo.maximumPathBytes,
                  components.allSatisfy(FolderInfo.isValidComponent) else { throw FolderEnumerationError.pathTooLong }
            result.append(FolderEntry(url: url, relativePath: relative, size: UInt64(max(0, values.fileSize ?? 0)),
                                      modified: values.contentModificationDate ?? .distantPast))
            guard result.count <= FolderInfo.maximumFileCount else { throw FolderEnumerationError.tooManyFiles }
        }
        guard !result.isEmpty else { throw FolderEnumerationError.empty }
        return result.sorted { $0.relativePath < $1.relativePath }
    }
}

/// Keeps a partial file across disconnects in the receive directory as `.feige-resume-<key>.part`.
/// The final rename may land in a folder below the receive directory and never overwrites a file.
final class ResumableFileSink: IncomingFileSink, @unchecked Sendable {
    private let lock = NSLock()
    private var file: Int32 = -1
    private var partDirectory: Int32 = -1
    private var targetDirectory: Int32 = -1
    private let partName: String
    private let targetURL: URL
    private let fileName: String
    private var committedURL: URL?
    /// Bytes already on disk when the sink was opened; the first chunk must start here.
    /// A default value lets `deinit` close descriptors when the initializer throws.
    private(set) var existingLength: UInt64 = 0

    /// `targetDirectory` is borrowed and duplicated; the caller keeps its own descriptor.
    init(receiveDirectory: URL, key: String, size: UInt64, targetDirectory target: Int32, targetURL: URL, fileName: String,
         hasher: inout SHA256) throws {
        partName = ".feige-resume-\(key).part"
        self.targetURL = targetURL
        self.fileName = fileName
        partDirectory = open(receiveDirectory.resolvingSymlinksInPath().path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard partDirectory >= 0 else { throw Self.ioError() }
        targetDirectory = dup(target)
        guard targetDirectory >= 0 else { throw Self.ioError() }
        file = openat(partDirectory, partName, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
        var info = stat()
        guard file >= 0, fstat(file, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG else {
            throw file >= 0 ? TransferSessionError.ioFailure : Self.ioError()
        }
        var length = UInt64(info.st_size)
        if length > size {
            guard ftruncate(file, 0) == 0 else { throw Self.ioError() }
            length = 0
        }
        // Hash the retained prefix before accepting chunks.
        var remaining = length
        var buffer = [UInt8](repeating: 0, count: 1 << 20)
        while remaining > 0 {
            let count = pread(file, &buffer, min(buffer.count, Int(remaining)), off_t(length - remaining))
            if count < 0 && errno == EINTR { continue }
            guard count > 0 else { throw Self.ioError() }
            hasher.update(data: Data(buffer[0..<count]))
            remaining -= UInt64(count)
        }
        guard lseek(file, off_t(length), SEEK_SET) >= 0 else { throw Self.ioError() }
        existingLength = length
    }

    var savedURL: URL? { lock.withLock { committedURL } }

    func write(_ data: Data) throws {
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

    func commit() throws {
        try lock.withLock {
            guard file >= 0, committedURL == nil else { throw TransferSessionError.ioFailure }
            guard fsync(file) == 0 else { throw Self.ioError() }
            let candidate = try IncomingNames.rename(from: partDirectory, name: partName, to: targetDirectory, preferred: fileName)
            committedURL = targetURL.appendingPathComponent(candidate)
            closeDescriptors()
        }
    }

    /// Deletes the partial data after a digest mismatch so the next attempt starts at 0.
    func removePartial() {
        lock.withLock {
            if partDirectory >= 0, committedURL == nil { unlinkat(partDirectory, partName, 0) }
            closeDescriptors()
        }
    }

    /// Disconnects keep the partial file for a later resume.
    func discard() { lock.withLock { closeDescriptors() } }

    deinit { discard() }

    private func closeDescriptors() {
        if file >= 0 { Darwin.close(file); file = -1 }
        if partDirectory >= 0 { Darwin.close(partDirectory); partDirectory = -1 }
        if targetDirectory >= 0 { Darwin.close(targetDirectory); targetDirectory = -1 }
    }

    private static func ioError() -> Error { NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
}

/// The sink operations the connection's read loop needs from either sink type.
protocol IncomingFileSink: AnyObject, Sendable {
    var savedURL: URL? { get }
    func write(_ data: Data) throws
    func commit() throws
    func discard()
}

extension TemporaryFileSink: IncomingFileSink {}

enum IncomingNames {
    /// Moves `name` to the first free `preferred`, `stem (1).ext`, … name; never overwrites.
    static func rename(from source: Int32, name: String, to target: Int32, preferred: String) throws -> String {
        let nameURL = URL(fileURLWithPath: preferred)
        let ext = nameURL.pathExtension
        let stem = nameURL.deletingPathExtension().lastPathComponent
        for suffix in 0..<1000 {
            let candidate = suffix == 0 ? preferred : numbered(preferred, stem: stem, ext: ext, suffix: suffix)
            if renameatx_np(source, name, target, candidate, UInt32(RENAME_EXCL)) == 0 { return candidate }
            guard errno == EEXIST else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        }
        throw CocoaError(.fileWriteFileExists)
    }

    /// Creates the first free directory `name`, `name (1)`, … and never merges into an existing one.
    static func makeUniqueDirectory(in parent: Int32, preferred: String) throws -> String {
        for suffix in 0..<1000 {
            let candidate = suffix == 0 ? preferred : numbered(preferred, stem: preferred, ext: "", suffix: suffix)
            if mkdirat(parent, candidate, 0o755) == 0 { return candidate }
            guard errno == EEXIST else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        }
        throw CocoaError(.fileWriteFileExists)
    }

    private static func numbered(_ name: String, stem: String, ext: String, suffix: Int) -> String {
        var tail = " (\(suffix))" + (ext.isEmpty ? "" : ".\(ext)")
        var base = stem
        if tail.utf8.count > 254 { tail = " (\(suffix))"; base = name }
        while base.utf8.count + tail.utf8.count > 255 { base.removeLast() }
        return base + tail
    }
}

/// Receiver-side folder progress for one folder ID on one connection, persisted in
/// `.feige-folder-<key>.state`: the root directory name, then completed resume IDs.
final class FolderReceiveState: @unchecked Sendable {
    let info: FolderInfo
    let rootURL: URL
    private let receiveDirectory: Int32
    private let root: Int32
    private let stateName: String
    private(set) var completed: Set<UUID>
    var acceptedFiles = 0
    var acceptedBytes: UInt64 = 0
    var finishedBytes: UInt64 = 0

    init(info: FolderInfo, receiveDirectoryURL: URL, peerFingerprint: String) throws {
        self.info = info
        let base = receiveDirectoryURL.resolvingSymlinksInPath()
        receiveDirectory = open(base.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard receiveDirectory >= 0 else { throw Self.ioError() }
        stateName = ".feige-folder-\(ResumeIdentifiers.storageKey(peerFingerprint: peerFingerprint, id: info.folderID)).state"
        var rootName: String?
        var completed = Set<UUID>()
        if let text = Self.read(stateName, in: receiveDirectory) {
            let lines = text.split(separator: "\n").map(String.init)
            if let first = lines.first, FolderInfo.isValidComponent(first) {
                rootName = first
                completed = Set(lines.dropFirst().compactMap(UUID.init(uuidString:)))
            }
        }
        var descriptor: Int32 = -1
        if let existing = rootName {
            descriptor = openat(receiveDirectory, existing, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        }
        if descriptor < 0 {
            completed = []
            let created: String
            do { created = try IncomingNames.makeUniqueDirectory(in: receiveDirectory, preferred: info.rootName) }
            catch { Darwin.close(receiveDirectory); throw error }
            rootName = created
            descriptor = openat(receiveDirectory, created, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        }
        guard descriptor >= 0, let rootName else { let error = Self.ioError(); Darwin.close(receiveDirectory); throw error }
        root = descriptor
        rootURL = base.appendingPathComponent(rootName, isDirectory: true)
        self.completed = completed
        try persist(rootName: rootName)
    }

    deinit {
        Darwin.close(root)
        Darwin.close(receiveDirectory)
    }

    /// Opens (creating as needed) the parent directory of `info.relativePath` without following links.
    /// The caller closes the returned descriptor.
    func openParent(of info: FolderInfo) throws -> (descriptor: Int32, url: URL) {
        var current = dup(root)
        guard current >= 0 else { throw Self.ioError() }
        var url = rootURL
        for component in info.components.dropLast() {
            var next = openat(current, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            if next < 0 && errno == ENOENT {
                guard mkdirat(current, component, 0o755) == 0 || errno == EEXIST else {
                    let error = Self.ioError(); Darwin.close(current); throw error
                }
                next = openat(current, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            }
            Darwin.close(current)
            guard next >= 0 else { throw Self.ioError() }
            current = next
            url.appendPathComponent(component, isDirectory: true)
        }
        return (current, url)
    }

    func markCompleted(_ resumeID: UUID) throws {
        completed.insert(resumeID)
        try persist(rootName: rootURL.lastPathComponent)
    }

    func removeState() { unlinkat(receiveDirectory, stateName, 0) }

    private func persist(rootName: String) throws {
        let text = ([rootName] + completed.map(\.uuidString)).joined(separator: "\n") + "\n"
        let temporary = stateName + ".tmp"
        unlinkat(receiveDirectory, temporary, 0)
        let file = openat(receiveDirectory, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard file >= 0 else { throw Self.ioError() }
        defer { Darwin.close(file) }
        let bytes = Array(text.utf8)
        guard Darwin.write(file, bytes, bytes.count) == bytes.count, fsync(file) == 0,
              renameat(receiveDirectory, temporary, receiveDirectory, stateName) == 0 else { throw Self.ioError() }
    }

    private static func read(_ name: String, in directory: Int32) -> String? {
        let file = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard file >= 0 else { return nil }
        defer { Darwin.close(file) }
        var info = stat()
        guard fstat(file, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG, info.st_size <= 1 << 20 else { return nil }
        var buffer = [UInt8](repeating: 0, count: Int(info.st_size))
        guard Darwin.read(file, &buffer, buffer.count) == buffer.count else { return nil }
        return String(bytes: buffer, encoding: .utf8)
    }

    private static func ioError() -> Error { NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
}

public enum ResumableStorageCleanup {
    public static let retention: TimeInterval = 7 * 24 * 60 * 60

    /// Removes partial files and folder progress older than seven days from the receive directory.
    @discardableResult
    public static func removeExpired(in directoryURL: URL, now: Date = Date()) -> [String] {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: directoryURL.path) else { return [] }
        let directory = open(directoryURL.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard directory >= 0 else { return [] }
        defer { Darwin.close(directory) }
        var removed: [String] = []
        for name in names where (name.hasPrefix(".feige-resume-") && name.hasSuffix(".part")) ||
            (name.hasPrefix(".feige-folder-") && (name.hasSuffix(".state") || name.hasSuffix(".state.tmp"))) {
            var info = stat()
            guard fstatat(directory, name, &info, AT_SYMLINK_NOFOLLOW) == 0, (info.st_mode & S_IFMT) == S_IFREG else { continue }
            let modified = Date(timeIntervalSince1970: TimeInterval(info.st_mtimespec.tv_sec))
            if now.timeIntervalSince(modified) > retention, unlinkat(directory, name, 0) == 0 { removed.append(name) }
        }
        return removed
    }
}
