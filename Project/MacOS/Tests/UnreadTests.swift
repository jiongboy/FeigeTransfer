import XCTest
@testable import FeigeMac

@MainActor
final class UnreadTests: XCTestCase {
    func testArrivalsCountPersistAndClearOnRead() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        let peer = String(repeating: "A", count: 64)
        let group = TransferModel.groupConversationKey(UUID())
        model.noteIncoming(conversation: peer, title: "Mac", body: "hi", notify: true)
        model.noteIncoming(conversation: peer, title: "Mac", body: "hi", notify: true)
        model.noteIncoming(conversation: group, title: "Team", body: "file", notify: false)
        XCTAssertEqual(model.unreadCount(for: peer), 2)
        XCTAssertEqual(model.totalUnread, 3)

        let reloaded = TransferModel(supportDirectory: root)
        XCTAssertEqual(reloaded.unreadCount(for: peer), 2)
        reloaded.markConversationRead(peer)
        XCTAssertEqual(reloaded.totalUnread, 1)
        XCTAssertEqual(TransferModel(supportDirectory: root).totalUnread, 1)
    }

    func testHiddenConversationDoesNotCountTowardTotal() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = TransferModel(supportDirectory: root)
        let peer = String(repeating: "B", count: 64)
        model.noteIncoming(conversation: peer, title: "Mac", body: "hi", notify: false)
        model.hideConversation(peer)
        XCTAssertEqual(model.totalUnread, 0)
        model.restoreConversation(peer)
        XCTAssertEqual(model.totalUnread, 1)
    }
}
