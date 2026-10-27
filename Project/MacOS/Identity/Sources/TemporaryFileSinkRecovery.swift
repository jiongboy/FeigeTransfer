import Foundation
import Darwin

/// Removes abandoned Feige partial files left behind by a crash or forced termination.
/// Only regular files with the exact private temporary-name pattern are considered.
public enum TemporaryFileSinkRecovery {
    public static func removeOrphanedFiles(in directoryURL: URL) throws -> [URL] {
        let root = directoryURL.standardizedFileURL
        let directory = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard directory >= 0 else { throw ioError() }
        defer { Darwin.close(directory) }

        let scanDescriptor = dup(directory)
        guard scanDescriptor >= 0, let stream = fdopendir(scanDescriptor) else {
            if scanDescriptor >= 0 { Darwin.close(scanDescriptor) }
            throw ioError()
        }
        defer { closedir(stream) }

        var removed: [URL] = []
        while true {
            errno = 0
            guard let entry = readdir(stream) else {
                if errno != 0 { throw ioError() }
                break
            }
            let name = withUnsafePointer(to: entry.pointee.d_name) { pointer in
                pointer.withMemoryRebound(to: CChar.self, capacity: Int(NAME_MAX) + 1) {
                    String(cString: $0)
                }
            }
            guard isTemporaryName(name) else { continue }

            var info = stat()
            let status = name.withCString { fstatat(directory, $0, &info, AT_SYMLINK_NOFOLLOW) }
            guard status == 0 else {
                if errno == ENOENT { continue }
                throw ioError()
            }
            guard (info.st_mode & S_IFMT) == S_IFREG else { continue }

            let result = name.withCString { unlinkat(directory, $0, 0) }
            guard result == 0 || errno == ENOENT else { throw ioError() }
            if result == 0 { removed.append(root.appendingPathComponent(name)) }
        }
        return removed
    }

    private static func isTemporaryName(_ name: String) -> Bool {
        let prefix = ".feige-"
        let suffix = ".part"
        guard name.hasPrefix(prefix), name.hasSuffix(suffix) else { return false }
        let identifier = name.dropFirst(prefix.count).dropLast(suffix.count)
        return UUID(uuidString: String(identifier)) != nil
    }

    private static func ioError() -> Error {
        NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }
}
