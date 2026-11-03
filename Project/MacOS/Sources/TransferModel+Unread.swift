import AppKit
import Foundation

/// Local unread counts. Nothing is sent to the peer: there are no read receipts.
extension TransferModel {
    static func groupConversationKey(_ id: UUID) -> String { "group:\(id.uuidString)" }

    /// Unread total shown on the Dock and menu bar; hidden conversations do not count.
    var totalUnread: Int {
        unreadCounts.filter { !hiddenConversationIDs.contains($0.key) }.values.reduce(0, +)
    }

    func unreadCount(for key: String) -> Int { hiddenConversationIDs.contains(key) ? 0 : unreadCounts[key] ?? 0 }

    /// True while the user can see the main window; tests and command-line runs have no app instance.
    var isAppActive: Bool { TransferNotifier.isSupported && NSApplication.shared.isActive }

    func setVisibleConversation(_ key: String?) {
        visibleConversationKey = key
        if let key, isAppActive { markConversationRead(key) }
    }

    /// Call when the application becomes active again.
    func applicationDidBecomeActive() {
        if let visibleConversationKey { markConversationRead(visibleConversationKey) }
    }

    func markConversationRead(_ key: String) {
        guard unreadCounts[key] != nil else { return }
        unreadCounts[key] = nil
        saveUnreadCounts()
    }

    /// Counts one arrival unless the user is looking at that conversation, and optionally shows a notification.
    func noteIncoming(conversation key: String, title: String, body: String, notify: Bool) {
        let active = isAppActive
        if active && visibleConversationKey == key { return }
        unreadCounts[key, default: 0] += 1
        saveUnreadCounts()
        if notify && !active && settings.notificationsEnabled {
            notifier.show(title: title, body: String(body.prefix(120)))
        }
    }

    func loadUnreadCounts() {
        guard let data = try? Data(contentsOf: unreadURL),
              let decoded = try? JSONDecoder().decode([String: Int].self, from: data) else { return }
        unreadCounts = decoded.filter { $0.value > 0 }
        updateDockBadge()
    }

    func saveUnreadCounts() {
        updateDockBadge()
        do { try JSONEncoder().encode(unreadCounts).write(to: unreadURL, options: .atomic) }
        catch { AppLogFile.write("unread save: \(type(of: error))", in: unreadURL.deletingLastPathComponent()) }
    }

    func updateDockBadge() {
        guard TransferNotifier.isSupported else { return }
        let total = totalUnread
        NSApplication.shared.dockTile.badgeLabel = total > 0 ? (total > 99 ? "99+" : "\(total)") : nil
    }
}
