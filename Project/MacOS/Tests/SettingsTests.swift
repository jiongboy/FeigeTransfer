import XCTest
@testable import FeigeMac

final class SettingsTests: XCTestCase {
    @MainActor
    func testRenamedDeviceIsPublishedAndRestoredAfterReload() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        let receiveDirectory = root.appendingPathComponent("Received")
        // An unrelated earlier error must not prevent a successful settings save.
        model.errorMessage = "之前的连接错误"

        try model.saveSettings(deviceName: "  书房 Mac  ", receiveDirectory: receiveDirectory)
        XCTAssertEqual(model.settings.deviceName, "书房 Mac")
        XCTAssertTrue(model.settings.firstRunComplete)

        let restored = TransferModel(supportDirectory: root)
        XCTAssertEqual(restored.settings.deviceName, "书房 Mac")
        XCTAssertEqual(restored.settings.receiveDirectory, receiveDirectory)
        try restored.saveSettings(deviceName: "工作 Mac", receiveDirectory: receiveDirectory)
        XCTAssertEqual(TransferModel(supportDirectory: root).settings.deviceName, "工作 Mac")
    }

    @MainActor
    func testSavingSettingsCleansOrphanedTransferFilesOnly() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let receiveDirectory = root.appendingPathComponent("Received")
        try FileManager.default.createDirectory(at: receiveDirectory, withIntermediateDirectories: true)
        let orphan = receiveDirectory.appendingPathComponent(".feige-\(UUID().uuidString).part")
        try Data("partial".utf8).write(to: orphan)
        let ordinary = receiveDirectory.appendingPathComponent("notes.part")
        try Data("keep".utf8).write(to: ordinary)

        let model = TransferModel(supportDirectory: root.appendingPathComponent("Support"))
        try model.saveSettings(deviceName: "书房 Mac", receiveDirectory: receiveDirectory)

        XCTAssertFalse(FileManager.default.fileExists(atPath: orphan.path))
        XCTAssertEqual(try Data(contentsOf: ordinary), Data("keep".utf8))
    }

    @MainActor
    func testInvalidNamesLeaveSavedNameUnchanged() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        try model.saveSettings(deviceName: "原名称", receiveDirectory: root)

        for invalidName in [" \n ", String(repeating: "名", count: 49)] {
            XCTAssertThrowsError(try model.saveSettings(deviceName: invalidName, receiveDirectory: root))
            XCTAssertEqual(model.settings.deviceName, "原名称")
            XCTAssertEqual(TransferModel(supportDirectory: root).settings.deviceName, "原名称")
        }
        let longestName = String(repeating: "a", count: 48)
        try model.saveSettings(deviceName: longestName, receiveDirectory: root)
        XCTAssertEqual(TransferModel(supportDirectory: root).settings.deviceName, longestName)
    }

    @MainActor
    func testFailedWriteDoesNotPublishUnsavedNameAndCanBeRetried() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        try model.saveSettings(deviceName: "原名称", receiveDirectory: root)
        let settingsURL = root.appendingPathComponent("settings.json")
        try FileManager.default.removeItem(at: settingsURL)
        try FileManager.default.createDirectory(at: settingsURL, withIntermediateDirectories: false)

        XCTAssertThrowsError(try model.saveSettings(deviceName: "新名称", receiveDirectory: root))
        XCTAssertEqual(model.settings.deviceName, "原名称")

        try FileManager.default.removeItem(at: settingsURL)
        try model.saveSettings(deviceName: "新名称", receiveDirectory: root)
        XCTAssertEqual(TransferModel(supportDirectory: root).settings.deviceName, "新名称")
    }

    @MainActor
    func testFirstRunStepIsSavedAndResumedUntilSetupCompletes() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        let receiveDirectory = root.appendingPathComponent("Received")
        try model.saveSetupDraft(deviceName: "书房 Mac", receiveDirectory: receiveDirectory, nextStep: 1)
        XCTAssertFalse(model.settings.firstRunComplete)
        XCTAssertEqual(model.settings.setupStep, 1)

        let resumed = TransferModel(supportDirectory: root)
        XCTAssertEqual(resumed.settings.setupStep, 1)
        try resumed.saveSettings(deviceName: resumed.settings.deviceName, receiveDirectory: resumed.settings.receiveDirectory)
        XCTAssertTrue(resumed.settings.firstRunComplete)
        XCTAssertNil(resumed.settings.setupStep)
        XCTAssertNil(TransferModel(supportDirectory: root).settings.setupStep)
    }
}
