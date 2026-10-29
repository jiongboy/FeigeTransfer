import AppKit
import Foundation
import UserNotifications

/// System notifications for arrivals, completions and progress.
///
/// Notifications are a nicety, never a dependency: when macOS refuses permission, `isAvailable`
/// stays false, the status line explains why and the in-window status keeps working.
@MainActor
final class TransferNotifier {
    enum Permission { case undetermined, denied, authorized }

    private(set) var permission: Permission = .undetermined
    private(set) var needsSettingsLink = false
    private(set) var lastError: String?

    var isAvailable: Bool { permission == .authorized }

    var status: String {
        if let lastError { return lastError }
        switch permission {
        case .authorized: return "通知已启用：收发完成时会显示系统提示。"
        case .denied: return needsSettingsLink
            ? "系统已关闭飞鸽快传的通知，可在系统设置中重新允许。"
            : "系统通知尚未授权，收发状态会显示在窗口内。"
        case .undetermined: return "尚未启用通知，收发状态会显示在窗口内。"
        }
    }

    /// UNUserNotificationCenter requires an application bundle; test processes and plain command-line
    /// binaries run without one and raise an ObjC exception, so notifications simply stay unavailable.
    static var isSupported: Bool {
        // XCTest runs inside .../usr/bin with no application identifier; the packaged app has both.
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.path.hasSuffix(".app")
    }

    func refreshStatus() async {
        guard Self.isSupported else {
            permission = .undetermined
            needsSettingsLink = false
            lastError = "当前运行环境没有应用包标识，系统通知不可用。"
            return
        }
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        apply(settings)
    }

    /// Requests permission. Only call from an explicit user action (enable button or invitation).
    @discardableResult
    func requestAuthorization() async -> Bool {
        guard Self.isSupported else {
            lastError = "当前运行环境没有应用包标识，系统通知不可用。"
            return false
        }
        do {
            _ = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
            lastError = nil
        } catch {
            lastError = "无法请求系统通知权限：\(error.localizedDescription)"
        }
        await refreshStatus()
        return isAvailable
    }

    func show(title: String, body: String) {
        guard Self.isSupported, isAvailable, !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { [weak self] error in
            guard let error else { return }
            Task { @MainActor in self?.lastError = "发送系统通知失败：\(error.localizedDescription)" }
        }
    }

    static func openSystemSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") else { return }
        NSWorkspace.shared.open(url)
    }

    private func apply(_ settings: UNNotificationSettings) {
        switch settings.authorizationStatus {
        case .authorized, .provisional, .ephemeral:
            permission = .authorized
            needsSettingsLink = false
            lastError = nil
        case .denied:
            permission = .denied
            needsSettingsLink = true
        default:
            permission = .undetermined
            needsSettingsLink = false
        }
    }
}
