import Combine
import SwiftUI

/// First-run guide (two pages) and the preferences window, matching the Windows settings cards:
/// device and connection info, receive folder, version and updates, notifications, local records,
/// hidden conversations, identity, application run and copyright.
struct SetupView: View {
    @EnvironmentObject private var model: TransferModel
    @Environment(\.dismiss) private var dismiss
    let isFirstRun: Bool
    @State private var deviceName = ""
    @State private var saveError: String?
    @State private var firstRunStep = 0
    @State private var updateRevision = 0
    @FocusState private var isDeviceNameFocused: Bool
    @State private var receiveDirectory = URL(fileURLWithPath: NSHomeDirectory()).appendPathComponentCompat("Downloads/Feige Received")

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header
                Divider()
                if isFirstRun {
                    steps
                }
                if !isFirstRun || firstRunStep == 0 {
                    deviceCard
                    folderCard
                }
                if isFirstRun && firstRunStep == 1 {
                    identityCard(showsPreparation: true)
                }
                if !isFirstRun {
                    connectionCard
                    updatesCard.id(updateRevision)
                    notificationsCard
                    recordsCard
                    hiddenConversationsCard
                    identityCard(showsPreparation: false)
                    runCard
                    footer
                }
                if !isFirstRun || firstRunStep == 1 {
                    footerButtons
                }
            }
            .padding(24)
        }
        .frame(width: isFirstRun ? 560 : 620, height: isFirstRun ? 620 : 760)
        .onAppear {
            deviceName = model.settings.deviceName
            receiveDirectory = model.settings.receiveDirectory
            firstRunStep = model.settings.setupStep ?? 0
            isDeviceNameFocused = isFirstRun && firstRunStep == 0
            Task { await model.refreshNotifierStatus() }
        }
        .alert("无法保存设置", isPresented: Binding(get: { saveError != nil }, set: { if !$0 { saveError = nil } })) {
            Button("好", role: .cancel) { saveError = nil }
        } message: { Text(saveError ?? "") }
        .onReceive(model.updates?.objectWillChange ?? ObservableObjectPublisher()) { _ in
            updateRevision &+= 1
        }
        .onReceive(NotificationCenter.default.publisher(for: .feigeOpenUpdates)) { _ in
            if model.updates?.canCheck == true { model.updates?.check() }
        }
    }

    // MARK: header

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "paperplane.fill")
                .font(.system(size: 19, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 44, height: 44)
                .background(LinearGradient(colors: [.indigo, .cyan], startPoint: .topLeading, endPoint: .bottomTrailing), in: RoundedRectangle(cornerRadius: 13))
            VStack(alignment: .leading, spacing: 3) {
                Text(isFirstRun ? (firstRunStep == 0 ? "欢迎使用飞鸽快传" : "准备安全连接") : "偏好设置")
                    .font(.system(size: 20, weight: .bold))
                Text(isFirstRun ? (firstRunStep == 0 ? "先设置本机名称和文件接收位置。" : "确认身份状态后即可开始连接附近设备。")
                     : "管理本机设备、接收位置和使用偏好。")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Spacer()
        }
    }

    private var steps: some View {
        HStack(spacing: 8) {
            Label("1 本机设置", systemImage: firstRunStep == 0 ? "1.circle.fill" : "checkmark.circle.fill")
            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary)
            Label("2 准备连接", systemImage: firstRunStep == 1 ? "2.circle.fill" : "2.circle")
        }.font(.caption).foregroundStyle(Color.accentColor)
    }

    // MARK: cards

    private var deviceCard: some View {
        Card(title: "设备名称", subtitle: "其他设备会通过这个名称找到你。") {
            HStack(spacing: 8) {
                TextField("例如：书房 Mac", text: $deviceName)
                    .textFieldStyle(.roundedBorder)
                    .frame(minHeight: 28)
                    .focused($isDeviceNameFocused)
                    .accessibilityLabel("设备名称")
                Button("保存") { saveName() }.buttonStyle(.borderedProminent)
                    .disabled(!isFirstRun && deviceName.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            Text("1–48 个字符，最长 128 字节，不能包含换行等控制字符。改名不会改变设备身份或合并历史。")
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
    }

    private var folderCard: some View {
        Card(title: "接收文件夹", subtitle: "收到的文件将保存在这里，不会覆盖同名文件。") {
            HStack(spacing: 8) {
                Image(systemName: "folder").foregroundStyle(.secondary)
                Text(receiveDirectory.path)
                    .font(.system(size: 11, design: .monospaced))
                    .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                Spacer()
                if !isFirstRun { Button("更改…") { chooseFolder() }.disabled(false) }
                else { Button("选择…") { chooseFolder() } }
            }
            .padding(8)
            .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 8))
            Text("保存或更改接收目录时会清理本应用遗留的未完成暂存文件，不触碰普通文件或符号链接。")
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
    }

    private var connectionCard: some View {
        Card(title: "本机连接信息", subtitle: "与对方处于同一局域网；连接请求需对方同意。") {
            Text(connectionStatus)
                .font(.system(size: 13, weight: .semibold))
            ForEach(connectionLines, id: \.self) { line in
                Text(line).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                    .foregroundStyle(.secondary)
            }
            Text("手动连接时，可将以上地址和端口提供给对方。")
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
    }

    private var connectionStatus: String {
        if model.isIdentityReady == false { return model.isPreparingIdentity ? "正在准备安全连接…" : "尚未就绪" }
        if model.listenerPort == nil { return model.discoveryState }
        return model.discoveryState.localizedCaseInsensitiveContains("失败") || model.discoveryState.localizedCaseInsensitiveContains("不可用")
            ? "直连已就绪，自动发现暂不可用" : "局域网已就绪"
    }

    private var connectionLines: [String] {
        guard let port = model.listenerPort else { return ["安全连接尚未准备好，就绪后会显示本机地址和端口。"] }
        return ["\(model.localAddress) · 端口 \(port)"]
    }

    private var updatesCard: some View {
        Card(title: "版本与更新", subtitle: nil) {
            if let updates = model.updates {
                Text(updates.versionText).font(.system(size: 11)).textSelection(.enabled)
                Text(updates.status).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 8) {
                    Button("检查更新") { updates.check() }.disabled(!updates.canCheck)
                    if updates.isChecking { ProgressView().controlSize(.small) }
                    if updates.canDownload {
                        Button("下载更新") { updates.download() }
                    }
                    if updates.canInstall {
                        Button("安装并重启") { Task { await updates.installWhenReady(window: NSApplication.shared.keyWindow) { [model] in model.hasActiveWork } } }
                            .buttonStyle(.borderedProminent)
                    }
                    if updates.isWaitingToInstall { Button("取消等待安装") { updates.cancel() } }
                    if updates.isDownloading { Button("取消下载") { updates.cancel() } }
                }
                if let candidate = updates.candidate {
                    if updates.isDownloading {
                        ProgressView(value: Double(updates.downloaded), total: Double(max(1, candidate.size)))
                        Text("\(SizeText.format(updates.downloaded)) / \(SizeText.format(candidate.size))")
                            .font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    Text(candidate.releaseText).font(.system(size: 11))
                        .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    HStack(spacing: 8) {
                        Button("查看完整更新说明") { NSWorkspace.shared.open(candidate.notesUrl) }
                        Button("跳过此版本") { updates.skip() }
                            .disabled(updates.isDownloading || updates.isWaitingToInstall)
                    }
                }
                Toggle("自动检查更新", isOn: autoCheckBinding)
                Text("初始化完成后每 24 小时最多联网检查一次；失败不影响局域网收发。下载和安装分别确认。")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            } else {
                Text("更新来源尚未配置，暂不联网检查。").font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
    }

    private var notificationsCard: some View {
        Card(title: "通知（可选）", subtitle: nil) {
            Text(model.notifierStatus.isEmpty ? "尚未启用通知，收发状态会显示在窗口内。" : model.notifierStatus)
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                if model.settings.notificationsEnabled {
                    Button("关闭通知") { try? model.disableNotifications(); Task { await model.refreshNotifierStatus() } }
                    Button("发送测试通知") { Task { await model.sendTestNotification() } }
                } else {
                    Button("启用通知") { enableNotifications() }.disabled(model.settings.notificationsEnabled)
                }
                Button("打开系统通知设置") { TransferNotifier.openSystemSettings() }
            }
        }
    }

    private var recordsCard: some View {
        Card(title: "本机记录", subtitle: nil) {
            Text(recordSummary).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Button("清空传输记录") { clearHistory() }.disabled(model.records.isEmpty)
                Button("打开数据目录") { revealAppData() }
            }
        }
    }

    private var recordSummary: String {
        let count = model.records.count + model.messages.count
        return count == 0 ? "还没有记录。" : "已保存 \(count) 条记录（传输记录 \(model.records.count) 条、会话消息 \(model.messages.count) 条），保留最近 1000 条已结束消息。"
    }

    private var hiddenConversationsCard: some View {
        Card(title: "隐藏的会话", subtitle: "隐藏仅从会话列表移除，聊天记录仍保留；收到新消息或重新连接时会再次显示。") {
            if model.hiddenConversations.isEmpty {
                Text("没有隐藏的会话。").font(.system(size: 11)).foregroundStyle(.tertiary)
            } else {
                ForEach(model.hiddenConversations) { peer in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(peer.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                            Text(peer.preview).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer()
                        Button("恢复") { model.restoreConversation(peer.id) }.controlSize(.small)
                    }
                }
                Button("全部恢复") { model.restoreAllConversations() }.controlSize(.small)
            }
        }
    }

    private func identityCard(showsPreparation: Bool) -> some View {
        Card(title: "设备身份与连接安全", subtitle: "身份保存在本机钥匙串，私钥不会发送给其他设备。") {
            Text(model.identityFingerprint)
                .font(.system(size: 10, design: .monospaced))
                .textSelection(.enabled)
                .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 8))
            if showsPreparation {
                HStack(spacing: 8) {
                    if model.isPreparingIdentity { ProgressView().controlSize(.small) }
                    else { Image(systemName: model.isIdentityReady ? "checkmark.circle.fill" : "exclamationmark.circle.fill") }
                    Text(model.isPreparingIdentity ? "正在验证本机身份…" : model.isIdentityReady ? "本机身份已准备就绪" : "身份尚未准备，请检查系统授权后重试。")
                        .font(.system(size: 12)).foregroundStyle(model.isIdentityReady ? Color.secondary : Color.orange)
                    Spacer()
                    if !model.isPreparingIdentity && !model.isIdentityReady {
                        Button("重试") { Task { await model.prepareIdentityForSetup() } }
                    }
                }
                if let error = model.identityPreparationError {
                    Text(error).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var runCard: some View {
        Card(title: "应用运行", subtitle: "关闭窗口后仍在后台运行，可从 Dock 重新打开。退出应用会断开当前连接并结束收发。") {
            Button {
                NSApplication.shared.terminate(nil)
            } label: {
                Label("退出应用", systemImage: "power")
            }
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("飞鸽快传 for macOS").font(.system(size: 13, weight: .semibold))
            Text("同意一次连接，轻松互传文字、图片和文件。").font(.system(size: 12)).foregroundStyle(.secondary)
            Text("© Songli").font(.system(size: 12)).foregroundStyle(.tertiary).padding(.top, 4)
        }.padding(.bottom, 8)
    }

    private var footerButtons: some View {
        HStack {
            Spacer()
            if !isFirstRun { Button("取消") { dismiss() }.keyboardShortcut(.cancelAction) }
            if isFirstRun {
                Button("稍后设置") { model.deferFirstRunSetup(); dismiss() }
                if firstRunStep == 1 {
                    Button("上一步") {
                        do {
                            try model.saveSetupDraft(deviceName: deviceName, receiveDirectory: receiveDirectory, nextStep: 0)
                            firstRunStep = 0
                        } catch { saveError = error.localizedDescription }
                    }
                }
            }
            Button(primaryTitle, action: handlePrimaryAction)
                .buttonStyle(.borderedProminent)
                .disabled(deviceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                          (isFirstRun && firstRunStep == 1 && !model.isIdentityReady))
        }
    }

    // MARK: bindings and actions

    private var autoCheckBinding: Binding<Bool> {
        Binding(get: { model.settings.autoCheckUpdates }, set: { value in
            do { try model.setAutoCheckUpdates(value) }
            catch { saveError = "保存更新偏好失败：\(error.localizedDescription)" }
        })
    }

    private var primaryTitle: String {
        if !isFirstRun { return "完成" }
        return firstRunStep == 0 ? "继续" : "完成设置并开始"
    }

    private func handlePrimaryAction() {
        if !isFirstRun { dismiss(); return }
        do {
            if firstRunStep == 0 {
                try model.saveSetupDraft(deviceName: deviceName, receiveDirectory: receiveDirectory, nextStep: 1)
                firstRunStep = 1
                Task { await model.prepareIdentityForSetup() }
            } else {
                try model.saveSettings(deviceName: deviceName, receiveDirectory: receiveDirectory)
                Task { await model.start() }
                dismiss()
            }
        } catch {
            saveError = error.localizedDescription
        }
    }

    private func saveName() {
        do {
            try model.saveSettings(deviceName: deviceName, receiveDirectory: receiveDirectory)
            saveError = nil
        } catch {
            saveError = error.localizedDescription
        }
    }

    private func chooseFolder() {
        if let selected = model.chooseReceiveFolder() {
            receiveDirectory = selected
            if !isFirstRun {
                do { try model.saveSettings(deviceName: deviceName, receiveDirectory: selected) }
                catch { saveError = error.localizedDescription }
            }
        }
    }

    private func enableNotifications() {
        Task {
            do {
                if try await model.enableNotifications() == false {
                    saveError = "系统未授权通知，可在系统通知设置中允许飞鸽快传。"
                }
            } catch { saveError = "保存通知偏好失败：\(error.localizedDescription)" }
        }
    }

    private func clearHistory() {
        do { try model.clearHistory(); saveError = nil }
        catch { saveError = error.localizedDescription }
    }

    private func revealAppData() {
        let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FeigeTransfer", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSWorkspace.shared.activateFileViewerSelecting([directory])
    }
}

/// One settings card, matching the Windows `FeigeSectionCardStyle` grouping.
struct Card<Content: View>: View {
    let title: String
    var subtitle: String?
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.system(size: 14, weight: .semibold))
            if let subtitle {
                Text(subtitle).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            content
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
    }
}

private extension URL {
    func appendPathComponentCompat(_ component: String) -> URL {
        component.split(separator: "/").reduce(self) { $0.appendingPathComponent(String($1)) }
    }
}
