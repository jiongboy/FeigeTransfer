import XCTest
import CryptoKit
@testable import FeigeIdentity

final class ConnectionTests: XCTestCase {
    private func connections() async throws -> (FileConnection, FileConnection) {
        let a = DeviceIdentityStore(serviceIdentifier: "com.feige.v2.a.\(UUID())")
        let b = DeviceIdentityStore(serviceIdentifier: "com.feige.v2.b.\(UUID())")
        defer { a.delete(); b.delete() }
        let identityA = try a.loadOrCreate()
        let identityB = try b.loadOrCreate()
        let (first, second) = InMemoryDuplexTransport.pair()
        async let connecting = ConnectionService.establish(transport: first, identity: identityA, role: .initiator, deviceName: "A") { _ in
            XCTFail("requester must not be prompted"); return false
        }
        let responder = try await ConnectionService.establish(transport: second, identity: identityB, role: .responder, deviceName: "B") { _ in true }
        return (try await connecting, responder)
    }

    func testSameConnectionHandlesDuplexTransfersEmptyFilesAndNameCollisions() async throws {
        let (a, b) = try await connections()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let atA = root.appendingPathComponent("A")
        let atB = root.appendingPathComponent("B")
        try FileManager.default.createDirectory(at: atA, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: atB, withIntermediateDirectories: true)
        defer { a.close(); b.close(); try? FileManager.default.removeItem(at: root) }
        let bytesA = Data(repeating: 42, count: 140_000)
        let bytesB = Data(repeating: 21, count: 90_000)
        let sourceA = atA.appendingPathComponent("a.bin")
        let sourceB = atB.appendingPathComponent("b.bin")
        try bytesA.write(to: sourceA)
        try bytesB.write(to: sourceB)
        let readerA = Task { try await a.run(receiveDirectory: atA, didReceive: { _, _ in }) }
        let readerB = Task { try await b.run(receiveDirectory: atB, didReceive: { _, _ in }) }
        defer { readerA.cancel(); readerB.cancel() }
        async let forward = a.sendFile(sourceA)
        async let reverse = b.sendFile(sourceB)
        _ = try await (forward, reverse)
        XCTAssertEqual(try Data(contentsOf: atB.appendingPathComponent("a.bin")), bytesA)
        XCTAssertEqual(try Data(contentsOf: atA.appendingPathComponent("b.bin")), bytesB)
        _ = try await a.sendFile(sourceA)
        XCTAssertEqual(try Data(contentsOf: atB.appendingPathComponent("a (1).bin")), bytesA)
        let empty = root.appendingPathComponent("empty.txt")
        try Data().write(to: empty)
        _ = try await a.sendFile(empty)
        XCTAssertEqual(try Data(contentsOf: atB.appendingPathComponent("empty.txt")).count, 0)
        a.close(); b.close()
        _ = try? await readerA.value
        _ = try? await readerB.value
    }

    func testV2AcceptsLargeMetadataButRejectsInvalidPathsAndOffsets() throws {
        let metadata = try FileMetadata(fileName: "large.bin", size: 10 * 1024 * 1024 * 1024 * 1024)
        XCTAssertEqual(try JSONDecoder().decode(FileMetadata.self, from: JSONEncoder().encode(metadata)), metadata)
        XCTAssertNoThrow(try FileManifest(fileName: "large.bin", size: metadata.size, sha256: Data(repeating: 0, count: 32)))
        XCTAssertThrowsError(try FileMetadata(fileName: "../escape", size: 1))
        XCTAssertThrowsError(try FileMetadata(fileName: "large.bin", size: UInt64.max))
        XCTAssertNoThrow(try FileMetadata(fileName: "empty", size: 0))
    }

    func testVersionsCannotSilentlyDowngradeToOldConfirmationFlow() throws {
        let one = try HandshakeOffer(version: 1, role: .initiator, deviceName: "Old", identityPublicKeyDER: Data([1]),
                                     ephemeralPublicKey: Data(repeating: 1, count: 32), nonce: Data(repeating: 1, count: 32), sessionCode: "123456")
        let two = try HandshakeOffer(version: 2, role: .responder, deviceName: "New", identityPublicKeyDER: Data([2]),
                                     ephemeralPublicKey: Data(repeating: 2, count: 32), nonce: Data(repeating: 2, count: 32), sessionCode: "")
        XCTAssertThrowsError(try HandshakeTranscript.validated(local: one, peer: two))
    }
}
