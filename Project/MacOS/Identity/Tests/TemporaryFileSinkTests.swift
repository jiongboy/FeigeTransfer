import XCTest
import CryptoKit
import FeigeIdentity

final class TemporaryFileSinkTests: XCTestCase {
    func testCommitPreservesExistingFileAndSymlink() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let original = root.appendingPathComponent("report.txt")
        try Data("original".utf8).write(to: original)
        let link = root.appendingPathComponent("report (1).txt")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: original)
        let bytes = Data("new".utf8)
        let manifest = try FileManifest(fileName: "report.txt", size: 3, sha256: Data(SHA256.hash(data: bytes)))
        let sink = try TemporaryFileSink(directoryURL: root, manifest: manifest)
        try sink.write(bytes)
        XCTAssertNil(sink.savedURL)
        try sink.commit()
        XCTAssertEqual(sink.savedURL?.lastPathComponent, "report (2).txt")
        XCTAssertEqual(try Data(contentsOf: original), Data("original".utf8))
        XCTAssertEqual(try Data(contentsOf: XCTUnwrap(sink.savedURL)), bytes)
        sink.discard()
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path).count, 3)
    }

    func testAbandonedTransferRemovesTemporaryFile() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let manifest = try FileManifest(fileName: "incomplete.bin", size: 10, sha256: Data(repeating: 0, count: 32))
        let sink = try TemporaryFileSink(directoryURL: root, manifest: manifest)
        try sink.write(Data([1]))
        sink.discard()
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    }

    func testRecoveryRemovesOnlyPrivateRegularPartialFiles() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let orphan = root.appendingPathComponent(".feige-\(UUID().uuidString).part")
        try Data("partial".utf8).write(to: orphan)
        let ordinary = root.appendingPathComponent("notes.part")
        try Data("keep".utf8).write(to: ordinary)
        let victim = root.appendingPathComponent("victim.txt")
        try Data("keep".utf8).write(to: victim)
        let symlink = root.appendingPathComponent(".feige-\(UUID().uuidString).part")
        try FileManager.default.createSymbolicLink(at: symlink, withDestinationURL: victim)

        let removed = try TemporaryFileSinkRecovery.removeOrphanedFiles(in: root)
        XCTAssertEqual(removed.map(\.lastPathComponent), [orphan.lastPathComponent])
        XCTAssertFalse(FileManager.default.fileExists(atPath: orphan.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: ordinary.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: symlink.path))
        XCTAssertEqual(try Data(contentsOf: victim), Data("keep".utf8))
    }

    func testRecoveryRejectsSymlinkedDirectory() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let target = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        defer {
            try? FileManager.default.removeItem(at: root)
            try? FileManager.default.removeItem(at: target)
        }
        let link = root.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)
        XCTAssertThrowsError(try TemporaryFileSinkRecovery.removeOrphanedFiles(in: link))
    }
}
