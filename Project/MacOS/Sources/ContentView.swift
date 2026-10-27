import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject private var model: TransferModel
    @Environment(\.openWindow) private var openWindow
    @Environment(\.colorScheme) private var colorScheme
    @State private var selectedPeerID: String?
    @State private var selectedDeviceID: String?
    @State private var drafts: [String: String] = [:]
    @State private var showManualConnection = false
    @State private var pendingDevice: LANDevice?
    @State private var showHistory = false
    @State private var dropTargeted = false
    @State private var pendingDeletePeerID: String?
    @State private var showConnectionHelp = false

    private var currentPeerID: String { selectedDeviceID == nil ? (selectedPeerID ?? model.connectedPeerID) : "" }
    private var currentPeer: ChatPeer? { model.chatPeers.first { $0.id == currentPeerID } }
    private var currentDevice: LANDevice? {
        if let selectedDeviceID { return model.nearbyDevices.first { $0.id == selectedDeviceID } }
        guard let currentPeer else { return nil }
        return model.nearbyDevices.first { $0.serviceName == currentPeer.serviceName }
    }
    private var currentName: String { currentPeer?.name ?? currentDevice?.displayName ?? "设备会话" }
    private var viewingConnected: Bool { model.isConnected && currentPeerID == model.connectedPeerID }
    private var draftKey: String { currentPeerID.isEmpty ? (selectedDeviceID ?? "") : currentPeerID }
    private var draft: Binding<String> {
        Binding(get: { drafts[draftKey] ?? "" }, set: { drafts[draftKey] = $0 })
    }
    private var visibleMessages: [ChatMessage] { model.messages.filter { $0.peerID == currentPeerID } }
    private var undisplayedDevices: [LANDevice] {
        model.nearbyDevices.filter { device in !model.chatPeers.contains { $0.serviceName == device.serviceName } }
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider()
            VStack(spacing: 0) {
                if model.settings.firstRunComplete && !model.isIdentityReady { identityAuthorizationBanner }
                if !model.settings.firstRunComplete && !model.isFirstRun {
                    HStack {
                        Label("首次设置尚未完成", systemImage: "info.circle").font(.caption)
                        Spacer()
                        Button("继续设置") { model.isFirstRun = true }.buttonStyle(.borderless)
                    }.padding(.horizontal, 18).padding(.vertical, 9).background(Color.accentColor.opacity(0.07))
                }
                conversationHeader
                Divider()
                timeline
                Divider()
                composer
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .sheet(isPresented: Binding(get: { model.pendingConnectionPeerName != nil }, set: { if !$0 { model.approveConnection(false) } })) { approvalSheet }
        .sheet(isPresented: $showManualConnection, onDismiss: {
            if let device = pendingDevice { pendingDevice = nil; model.requestConnection(to: device) }
        }) {
            SendView { device in pendingDevice = device; showManualConnection = false }.environmentObject(model)
        }
        .sheet(isPresented: $showHistory) { historySheet }
        .sheet(isPresented: $showConnectionHelp) { ConnectionHelpView() }
        .sheet(isPresented: $model.isFirstRun) { SetupView(isFirstRun: true).environmentObject(model).interactiveDismissDisabled() }
        .alert("飞鸽快传", isPresented: Binding(get: { model.errorMessage != nil }, set: { if !$0 { model.clearError() } })) {
            if model.shouldSuggestNetworkHelp {
                Button("连接排查") { model.clearError(); showConnectionHelp = true }
            }
            Button("好", role: .cancel) { model.clearError() }
        } message: { Text(model.errorMessage ?? "") }
        .confirmationDialog("删除此会话记录？", isPresented: Binding(
            get: { pendingDeletePeerID != nil },
            set: { if !$0 { pendingDeletePeerID = nil } }
        ), titleVisibility: .visible) {
            Button("删除记录", role: .destructive) { deletePendingConversation() }
            Button("取消", role: .cancel) { pendingDeletePeerID = nil }
        } message: {
            Text("只删除本机保存的消息和会话状态，不删除已接收的文件或独立传输记录。")
        }
        .onChange(of: model.connectedPeerID) { id in
            if !id.isEmpty { selectedPeerID = id; selectedDeviceID = nil }
        }
        .task { await model.start() }
    }

    private var identityAuthorizationBanner: some View {
        HStack(alignment: .top, spacing: 12) {
            if model.isPreparingIdentity { ProgressView().controlSize(.small).padding(.top, 3) }
            else { Image(systemName: "lock.shield").foregroundStyle(Color.accentColor) }
            VStack(alignment: .leading, spacing: 5) {
                Text(model.isPreparingIdentity ? "正在准备安全连接" : "请先完成系统授权")
                    .font(.system(size: 13, weight: .medium))
                Text(model.identityPreparationError ?? "如出现 macOS 钥匙串弹窗，请输入登录密码并选择“始终允许”，避免后续连接重复询问。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            if !model.isPreparingIdentity { Button("重新授权") { Task { await model.start() } } }
        }.padding(16).background(Color.accentColor.opacity(0.07))
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Label("设备会话", systemImage: "bubble.left.and.bubble.right.fill").font(.headline)
                Spacer()
                Button { model.refreshPeers() } label: {
                    if model.isRefreshingPeers { ProgressView().controlSize(.small) }
                    else { Image(systemName: "arrow.clockwise") }
                }
                .buttonStyle(.borderless)
                .help(model.isRefreshingPeers ? "正在刷新附近设备" : "刷新附近设备")
                .disabled(model.isPreparingIdentity || model.isRefreshingPeers)
            }.padding(.horizontal, 12)
            if model.isRefreshingPeers || model.lastPeerRefreshAt != nil {
                HStack(spacing: 5) {
                    if model.isRefreshingPeers { ProgressView().controlSize(.mini) }
                    Text(model.isRefreshingPeers ? "正在搜索设备…" : "上次刷新：\(model.lastPeerRefreshAt?.formatted(date: .omitted, time: .shortened) ?? "")")
                        .font(.caption2).foregroundStyle(model.peerRefreshFailed ? .red : .secondary).lineLimit(1)
                }.padding(.horizontal, 12)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(model.chatPeers) { peer in
                        Button { selectedPeerID = peer.id; selectedDeviceID = nil } label: {
                            peerRow(name: peer.name, detail: peer.preview, active: peer.id == currentPeerID,
                                    connected: model.isConnected && peer.id == model.connectedPeerID)
                        }
                        .buttonStyle(.plain)
                        .contextMenu {
                            Button("打开会话") { openConversation(peer.id) }
                            if model.connectedPeerID == peer.id && model.isConnected {
                                Button("断开连接") { model.disconnect() }
                            }
                            Divider()
                            Button("隐藏会话") { model.hideConversation(peer.id) }
                            Button("删除记录…", role: .destructive) { pendingDeletePeerID = peer.id }
                                .disabled(!model.canDeleteConversation(peer.id))
                        }
                    }
                    if !undisplayedDevices.isEmpty {
                        Text("附近的设备").font(.caption).foregroundStyle(.secondary).padding(.leading, 12).padding(.top, 12)
                        ForEach(undisplayedDevices) { device in
                            Button { selectedDeviceID = device.id; selectedPeerID = nil } label: {
                                peerRow(name: device.displayName, detail: "在线 · 尚未连接", active: selectedDeviceID == device.id, connected: false)
                            }.buttonStyle(.plain)
                        }
                    }
                    if model.chatPeers.isEmpty && model.nearbyDevices.isEmpty {
                        VStack(alignment: .leading, spacing: 5) {
                            Text(model.discoveryState).font(.caption).foregroundStyle(model.peerRefreshFailed ? .red : .secondary)
                            if let refreshed = model.lastPeerRefreshAt {
                                Text("上次刷新：\(refreshed.formatted(date: .omitted, time: .shortened))")
                                    .font(.caption2).foregroundStyle(.tertiary)
                            }
                        }.padding(12)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 12) {
                Button { showManualConnection = true } label: { Label("手动连接", systemImage: "network") }
                    .disabled(!model.isIdentityReady || model.isConnected || model.isConnecting)
                Button { showHistory = true } label: { Label("传输记录", systemImage: "clock.arrow.circlepath") }
                Button { showConnectionHelp = true } label: { Label("连接排查", systemImage: "questionmark.circle") }
                Divider()
                HStack(spacing: 8) {
                    Image(systemName: "laptopcomputer").foregroundStyle(Color.accentColor)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(model.settings.deviceName).font(.system(size: 12, weight: .medium)).lineLimit(1)
                        Text(model.listenerPort == nil ? model.discoveryState : "局域网内可被发现").font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                    }
                    Spacer(minLength: 0)
                    Button { openWindow(id: "settings") } label: { Image(systemName: "gearshape") }
                        .help("修改设备名称和接收位置").accessibilityLabel("偏好设置")
                }
                if let port = model.listenerPort {
                    Text("\(model.localAddress):\(port)").font(.caption2.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }.buttonStyle(.borderless).padding(.horizontal, 12)
        }
        .padding(.horizontal, 10).padding(.vertical, 22).frame(width: 238)
        .background(colorScheme == .light ? Color.white : Color(nsColor: .underPageBackgroundColor))
    }

    private func peerRow(name: String, detail: String, active: Bool, connected: Bool) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "desktopcomputer").foregroundStyle(Color.accentColor)
                .frame(width: 37, height: 39).background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 5) {
                Text(name).font(.system(size: 13, weight: .medium)).lineLimit(1)
                Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
            if connected { Circle().fill(.green).frame(width: 6, height: 6).accessibilityLabel("已连接") }
        }
        .padding(11).frame(maxWidth: .infinity, alignment: .leading)
        .background(active ? Color.accentColor.opacity(0.13) : .clear, in: RoundedRectangle(cornerRadius: 10))
        .contentShape(Rectangle())
    }

    private func openConversation(_ id: String) {
        selectedPeerID = id
        selectedDeviceID = nil
    }

    private func deletePendingConversation() {
        guard let id = pendingDeletePeerID else { return }
        do {
            try model.deleteConversation(id)
            if selectedPeerID == id { selectedPeerID = nil }
        } catch {
            model.errorMessage = error.localizedDescription
        }
        pendingDeletePeerID = nil
    }

    private var conversationHeader: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 5) {
                Text(currentName).font(.system(size: 17, weight: .semibold))
                Text(viewingConnected ? "已连接 · 可以聊天、互传图片与文件" : model.isConnecting ? model.connectionState : "对方同意连接后，即可聊天和传输")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if viewingConnected { Button("断开连接") { model.disconnect() } }
            else if model.isConnecting { Button("取消连接") { model.disconnect() } }
            else if let device = currentDevice {
                Button("请求连接") { model.requestConnection(to: device) }.disabled(!model.isIdentityReady || model.isConnected)
                    .help(model.isConnected ? "请先断开当前设备，再连接另一台设备" : "请求对方同意连接")
            }
        }.padding(.horizontal, 24).padding(.vertical, 18)
    }

    private var timeline: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 22) {
                    if visibleMessages.isEmpty {
                        VStack(spacing: 12) {
                            Image(systemName: "bubble.left.and.bubble.right").font(.system(size: 40)).foregroundStyle(Color.accentColor.opacity(0.65))
                            Text(viewingConnected ? "连接成功，开始聊天吧" : "选择设备，开始一个会话").font(.headline)
                            Text("文字、图片和文件，都在同一个会话里。")
                                .font(.subheadline).foregroundStyle(.secondary)
                        }.frame(maxWidth: .infinity).padding(.vertical, 90)
                    }
                    ForEach(visibleMessages, id: \.rowID) { message in
                        ChatMessageView(message: message,
                                        canRetry: viewingConnected && (message.kind == .text ? !model.isSendingText : !model.isSending)) {
                            model.retryMessage(message)
                        }.id(message.rowID)
                    }
                    Color.clear.frame(height: 1).id("conversation-bottom")
                }.padding(24)
            }
            .background(Color(nsColor: .textBackgroundColor).opacity(0.4))
            .onChange(of: visibleMessages.count) { _ in proxy.scrollTo("conversation-bottom", anchor: .bottom) }
            .onChange(of: currentPeerID) { _ in proxy.scrollTo("conversation-bottom", anchor: .bottom) }
        }
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 16) {
                Button { model.chooseAndSendFiles(imagesOnly: true) } label: { Label("图片", systemImage: "photo") }
                    .disabled(!viewingConnected || model.isSending)
                Button { model.chooseAndSendFiles() } label: { Label("文件", systemImage: "paperclip") }
                    .disabled(!viewingConnected || model.isSending)
                Spacer()
                if viewingConnected && (model.isSending || model.isReceivingFile) {
                    Button("取消传输") { model.cancelTransfer() }.help("取消会断开当前连接，未完成文件会被清理")
                }
                Text("文件大小不限").font(.caption2).foregroundStyle(.secondary)
            }.buttonStyle(.borderless)
            ChatComposer(text: draft, enabled: viewingConnected, onSend: sendDraft) { image in
                model.startSendingPastedImage(image)
            }
                .frame(height: 70)
                .overlay(alignment: .topLeading) {
                    if draft.wrappedValue.isEmpty {
                        Text(viewingConnected ? "输入消息，也可以将图片或文件拖到这里" : "连接设备后即可发送消息")
                            .font(.system(size: 14)).foregroundStyle(.tertiary).padding(.leading, 10).padding(.top, 8)
                            .allowsHitTesting(false)
                    }
                }
            HStack {
                Text("Enter 发送 · Shift+Enter 换行").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                Button("发送", action: sendDraft).buttonStyle(.borderedProminent)
                    .disabled(!viewingConnected || model.isSendingText || draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(.horizontal, 24).padding(.vertical, 14)
        .background(dropTargeted ? Color.accentColor.opacity(0.12) : Color(nsColor: .controlBackgroundColor))
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter(\.isFileURL)
            guard viewingConnected, !model.isSending, !files.isEmpty else { return false }
            model.startSending(files)
            return true
        } isTargeted: { dropTargeted = $0 }
    }

    private func sendDraft() {
        guard viewingConnected else { return }
        let key = draftKey
        if model.sendText(drafts[key] ?? "") { drafts[key] = "" }
    }

    private var approvalSheet: some View {
        VStack(alignment: .leading, spacing: 18) {
            Label("连接请求", systemImage: "link").font(.title2.bold())
            Text("\(model.pendingConnectionPeerName ?? "附近设备") 想要连接你的设备。").font(.headline)
            Text("同意后，双方可以直接聊天、发送图片和文件，直到断开连接。")
                .font(.subheadline).foregroundStyle(.secondary)
            Text("接收位置：\(model.settings.receiveDirectory.path)").font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("拒绝") { model.approveConnection(false) }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("同意连接") { model.approveConnection(true) }.keyboardShortcut(.defaultAction)
            }
        }.padding(24).frame(width: 440).interactiveDismissDisabled()
    }

    private var historySheet: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack { Text("传输记录").font(.title2.bold()); Spacer(); Button("完成") { showHistory = false } }
            if model.records.isEmpty { Text("暂无传输记录").foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity) }
            else {
                List(model.records) { record in
                    HStack {
                        Image(systemName: record.direction == .received ? "arrow.down.doc" : "arrow.up.doc").foregroundStyle(Color.accentColor)
                        VStack(alignment: .leading, spacing: 5) {
                            Text(record.fileName)
                            Text("\(record.peerName ?? "早期记录") · \(ByteCountFormatter.string(fromByteCount: record.size, countStyle: .file)) · \(record.date.formatted())")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if let path = record.savedPath {
                            Button("显示") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
                        }
                    }.padding(.vertical, 5)
                }
            }
        }.padding(24).frame(width: 680, height: 480)
    }
}
