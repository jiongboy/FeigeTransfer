import XCTest
import CryptoKit
import FeigeIdentity

final class TransferCoordinatorTests: XCTestCase {
    func testNetworkManifestDecodingEnforcesAllLimits() throws {
        let valid: [String: Any] = ["transferID": UUID().uuidString, "fileName": "safe.txt",
                                    "size": 1, "sha256": Data(repeating: 0, count: 32).base64EncodedString()]
        for (field, invalid) in [("fileName", "../escape" as Any), ("size", 0),
                                 ("size", UInt64.max), ("sha256", "AA==")] {
            var input = valid
            input[field] = invalid
            XCTAssertThrowsError(try JSONDecoder().decode(FileManifest.self, from: JSONSerialization.data(withJSONObject: input)))
        }
        XCTAssertNoThrow(try JSONDecoder().decode(FileManifest.self, from: JSONSerialization.data(withJSONObject: valid)))
    }

    func testAcceptedTransferCompletesOnlyAfterSizeAndDigestMatch() throws {
        let payload = Data((0..<130_000).map { UInt8($0 % 251) })
        let manifest = try FileManifest(fileName: "report.bin", size: UInt64(payload.count), sha256: Data(SHA256.hash(data: payload)))
        let coordinator = TransferCoordinator()
        XCTAssertEqual(try coordinator.receiveManifest(manifest).kind, .manifest(manifest))
        XCTAssertThrowsError(try coordinator.receiveChunk(offset: 0, payload: Data([1])))

        _ = try coordinator.approve(transferID: manifest.transferID) { _ in XCTFail("approval has not occurred") }
        XCTAssertEqual(coordinator.state, .transferring)
    }

    func testStreamedAcceptedTransferWritesOnlyAfterApprovalAndVerifiesDigest() throws {
        let payload = Data((0..<130_000).map { UInt8($0 % 251) })
        let manifest = try FileManifest(fileName: "report.bin", size: UInt64(payload.count), sha256: Data(SHA256.hash(data: payload)))
        let coordinator = TransferCoordinator()
        var sinkData = Data()
        _ = try coordinator.receiveManifest(manifest)
        XCTAssertTrue(sinkData.isEmpty)
        _ = try coordinator.approve(transferID: manifest.transferID) { sinkData.append($0) }
        _ = try coordinator.receiveChunk(offset: 0, payload: Data(payload.prefix(65_000)))
        _ = try coordinator.receiveChunk(offset: 65_000, payload: Data(payload.suffix(from: 65_000)))
        XCTAssertEqual(sinkData, payload)
        XCTAssertEqual(try coordinator.finish().kind, .completed("report.bin"))
        XCTAssertEqual(coordinator.state, .completed)
    }

    func testRejectTraversalOversizeAndOutOfOrderNeverWrite() throws {
        XCTAssertThrowsError(try FileManifest(fileName: "../escape", size: 5, sha256: Data(repeating: 0, count: 32)))
        XCTAssertThrowsError(try FileManifest(fileName: "huge", size: UInt64.max, sha256: Data(repeating: 0, count: 32)))
        let manifest = try FileManifest(fileName: "safe.txt", size: 4, sha256: Data(repeating: 0, count: 32))
        let rejected = TransferCoordinator()
        _ = try rejected.receiveManifest(manifest)
        XCTAssertEqual(try rejected.reject(transferID: manifest.transferID).kind, .rejected)
        XCTAssertThrowsError(try rejected.receiveChunk(offset: 0, payload: Data([1])))

        let ordered = TransferCoordinator()
        var sinkData = Data()
        _ = try ordered.receiveManifest(manifest)
        _ = try ordered.approve(transferID: manifest.transferID) { sinkData.append($0) }
        XCTAssertThrowsError(try ordered.receiveChunk(offset: 1, payload: Data([1])))
        XCTAssertTrue(sinkData.isEmpty)
    }

    func testIncompleteOrCorruptTransferCannotComplete() throws {
        let incompleteManifest = try FileManifest(fileName: "short.dat", size: 4, sha256: Data(repeating: 0, count: 32))
        let incomplete = TransferCoordinator()
        _ = try incomplete.receiveManifest(incompleteManifest)
        _ = try incomplete.approve(transferID: incompleteManifest.transferID) { _ in }
        _ = try incomplete.receiveChunk(offset: 0, payload: Data([1, 2]))
        XCTAssertThrowsError(try incomplete.finish())
        XCTAssertEqual(incomplete.state, .failed)

        let corruptManifest = try FileManifest(fileName: "bad.dat", size: 4, sha256: Data(repeating: 0, count: 32))
        let corrupt = TransferCoordinator()
        _ = try corrupt.receiveManifest(corruptManifest)
        _ = try corrupt.approve(transferID: corruptManifest.transferID) { _ in }
        _ = try corrupt.receiveChunk(offset: 0, payload: Data([1, 2, 3, 4]))
        XCTAssertThrowsError(try corrupt.finish())
        XCTAssertEqual(corrupt.state, .failed)
    }
}
