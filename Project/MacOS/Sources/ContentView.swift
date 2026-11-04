import FeigeIdentity
import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject private var model: TransferModel
    @Environment(\.openWindow) private var openWindow
    @Environment(\.colorScheme) private var colorScheme
    @State private var selectedPeerID: String?
    @State private var selectedGroupID: UUID?
    @State private var selectedDeviceID: String?
    @State private var showGroupMembers = false
    @State private var groupMemberSearch = ""
    @State private var showNewGroup = false
    @State private var showManualConnection = false
    @State private var pendingDevice: LANDevice?
    @State private var showHistory = false
    @State private var dropTargeted = false
    @State private var pendingDeletePeerID: String?
    @State private var pendingMergePeerID: String?
    @State private var showConnectionHelp = false
    @State private var showUsageHelp = false
    @State private var showEmojiPalette = false

    private var currentGroup: GroupConversation? { selectedGroupID.flatMap { id in model.groups.first { $0.id == id } } }
    private var pendingGroupPreparations: [PendingGroupPreparation] {
        model.pendingGroupPreparations.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString }
    }
    private var pendingGroupAborts: [PendingGroupAbort] {
        model.pendingGroupAborts.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString }
    }
    private var pendingGroupCommits: [PendingGroupCommit] {
        model.pendingGroupCommits.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString }
    }
    private var currentPeerID: String { selectedGroupID == nil && selectedDeviceID == nil ? (selectedPeerID ?? model.connectedPeerID) : "" }
    private var currentPeer: ChatPeer? { model.chatPeers.first { $0.id == currentPeerID } }
    private var currentDevice: LANDevice? {
        if let selectedDeviceID { return model.nearbyDevices.first { $0.id == selectedDeviceID } }
        guard let currentPeer else { return nil }
        return model.nearbyDevice(forPeer: currentPeer.id)
    }
    private var currentName: String { currentGroup?.name ?? currentPeer?.name ?? currentDevice?.displayName ?? "设备会话" }
    private var currentConnectionPending: Bool {
        if let currentDevice { return model.isConnectionPending(serviceName: currentDevice.serviceName) }
        return !currentPeerID.isEmpty && model.isConnectionPending(peerID: currentPeerID)
    }
    private var viewingConnected: Bool { selectedGroupID == nil && model.isConnected(to: currentPeerID) }
    private var composerEnabled: Bool {
        if let currentGroup { return model.canSendGroupText(currentGroup.id) }
        // A known direct conversation accepts messages offline; they wait in the outbox.
        return viewingConnected || (selectedGroupID == nil && model.canQueueOffline(currentPeerID))
    }
    private var visibleConversationKey: String? {
        if let selectedGroupID { return TransferModel.groupConversationKey(selectedGroupID) }
        return currentPeerID.isEmpty ? nil : currentPeerID
    }
    private var draftKey: String { selectedGroupID?.uuidString ?? (currentPeerID.isEmpty ? (selectedDeviceID ?? "") : currentPeerID) }
    private var draft: Binding<String> {
        Binding(get: { model.drafts[draftKey] ?? "" }, set: { model.setDraft($0, for: draftKey) })
    }
    private var visibleMessages: [ChatMessage] {
        guard let group = currentGroup else { return model.messages.filter { $0.peerID == currentPeerID } }
        return model.groupMessages.filter { $0.groupID == group.id }.map { envelope in
            let sender = group.members.first { $0.fingerprint == envelope.senderFingerprint }
            let delivery = model.groupDeliveries.first { $0.groupID == group.id && $0.id == envelope.id }
            let isLocal = envelope.senderFingerprint == model.identityFingerprint
            let status: ChatMessage.Status
            var failureReason: String?
            if !isLocal { status = .received }
            else if let delivery, delivery.isComplete {
                status = delivery.recipients.allSatisfy { $0.status == .delivered } ? .delivered : .failed
                if status == .failed {
                    let failed = delivery.recipients.filter { $0.status != .delivered }
                    failureReason = "已送达 \(delivery.deliveredCount)/\(delivery.recipients.count) 位成员；\(failed.map { $0.failureReason ?? $0.status.rawValue }.joined(separator: "、"))"
                }
            } else { status = .sending }
            let kind: ChatMessage.Kind = switch envelope.kind {
            case .text: .text
            case .image: .image
            case .file: .file
            }
            let localPath = envelope.fileMetadata.flatMap { model.groupFileLocations[$0.transferID] }
            let progress = delivery.flatMap { value -> Double? in
                guard !value.recipients.isEmpty else { return isLocal ? 1 : nil }
                return value.recipients.reduce(0) { $0 + $1.progress } / Double(value.recipients.count)
            } ?? (isLocal ? 1 : 0)
            return ChatMessage(id: envelope.id, peerID: envelope.senderFingerprint,
                               peerName: sender?.displayName ?? String(envelope.senderFingerprint.suffix(8)),
                               direction: isLocal ? .sent : .received, kind: kind, date: envelope.date,
                               text: envelope.text, fileName: envelope.fileMetadata?.fileName,
                               size: envelope.fileMetadata.flatMap { Int64(exactly: $0.size) },
                               localPath: localPath, status: status, progress: progress,
                               failureReason: failureReason)
        }
    }
    /// Nearby entries whose device already has a visible conversation are reached from that conversation.
    private var undisplayedDevices: [LANDevice] {
        let shown = Set(model.chatPeers.compactMap { model.nearbyDevice(forPeer: $0.id)?.id })
        return model.nearbyDevices.filter { !shown.contains($0.id) }
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider()
            VStack(spacing: 0) {
                if !model.settings.firstRunComplete && !model.isFirstRun {
                    HStack {
                        Label("首次设置尚未完成", systemImage: "info.circle").font(.caption)
                        Spacer()
                        Button("继续设置") { model.isFirstRun = true }.buttonStyle(.borderless)
                    }.padding(.horizontal, 18).padding(.vertical, 9).background(Color.accentColor.opacity(0.07))
                }
                GuidancePanel()
                conversationHeader
                Divider()
                HStack(spacing: 0) {
                    timeline
                    if let group = currentGroup, showGroupMembers {
                        Divider()
                        GroupMembersInspector(group: group, searchText: $groupMemberSearch, isPresented: $showGroupMembers)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
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
        .sheet(isPresented: $showNewGroup) {
            GroupMemberPickerSheet(mode: .create, existingMemberCount: 1,
                                   candidates: model.groupMemberPickerCandidates(for: nil),
                                   onConfirm: { name, peerIDs in
                Task {
                    do {
                        let id = try await model.createGroup(name: name, memberPeerIDs: peerIDs)
                        selectedGroupID = id
                        selectedPeerID = nil
                        selectedDeviceID = nil
                        showNewGroup = false
                    } catch {
                        model.errorMessage = error.localizedDescription
                        if let groupError = error as? GroupConversationError,
                           groupError == .groupPreparationPending {
                            showNewGroup = false
                        }
                    }
                }
            }, onCancel: { showNewGroup = false })
        }
        .sheet(isPresented: $showHistory) { historySheet }
        .sheet(isPresented: Binding(get: { !model.pendingGroupFileApprovals.isEmpty }, set: { _ in })) {
            if let approval = model.pendingGroupFileApprovals.first {
                GroupFileApprovalSheet(approval: approval) { accepted in
                    model.decideGroupFile(approval.manifest.metadata.transferID,
                                          peerID: approval.manifest.senderFingerprint, accepted: accepted)
                }
            }
        }
        .sheet(isPresented: $showConnectionHelp) { ConnectionHelpView(mode: .troubleshooting) }
        .sheet(isPresented: $showUsageHelp) { ConnectionHelpView(mode: .usage) }
        .sheet(isPresented: $model.isFirstRun) { SetupView(isFirstRun: true).environmentObject(model).interactiveDismissDisabled() }
        .alert("飞鸽快传", isPresented: Binding(get: { model.errorMessage != nil }, set: { if !$0 { model.clearError() } })) {
            if model.shouldSuggestNetworkHelp {
                Button("连接排查") { model.clearError(); showConnectionHelp = true }
            }
            Button("使用帮助") { model.clearError(); showUsageHelp = true }
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
        .confirmationDialog("合并会话", isPresented: Binding(
            get: { pendingMergePeerID != nil },
            set: { if !$0 { pendingMergePeerID = nil } }
        ), titleVisibility: .visible) {
            if let source = pendingMergePeerID {
                ForEach(model.mergeTargets(for: source)) { target in
                    Button("并入“\(target.name)”") { mergePendingConversation(into: target.id) }
                }
            }
            Button("取消", role: .cancel) { pendingMergePeerID = nil }
        } message: {
            Text("“\(model.chatPeers.first { $0.id == pendingMergePeerID }?.name ?? "")”的聊天记录将移入所选会话，之后删除这个会话。仅在确认两者是同一台设备（例如对方重装或重置后设备指纹变化）时合并，合并后无法撤销。")
        }
        .confirmationDialog("群组邀请", isPresented: Binding(
            get: { !model.pendingGroupInvitations.isEmpty },
            set: { _ in }
        ), titleVisibility: .visible) {
            if let invitation = model.pendingGroupInvitations.first {
                Button("加入「\(invitation.message.name ?? "群组")」") {
                    model.acceptGroupInvitation(invitation.id, accepted: true)
                }
                Button("拒绝", role: .destructive) {
                    model.acceptGroupInvitation(invitation.id, accepted: false)
                }
            }
        } message: {
            if let invitation = model.pendingGroupInvitations.first {
                Text("\(invitation.peerName) 邀请你加入群聊。只有你确认后，邀请方才会提交群成员名单。")
            }
        }
        .confirmationDialog("成员申请退出群聊", isPresented: Binding(
            get: { !model.pendingGroupLeaveRequests.isEmpty },
            set: { _ in }
        ), titleVisibility: .visible) {
            if let request = model.pendingGroupLeaveRequests.first {
                Button("批准退出", role: .destructive) {
                    model.resolveGroupLeaveRequest(request.id, accepted: true)
                }
                Button("暂不批准") {
                    model.resolveGroupLeaveRequest(request.id, accepted: false)
                }
            }
        } message: {
            if let request = model.pendingGroupLeaveRequests.first {
                let groupName = model.groups.first(where: { $0.id == request.message.groupID })?.name ?? "群组"
                Text("\(request.peerName) 申请退出「\(groupName)」。批准后会同步更新成员名单。")
            }
        }
        .onChange(of: model.connectedPeerID) { id in
            if !id.isEmpty { selectedPeerID = id; selectedGroupID = nil; selectedDeviceID = nil }
        }
        .onChange(of: selectedGroupID) { id in
            groupMemberSearch = ""
            if id == nil { showGroupMembers = false }
        }
        .onChange(of: currentPeerID) { id in
            if !id.isEmpty { model.selectConversation(id) }
        }
        .onChange(of: visibleConversationKey) { model.setVisibleConversation($0) }
        .onAppear { model.setVisibleConversation(visibleConversationKey) }
        .onDisappear { model.setVisibleConversation(nil) }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            model.applicationDidBecomeActive()
        }
        .onReceive(NotificationCenter.default.publisher(for: .feigeOpenSettings)) { _ in openWindow(id: "settings") }
        .onReceive(NotificationCenter.default.publisher(for: .feigeOpenUpdates)) { _ in openWindow(id: "settings") }
        .task { await model.start() }
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
                    HStack {
                        Text("群组").font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button { showNewGroup = true } label: { Image(systemName: "plus") }
                            .buttonStyle(.borderless)
                            .help("创建群组")
                            .disabled(model.groupEligiblePeers.isEmpty || model.isPreparingIdentity)
                    }.padding(.horizontal, 12).padding(.top, 4)
                    if !pendingGroupPreparations.isEmpty || !pendingGroupAborts.isEmpty || !pendingGroupCommits.isEmpty {
                        Text("待处理群组操作").font(.caption).foregroundStyle(.secondary).padding(.horizontal, 12).padding(.top, 5)
                        ForEach(pendingGroupAborts, id: \.group.id) { abort in
                            HStack(spacing: 6) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(abort.group.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                                    Text("取消待确认 · 等待全部原成员持久确认")
                                        .font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                                }
                                Spacer(minLength: 2)
                                Button { retryGroupAbort(abort.group.id) } label: {
                                    if model.retryingGroupAborts.contains(abort.group.id) {
                                        ProgressView().controlSize(.mini)
                                    } else {
                                        Image(systemName: "arrow.clockwise")
                                    }
                                }
                                .buttonStyle(.borderless)
                                .help("重试取消确认")
                                .accessibilityLabel("重试取消「\(abort.group.name)」")
                                .disabled(model.retryingGroupAborts.contains(abort.group.id))
                            }
                            .padding(.horizontal, 10).padding(.vertical, 7)
                            .background(Color.orange.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
                        }
                        ForEach(pendingGroupPreparations, id: \.group.id) { preparation in
                            HStack(spacing: 6) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(preparation.group.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                                    let isNewGroup = !model.groups.contains { $0.id == preparation.group.id }
                                    Text(isNewGroup ? "创建待完成 · 等待成员确认" : "成员变更待完成 · 等待成员确认")
                                        .font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                                }
                                Spacer(minLength: 2)
                                Button { retryGroupPreparation(preparation.group.id) } label: {
                                    if model.retryingGroupPreparations.contains(preparation.group.id) {
                                        ProgressView().controlSize(.mini)
                                    } else {
                                        Image(systemName: "arrow.clockwise")
                                    }
                                }
                                .buttonStyle(.borderless)
                                .help("重试群组成员准备")
                                .accessibilityLabel("重试「\(preparation.group.name)」群组操作")
                                .disabled(model.retryingGroupPreparations.contains(preparation.group.id))
                                Button { cancelGroupPreparation(preparation.group.id) } label: {
                                    Image(systemName: "xmark")
                                }
                                .buttonStyle(.borderless)
                                .help("取消成员变更（仅提交前）")
                                .accessibilityLabel("取消「\(preparation.group.name)」成员变更")
                                .disabled(!model.canCancelPendingGroupPreparation(preparation.group.id))
                            }
                            .padding(.horizontal, 10).padding(.vertical, 7)
                            .background(Color.accentColor.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
                        }
                        ForEach(pendingGroupCommits, id: \.group.id) { commit in
                            HStack(spacing: 6) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(commit.group.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                                    let isNewGroup = !model.groups.contains { $0.id == commit.group.id }
                                    Text(isNewGroup ? "群组创建提交待完成" : "成员名单提交待完成")
                                        .font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                                }
                                Spacer(minLength: 2)
                                Button { retryGroupCommit(commit.group.id) } label: {
                                    if model.retryingGroupCommits.contains(commit.group.id) {
                                        ProgressView().controlSize(.mini)
                                    } else {
                                        Image(systemName: "arrow.clockwise")
                                    }
                                }
                                .buttonStyle(.borderless)
                                .help("重试群组提交")
                                .accessibilityLabel("重试「\(commit.group.name)」群组提交")
                                .disabled(model.retryingGroupCommits.contains(commit.group.id))
                            }
                            .padding(.horizontal, 10).padding(.vertical, 7)
                            .background(Color.orange.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
                        }
                    }
                    ForEach(model.groups.filter { group in
                        group.members.contains { $0.fingerprint == model.identityFingerprint }
                    }) { group in
                        Button {
                            selectedGroupID = group.id
                            selectedPeerID = nil
                            selectedDeviceID = nil
                        } label: {
                            peerRow(name: group.name,
                                    detail: "\(group.members.count) 位成员 · 修订 \(group.revision)",
                                    active: selectedGroupID == group.id, connected: false, symbol: "person.3.fill",
                                    unread: model.unreadCount(for: TransferModel.groupConversationKey(group.id)))
                        }
                        .buttonStyle(.plain)
                    }
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
                    }.padding(.horizontal, 12).padding(.top, 8)
                    ForEach(model.chatPeers) { peer in
                        Button { selectedPeerID = peer.id; selectedGroupID = nil; selectedDeviceID = nil } label: {
                            peerRow(name: peer.name, detail: peer.preview, active: peer.id == currentPeerID,
                                    connected: model.isConnected(to: peer.id), avatarId: model.avatarId(for: peer.id),
                                    unread: model.unreadCount(for: peer.id))
                        }
                        .buttonStyle(.plain)
                        .contextMenu {
                            Button("打开会话") { openConversation(peer.id) }
                            if model.isConnected(to: peer.id) {
                                Button("断开连接") { model.disconnectByUser(peerID: peer.id) }
                            }
                            trustedPeerButton(peer.id)
                            Divider()
                            Button("合并到其他会话…") { pendingMergePeerID = peer.id }
                                .disabled(!model.canDeleteConversation(peer.id) || model.mergeTargets(for: peer.id).isEmpty)
                            Button("隐藏会话") { model.hideConversation(peer.id) }
                            Button("删除记录…", role: .destructive) { pendingDeletePeerID = peer.id }
                                .disabled(!model.canDeleteConversation(peer.id))
                        }
                    }
                    if !undisplayedDevices.isEmpty {
                        Text("附近的设备").font(.caption).foregroundStyle(.secondary).padding(.leading, 12).padding(.top, 12)
                        ForEach(undisplayedDevices) { device in
                            Button { selectedDeviceID = device.id; selectedGroupID = nil; selectedPeerID = nil } label: {
                                peerRow(name: device.displayName, detail: "在线 · 尚未连接", active: selectedDeviceID == device.id,
                                        connected: false, avatarId: model.avatarId(for: device.fingerprintHint ?? "", hint: device.avatarId))
                            }.buttonStyle(.plain)
                        }
                    }
                    if model.chatPeers.isEmpty && model.nearbyDevices.isEmpty {
                        VStack(alignment: .leading, spacing: 5) {
                            if model.isIdentityReady {
                                Text(model.isRefreshingPeers ? "正在搜索附近设备…" : "尚未找到设备")
                                    .font(.system(size: 12, weight: .medium))
                                Text("请确认另一台设备已打开飞鸽快传，并处于可互通的网络。")
                                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                            }
                            Text(model.discoveryState).font(.caption).foregroundStyle(model.peerRefreshFailed ? .red : .secondary)
                            if let refreshed = model.lastPeerRefreshAt {
                                Text("上次刷新：\(refreshed.formatted(date: .omitted, time: .shortened))")
                                    .font(.caption2).foregroundStyle(.tertiary)
                            }
                            HStack(spacing: 8) {
                                Button("重新搜索") { model.refreshPeers() }
                                    .disabled(model.isPreparingIdentity || model.isRefreshingPeers)
                                Button("连接排查") { showConnectionHelp = true }
                            }
                            .controlSize(.small).padding(.top, 3)
                        }.padding(12)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 12) {
                Button { showManualConnection = true } label: { Label("手动连接", systemImage: "network") }
                    .disabled(!model.isIdentityReady)
                Button { showHistory = true } label: { Label("传输记录", systemImage: "clock.arrow.circlepath") }
                Button { showConnectionHelp = true } label: { Label("连接排查", systemImage: "network.badge.shield.half.filled") }
                Button { showUsageHelp = true } label: { Label("使用帮助", systemImage: "questionmark.circle") }
                Divider()
                HStack(spacing: 8) {
                    AvatarView(avatarID: model.settings.avatarId, size: 30)
                        .accessibilityLabel("本机头像")
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

    private func peerRow(name: String, detail: String, active: Bool, connected: Bool,
                         avatarId: String? = nil, symbol: String = "desktopcomputer", unread: Int = 0) -> some View {
        HStack(spacing: 10) {
            if let avatarId {
                AvatarView(avatarID: avatarId, size: 37)
                    .frame(width: 37, height: 39)
            } else {
                Image(systemName: symbol).foregroundStyle(Color.accentColor)
                    .frame(width: 37, height: 39).background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
            }
            VStack(alignment: .leading, spacing: 5) {
                Text(name).font(.system(size: 13, weight: .medium)).lineLimit(1)
                Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
            if unread > 0 {
                Text(unread > 99 ? "99+" : "\(unread)")
                    .font(.caption2.weight(.semibold)).monospacedDigit().foregroundStyle(.white)
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(Color.red, in: Capsule())
                    .accessibilityLabel("\(unread) 条未读")
            }
            if connected { Circle().fill(.green).frame(width: 6, height: 6).accessibilityLabel("已连接") }
        }
        .padding(11).frame(maxWidth: .infinity, alignment: .leading)
        .background(active ? Color.accentColor.opacity(0.13) : .clear, in: RoundedRectangle(cornerRadius: 10))
        .contentShape(Rectangle())
    }

    private func openConversation(_ id: String) {
        selectedPeerID = id
        selectedGroupID = nil
        selectedDeviceID = nil
    }

    private func mergePendingConversation(into target: String) {
        guard let source = pendingMergePeerID else { return }
        do {
            try model.mergeConversation(source, into: target)
            openConversation(target)
        } catch {
            model.errorMessage = error.localizedDescription
        }
        pendingMergePeerID = nil
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

    private func cancelGroupPreparation(_ id: UUID) {
        Task { @MainActor in
            do {
                let completed = try await model.cancelPendingGroupPreparation(id)
                if !completed && model.pendingGroupAborts[id] != nil &&
                    !model.retryingGroupAborts.contains(id) {
                    model.errorMessage = "取消意图已保存；请连接全部原成员的 V5 通道后重试。"
                }
            } catch {
                model.errorMessage = error.localizedDescription
            }
        }
    }

    private func retryGroupAbort(_ id: UUID) {
        Task { @MainActor in
            let completed = await model.retryPendingGroupAbort(id)
            if !completed && model.pendingGroupAborts[id] != nil &&
                !model.retryingGroupAborts.contains(id) && model.errorMessage == nil {
                model.errorMessage = "取消仍待全部原成员确认，请连接成员后重试。"
            }
        }
    }

    private func retryGroupPreparation(_ id: UUID) {
        Task { @MainActor in
            do {
                let started = try await model.retryPendingGroupPreparation(id)
                if !started && model.pendingGroupPreparations[id] != nil &&
                    !model.retryingGroupPreparations.contains(id) {
                    model.errorMessage = "请先连接所有待处理成员，然后重试群组操作。"
                }
            } catch {
                model.errorMessage = error.localizedDescription
            }
        }
    }

    private func retryGroupCommit(_ id: UUID) {
        Task { @MainActor in
            let completed = await model.retryPendingGroupCommit(id)
            if !completed && model.pendingGroupCommits[id] != nil &&
                !model.retryingGroupCommits.contains(id) && model.errorMessage == nil {
                model.errorMessage = "群组提交仍待完成，请确认相关成员在线后重试。"
            }
        }
    }

    private var conversationHeader: some View {
        HStack(spacing: 12) {
            if currentGroup != nil {
                Image(systemName: "person.3.fill").foregroundStyle(Color.accentColor)
                    .frame(width: 42, height: 42).background(Color.accentColor.opacity(0.09), in: RoundedRectangle(cornerRadius: 12))
            } else if let currentPeer {
                AvatarView(avatarID: model.avatarId(for: currentPeer.id), size: 42)
            } else if let currentDevice {
                AvatarView(avatarID: model.avatarId(for: currentDevice.fingerprintHint ?? "", hint: currentDevice.avatarId), size: 42)
            } else {
                AvatarView(avatarID: model.settings.avatarId, size: 42)
            }
            VStack(alignment: .leading, spacing: 5) {
                Text(currentName).font(.system(size: 17, weight: .semibold))
                Text(currentGroup.map { "群聊 · \($0.members.count) 位成员 · 成员变更需成员确认" }
                     ?? (viewingConnected ? "已连接 · 可以聊天、互传图片与文件" : currentConnectionPending ? model.connectionState : "对方同意连接后，即可聊天和传输"))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if let group = currentGroup {
                Button {
                    groupMemberSearch = ""
                    showGroupMembers.toggle()
                } label: {
                    Label("成员 \(group.members.count)/\(GroupConversation.maximumMemberCount)", systemImage: "person.3")
                }
                .buttonStyle(.borderless)
                .help("查看成员、连接状态和群管理操作")
                .accessibilityLabel("群聊成员，\(group.members.count) 位")
            }
            if viewingConnected { Button("断开连接") { model.disconnectByUser(peerID: currentPeerID) } }
            else if selectedGroupID == nil && currentConnectionPending {
                Button("取消连接") {
                    if let device = currentDevice { model.cancelConnection(serviceName: device.serviceName) }
                }
            } else if let device = currentDevice {
                Button("请求连接") { model.requestConnection(to: device) }
                    .disabled(!model.isIdentityReady || model.isConnected(to: currentPeerID) || model.isConnectionPending(serviceName: device.serviceName))
                    .help(model.isConnected(to: currentPeerID) ? "此设备已连接" : "请求对方同意连接")
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
                            Text(currentGroup != nil ? "群聊已建立，可以发送群消息" : viewingConnected ? "连接成功，开始聊天吧" : "选择设备，开始一个会话").font(.headline)
                            Text(currentGroup != nil ? "消息会单独发送给每位成员，并记录各自回执。" : "文字、图片和文件，都在同一个会话里。")
                                .font(.subheadline).foregroundStyle(.secondary)
                        }.frame(maxWidth: .infinity).padding(.vertical, 90)
                    }
                    ForEach(visibleMessages, id: \.rowID) { message in
                        ChatMessageView(message: message,
                                        avatarId: message.direction == .sent ? model.settings.avatarId : model.avatarId(for: message.peerID),
                                        canRetry: currentGroup.map { model.canRetryGroupMessage($0.id, messageID: message.id) }
                                            ?? (!viewingConnected || (message.kind == .text ? !model.isSendingText(to: currentPeerID) : !model.isSending(to: currentPeerID))),
                                        retry: {
                                            if let currentGroup { model.retryGroupMessage(currentGroup.id, messageID: message.id) }
                                            else { model.retryMessage(message) }
                                        },
                                        cancel: { model.cancelOutboxMessage(message) },
                                        accept: { model.decideIncomingFile(message.id, peerID: message.peerID, accepted: true) },
                                        decline: { model.decideIncomingFile(message.id, peerID: message.peerID, accepted: false) })
                            .id(message.rowID)
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
                Button {
                    if let currentGroup { model.chooseAndSendGroupFiles(groupID: currentGroup.id, imagesOnly: true) }
                    else { model.chooseAndSendFiles(imagesOnly: true, to: currentPeerID) }
                } label: { Label("图片", systemImage: "photo") }
                    .disabled(!composerEnabled || (currentGroup.map { model.isSendingGroupFile($0.id) } ?? model.isSending(to: currentPeerID)))
                Button {
                    if let currentGroup { model.chooseAndSendGroupFiles(groupID: currentGroup.id) }
                    else { model.chooseAndSendFiles(to: currentPeerID) }
                } label: { Label("文件", systemImage: "paperclip") }
                    .disabled(!composerEnabled || (currentGroup.map { model.isSendingGroupFile($0.id) } ?? model.isSending(to: currentPeerID)))
                Button { showEmojiPalette.toggle() } label: { Label("表情", systemImage: "face.smiling") }
                    .disabled(!composerEnabled)
                    .popover(isPresented: $showEmojiPalette, arrowEdge: .bottom) {
                        EmojiPaletteView { emoji in
                            let key = draftKey
                            model.setDraft(EmojiPalette.append(emoji, to: model.drafts[key] ?? ""), for: key)
                        }
                    }
                    .help("插入表情（也可用 ⌃⌘Space 系统表情面板）")
                Spacer()
                if viewingConnected && (model.isSending(to: currentPeerID) || model.isReceiving(from: currentPeerID)) {
                    Button("取消传输") { model.cancelTransfer() }.help("取消会断开当前连接，未完成文件会被清理")
                }
                Text("文件大小不限").font(.caption2).foregroundStyle(.secondary)
            }.buttonStyle(.borderless)
            ChatComposer(text: draft, enabled: composerEnabled, onSend: sendDraft) { image in
                if let currentGroup { model.startSendingPastedImage(image, toGroupID: currentGroup.id) }
                else { model.startSendingPastedImage(image, to: currentPeerID) }
            }
                .frame(height: 70)
                .overlay(alignment: .topLeading) {
                    if draft.wrappedValue.isEmpty {
                        Text(selectedGroupID != nil ? "输入群消息" : viewingConnected ? "输入消息，也可以将图片或文件拖到这里"
                             : composerEnabled ? "对方未连接，消息会在连接后自动发送" : "连接设备后即可发送消息")
                            .font(.system(size: 14)).foregroundStyle(.tertiary).padding(.leading, 10).padding(.top, 8)
                            .allowsHitTesting(false)
                    }
                }
            HStack {
                Text("Enter 发送 · Shift+Enter 换行").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                Button("发送", action: sendDraft).buttonStyle(.borderedProminent)
                    .disabled(!composerEnabled || (selectedGroupID == nil && model.isSendingText(to: currentPeerID)) || draft.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(.horizontal, 24).padding(.vertical, 14)
        .background(dropTargeted ? Color.accentColor.opacity(0.12) : Color(nsColor: .controlBackgroundColor))
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter(\.isFileURL)
            guard !files.isEmpty else { return false }
            if let currentGroup {
                guard composerEnabled, !model.isSendingGroupFile(currentGroup.id) else { return false }
                model.startSendingGroupFiles(files, groupID: currentGroup.id)
                return true
            }
            guard composerEnabled, !model.isSending(to: currentPeerID) else { return false }
            model.startSending(files, to: currentPeerID)
            return true
        } isTargeted: { dropTargeted = $0 }
    }

    private func sendDraft() {
        guard composerEnabled else { return }
        let key = draftKey
        let text = draft.wrappedValue
        let sent: Bool
        if let currentGroup { sent = model.sendGroupText(text, groupID: currentGroup.id) }
        else { sent = model.sendText(text, to: currentPeerID) }
        if sent {
            model.clearDraft(for: key)
            return
        }
        // A rejected send (empty, empty newline or longer than 8 KB) keeps the draft and explains why.
        if !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, (try? ChatText(text: text)) == nil {
            model.errorMessage = "单条文字最多 8 KB UTF-8；长文可作为文件发送。"
        }
    }

    @ViewBuilder
    private func trustedPeerButton(_ peerID: String) -> some View {
        if model.isTrustedPeer(peerID) {
            Button("取消常驻联系人") { try? model.setTrustedPeer(peerID, trusted: false) }
        } else {
            Button("设为常驻联系人") { try? model.setTrustedPeer(peerID, trusted: true) }
                .help("以后这台设备上线时自动连接，接收文件仍需确认")
        }
    }

    private var approvalSheet: some View {
        VStack(alignment: .leading, spacing: 18) {
            Label("连接请求", systemImage: "link").font(.title2.bold())
            HStack(spacing: 12) {
                AvatarView(avatarID: model.avatarId(for: model.pendingConnectionPeerFingerprint ?? ""), size: 48)
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(model.pendingConnectionPeerName ?? "附近设备") 想要连接你的设备。").font(.headline)
                    Text("已验证对方设备身份；头像仅用于显示。")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Text("同意后双方可以直接聊天、发送图片和文件。只需同意一次：以后这台设备连接无需再确认，上线时自动连接；可在设置的常驻联系人中移除。接收文件仍需确认。")
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

private struct GroupFileApprovalSheet: View {
    let approval: PendingGroupFileApproval
    let decide: (Bool) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("群文件接收请求", systemImage: "arrow.down.doc").font(.title2.bold())
            Text("\(approval.peerName) 在「\(approval.groupName)」中发送了一个文件。")
                .font(.headline)
            Text(approval.manifest.metadata.fileName).textSelection(.enabled)
            Text(ByteCountFormatter.string(fromByteCount: Int64(approval.manifest.metadata.size), countStyle: .file))
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("拒绝", role: .destructive) { decide(false) }
                Spacer()
                Button("接收文件") { decide(true) }.buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        }.padding(24).frame(width: 420).interactiveDismissDisabled()
    }
}
