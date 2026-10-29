import XCTest
@testable import FeigeMac

final class SyncFeatureTests: XCTestCase {
    private func temporaryRoot() -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    @MainActor
    func testDraftsPersistPerPeerAndSurviveRestart() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        model.setDraft("给 A 的草稿", for: "fingerprint-a")
        model.setDraft("给 B 的草稿", for: "fingerprint-b")
        model.setDraft("", for: "fingerprint-b")

        let restored = TransferModel(supportDirectory: root, makeUpdates: false)
        XCTAssertEqual(restored.drafts["fingerprint-a"], "给 A 的草稿")
        XCTAssertNil(restored.drafts["fingerprint-b"], "Empty drafts must not be persisted.")

        restored.clearDraft(for: "fingerprint-a")
        XCTAssertTrue(TransferModel(supportDirectory: root, makeUpdates: false).drafts.isEmpty)
    }

    @MainActor
    func testDraftForBlankSelectionIsIgnored() {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        model.setDraft("不应保存", for: "  ")
        XCTAssertTrue(model.drafts.isEmpty)
    }

    @MainActor
    func testOlderSettingsFileKeepsNewPreferenceDefaults() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let old = DeviceSettings(deviceName: "旧版 Mac", receiveDirectory: root.appendingPathComponent("Received"),
                                 firstRunComplete: true)
        // Encode with the pre-0.4.0 field set only: the new preferences must fall back to defaults.
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: encoder.encode(old)) as? [String: Any])
        for key in ["autoCheckUpdates", "notificationsEnabled", "backgroundTipSeen", "connectionTipSeen", "legacyReviewPending"] {
            object.removeValue(forKey: key)
        }
        let written = try JSONSerialization.data(withJSONObject: object)
        FileManager.default.createFile(atPath: root.appendingPathComponent("settings.json").path, contents: written)

        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        XCTAssertEqual(model.settings.deviceName, "旧版 Mac")
        XCTAssertTrue(model.settings.autoCheckUpdates)
        XCTAssertFalse(model.settings.notificationsEnabled)
        XCTAssertFalse(model.settings.connectionTipSeen)
    }

    @MainActor
    func testAutoCheckPreferenceIsPersistedAndSynchronous() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        try model.setAutoCheckUpdates(false)
        XCTAssertFalse(model.settings.autoCheckUpdates)
        XCTAssertFalse(TransferModel(supportDirectory: root, makeUpdates: false).settings.autoCheckUpdates)
    }

    @MainActor
    func testManualConnectionAcceptsPrivateLiteralsOnly() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        XCTAssertNoThrow(try model.manualDevice(host: "192.168.1.20", port: " 51820 "))
        XCTAssertNoThrow(try model.manualDevice(host: "fe80::1", port: "1"))
        for (host, port) in [("feige.local", "1"), ("8.8.8.8", "1"), ("999.1.1.1", "1"),
                             ("192.168.1.20", "0"), ("192.168.1.20", "70000"), ("192.168.1.20", "abc"),
                             ("", "1")] {
            XCTAssertThrowsError(try model.manualDevice(host: host, port: port), "\(host):\(port) must be rejected")
        }
    }

    @MainActor
    func testConnectionTipIsRecordedOnceAndSurvivesRestart() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        model.markConnectionTipSeen()
        XCTAssertTrue(model.settings.connectionTipSeen)
        XCTAssertTrue(TransferModel(supportDirectory: root, makeUpdates: false).settings.connectionTipSeen)
    }

    @MainActor
    func testBackgroundNoticeIsRecordedOnceAndDismissible() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        XCTAssertFalse(model.backgroundNoticePending)
        model.markBackgroundNotice()
        XCTAssertTrue(model.settings.backgroundTipSeen)
        XCTAssertTrue(model.backgroundNoticePending)
        model.dismissBackgroundNotice()
        XCTAssertFalse(model.backgroundNoticePending)
        model.markBackgroundNotice()
        XCTAssertFalse(model.backgroundNoticePending, "The notice must not be repeated after the first close.")
    }

    @MainActor
    func testClearHistoryKeepsConversationMessagesAndFiles() throws {
        let root = temporaryRoot()
        let receive = root.appendingPathComponent("Received")
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        let kept = receive.appendingPathComponent("kept.txt")
        try FileManager.default.createDirectory(at: receive, withIntermediateDirectories: true)
        try Data("keep".utf8).write(to: kept)
        model.addRecord(TransferRecord(fileName: "kept.txt", size: 4, direction: .received, savedPath: kept.path))
        model.messages = [ChatMessage(peerID: "fp", peerName: "对端", direction: .received, kind: .text,
                                      text: "记录仍在", status: .received)]
        try model.clearHistory()
        XCTAssertTrue(model.records.isEmpty)
        XCTAssertEqual(TransferModel(supportDirectory: root, makeUpdates: false).records.count, 0)
        XCTAssertEqual(model.messages.count, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: kept.path))
    }

    @MainActor
    func testFirstSuccessfulTransferShowsTheTipOnce() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root, makeUpdates: false)
        XCTAssertFalse(model.firstTransferNoticePending)
    }
}
