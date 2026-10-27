import XCTest
import Network
@testable import FeigeMac

final class HistoryTests: XCTestCase {
    @MainActor
    func testSuccessfulRecordPersistsAndOldRecordsRemainReadable() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let old = TransferRecord(fileName: "old.txt", size: 4, direction: .received)
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(old)) as? [String: Any])
        json.removeValue(forKey: "peerName")
        json.removeValue(forKey: "savedPath")
        try JSONSerialization.data(withJSONObject: [json]).write(to: root.appendingPathComponent("history.json"))
        let model = TransferModel(supportDirectory: root)
        XCTAssertEqual(model.records.count, 1)
        XCTAssertNil(model.records[0].peerName)
        model.addRecord(TransferRecord(fileName: "new.txt", size: 5, direction: .sent, peerName: "书房 Mac"))
        let reloaded = TransferModel(supportDirectory: root)
        XCTAssertEqual(reloaded.records.map(\.fileName), ["new.txt", "old.txt"])
        XCTAssertEqual(reloaded.records.first?.peerName, "书房 Mac")
    }
    @MainActor
    func testPendingMessagesRecoverAsFailedAndSameNamedPeersStaySeparate() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let messages = [
            ChatMessage(peerID: "fingerprint-a", peerName: "相同名称", direction: .sent, kind: .text,
                        text: "未完成文字", status: .sending),
            ChatMessage(peerID: "fingerprint-b", peerName: "相同名称", direction: .received, kind: .image,
                        fileName: "image.png", size: 16, localPath: "/tmp/image.png", status: .received)
        ]
        try JSONEncoder().encode(messages).write(to: root.appendingPathComponent("messages.json"))
        let model = TransferModel(supportDirectory: root)
        XCTAssertEqual(model.messages.first?.status, .failed)
        XCTAssertEqual(model.messages.last?.status, .received)
        XCTAssertEqual(Set(model.chatPeers.map(\.id)), Set(["fingerprint-a", "fingerprint-b"]))
        XCTAssertFalse(model.sendText("离线不发送"))
        XCTAssertEqual(model.messages.count, 2)
    }

    @MainActor
    func testConversationHideRestoreAndDeletePersistWithoutTouchingTransferHistory() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let values = [
            ChatMessage(peerID: "peer-a", peerName: "书房 Mac", direction: .received, kind: .text,
                        text: "保留的会话", status: .received),
            ChatMessage(peerID: "peer-b", peerName: "工作电脑", direction: .received, kind: .text,
                        text: "待删除的会话", status: .received)
        ]
        try JSONEncoder().encode(values).write(to: root.appendingPathComponent("messages.json"))
        let transfer = TransferRecord(fileName: "独立记录.pdf", size: 12, direction: .received, savedPath: "/tmp/独立记录.pdf")
        try JSONEncoder().encode([transfer]).write(to: root.appendingPathComponent("history.json"))

        let model = TransferModel(supportDirectory: root)
        model.hideConversation("peer-a")
        XCTAssertEqual(model.chatPeers.map(\.id), ["peer-b"])
        XCTAssertEqual(model.hiddenConversations.map(\.id), ["peer-a"])

        let reloaded = TransferModel(supportDirectory: root)
        XCTAssertEqual(reloaded.hiddenConversationIDs, ["peer-a"])
        reloaded.restoreConversation("peer-a")
        XCTAssertEqual(Set(reloaded.chatPeers.map(\.id)), Set(["peer-a", "peer-b"]))
        try reloaded.deleteConversation("peer-b")
        XCTAssertEqual(reloaded.messages.map(\.peerID), ["peer-a"])
        XCTAssertEqual(reloaded.records.map(\.fileName), ["独立记录.pdf"])

        let finalReload = TransferModel(supportDirectory: root)
        XCTAssertEqual(finalReload.messages.map(\.peerID), ["peer-a"])
        XCTAssertEqual(finalReload.records.map(\.fileName), ["独立记录.pdf"])
    }

    @MainActor
    func testCannotDeleteConnectedOrPendingConversation() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        model.connectedPeerID = "peer-a"
        model.isConnected = true
        XCTAssertFalse(model.canDeleteConversation("peer-a"))
        XCTAssertThrowsError(try model.deleteConversation("peer-a"))
        model.isConnected = false
        model.connectedPeerID = ""
        model.messages = [ChatMessage(peerID: "peer-a", peerName: "书房 Mac", direction: .sent, kind: .file,
                                      fileName: "large.zip", size: 10, status: .sending)]
        XCTAssertFalse(model.canDeleteConversation("peer-a"))
    }

    @MainActor
    func testDiscoveredRenameIsReflectedInExistingConversation() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let id = "AABB CCDD"
        let message = ChatMessage(peerID: id, peerName: "旧名称", direction: .received, kind: .text,
                                  text: "你好", status: .received)
        try JSONEncoder().encode([message]).write(to: root.appendingPathComponent("messages.json"))
        let model = TransferModel(supportDirectory: root)
        let endpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: 54321)
        model.nearbyDevices = [LANDevice(serviceName: "Feige-AABBCCDD", displayName: "新名称", endpoint: endpoint)]
        XCTAssertEqual(model.chatPeers.first?.name, "新名称")
    }

    @MainActor
    func testCorruptConversationFileIsPreservedInsteadOfOverwritten() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let original = Data("unknown conversation data".utf8)
        let file = root.appendingPathComponent("messages.json")
        try original.write(to: file)
        let model = TransferModel(supportDirectory: root)
        model.hideConversation("peer-a")
        XCTAssertEqual(try Data(contentsOf: file), original)
        XCTAssertTrue(model.errorMessage?.contains("原文件") == true)
    }

}
