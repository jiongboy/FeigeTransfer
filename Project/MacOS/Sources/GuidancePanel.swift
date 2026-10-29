import Combine
import SwiftUI

/// Closable in-window tips mirroring the Windows guidance bars: authorization, legacy data,
/// first connection, first transfer, notification invitation, background run, updates and errors.
struct GuidancePanel: View {
    @EnvironmentObject private var model: TransferModel
    @State private var error: String?
    @State private var updateRevision = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            leadingBars
            if let updates = model.updates {
                updatesView(updates).id(updateRevision)
            }
            if let error {
                Bar(symbol: "exclamationmark.circle", tint: .red, title: "操作未完成", message: error,
                    dismiss: { self.error = nil })
            }
        }
        .padding(.horizontal, 18)
        .padding(.top, 6)
        .onReceive(model.updates?.objectWillChange ?? ObservableObjectPublisher()) { _ in
            updateRevision &+= 1
        }
    }

    @ViewBuilder private var leadingBars: some View {
        authorizationBar
        legacyBar
        connectionBar
        transferBar
        notificationBar
        backgroundBar
    }

    @ViewBuilder private var authorizationBar: some View {
        if !model.isIdentityReady && model.settings.firstRunComplete {
            Bar(symbol: "lock.shield", tint: .orange,
                title: model.isPreparingIdentity ? "正在准备安全连接" : "请先完成系统授权",
                message: model.identityPreparationError ?? "如出现 macOS 钥匙串弹窗，请输入登录密码并选择“始终允许”，避免后续连接重复询问。",
                actionTitle: model.isPreparingIdentity ? nil : "重新授权",
                action: { Task { await model.start() } })
        }
    }

    @ViewBuilder private var legacyBar: some View {
        if model.settings.legacyReviewPending {
            Bar(symbol: "folder.badge.questionmark", tint: .blue, title: "原有资料已保留",
                message: "可在偏好设置中核对设备名称、接收位置和使用偏好。",
                actionTitle: "核对设置",
                action: { model.dismissLegacyReviewQuietly(); NotificationCenter.default.post(name: .feigeOpenSettings, object: nil) },
                dismiss: { model.dismissLegacyReviewQuietly() })
        }
    }

    @ViewBuilder private var connectionBar: some View {
        if model.isConnected && !model.settings.connectionTipSeen {
            Bar(symbol: "link", tint: .accentColor, title: "已连接",
                message: "可以发送文字，或选择图片和文件。断开后再次连接需要对方同意。Enter 发送，Shift+Enter 换行。",
                dismiss: { model.markConnectionTipSeen() })
        }
    }

    @ViewBuilder private var transferBar: some View {
        if model.firstTransferNoticePending {
            Bar(symbol: "checkmark.circle", tint: .green, title: "收发成功",
                message: "消息已由对方确认送达，或文件已完整保存。",
                dismiss: { model.dismissTransferNotice() })
        }
    }

    @ViewBuilder private var notificationBar: some View {
        if model.notificationInvitationPending {
            Bar(symbol: "bell", tint: .accentColor, title: "需要收发完成通知吗？",
                message: "启用后可收到系统提示；跳过也不影响收发。",
                actionTitle: "启用通知",
                action: { enableNotifications() },
                dismiss: { model.dismissNotificationInvitation() })
        }
    }

    @ViewBuilder private var backgroundBar: some View {
        if model.backgroundNoticePending {
            Bar(symbol: "moon.zzz", tint: .secondary, title: "应用仍在后台运行",
                message: "关闭窗口后仍可在后台收发；从 Dock 图标或“文件 → 显示主窗口”重开，停止运行请按 ⌘Q。",
                dismiss: { model.dismissBackgroundNotice() })
        }
    }

    @ViewBuilder private func updatesView(_ updates: UpdateController) -> some View {
        if updates.bannerVisible || updates.isBusy {
            Bar(symbol: "arrow.down.circle", tint: .accentColor,
                title: updates.isBusy ? "正在更新" : "发现新版本",
                message: updateMessage(updates),
                actionTitle: updates.isDownloading ? "下载中…" : updates.isBusy ? "安装中…" : "更新",
                action: { NotificationCenter.default.post(name: .feigeOpenUpdates, object: nil) },
                actionEnabled: !updates.isBusy,
                secondaryTitle: "稍后",
                secondary: { updates.later() })
        }
        if let notice = updates.installNotice {
            Bar(symbol: updates.installNoticeIsSuccess ? "checkmark.seal" : "exclamationmark.triangle",
                tint: updates.installNoticeIsSuccess ? .green : .orange,
                title: updates.installNoticeIsSuccess ? "更新完成" : "更新未确认",
                message: notice,
                dismiss: { updates.dismissInstallNotice() })
        }
    }

    private func updateMessage(_ updates: UpdateController) -> String {
        if updates.isDownloading, let candidate = updates.candidate {
            return "\(SizeText.format(updates.downloaded)) / \(SizeText.format(candidate.size))"
        }
        if updates.isWaitingToInstall { return updates.status }
        if let candidate = updates.candidate { return "版本 \(candidate.version) 可用，点击“更新”查看并安装。" }
        return updates.status
    }

    private func enableNotifications() {
        Task {
            do {
                if try await model.enableNotifications() == false {
                    error = "系统未授权通知，可在偏好设置中重新启用。"
                }
            } catch {
                self.error = "保存通知偏好失败：\(error.localizedDescription)"
            }
        }
    }
}

/// One closable tip row.
struct Bar: View {
    let symbol: String
    var tint: Color = .accentColor
    let title: String
    let message: String
    var actionTitle: String?
    var action: (() -> Void)?
    var actionEnabled: Bool = true
    var secondaryTitle: String?
    var secondary: (() -> Void)?
    var dismiss: (() -> Void)?

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: symbol).foregroundStyle(tint).frame(width: 18).padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 12, weight: .semibold))
                Text(message).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            if let actionTitle, let action {
                Button(actionTitle, action: action).controlSize(.small).disabled(!actionEnabled)
            }
            if let secondaryTitle, let secondary {
                Button(secondaryTitle, action: secondary).controlSize(.small)
            }
            if let dismiss {
                Button(action: dismiss) { Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)) }
                    .buttonStyle(.borderless)
                    .help("关闭提示")
                    .accessibilityLabel("关闭提示")
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 8))
    }
}

extension Notification.Name {
    static let feigeOpenSettings = Notification.Name("feige.openSettings")
    static let feigeOpenUpdates = Notification.Name("feige.openUpdates")
}
