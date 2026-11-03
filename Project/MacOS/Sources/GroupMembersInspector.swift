import SwiftUI

struct GroupMembersInspector: View {
    @EnvironmentObject private var model: TransferModel
    let group: GroupConversation
    @Binding var searchText: String
    @Binding var isPresented: Bool

    @State private var pendingRemoval: GroupConversationMember?
    @State private var showOwnerLeaveConfirmation = false

    private var isOwner: Bool { group.ownerFingerprint == model.identityFingerprint }
    private var rosterOperationPending: Bool {
        model.pendingGroupAborts[group.id] != nil || model.pendingGroupPreparations[group.id] != nil ||
            model.pendingGroupCommits[group.id] != nil
    }
    private var allOtherMembersConnected: Bool {
        group.members.filter { $0.fingerprint != model.identityFingerprint }
            .allSatisfy { model.isConnected(to: $0.fingerprint) }
    }
    private var candidates: [ChatPeer] {
        model.groupEligiblePeers.filter { peer in
            !group.members.contains(where: { $0.fingerprint == peer.id }) &&
                (searchText.isEmpty || peer.name.localizedCaseInsensitiveContains(searchText) ||
                 peer.id.localizedCaseInsensitiveContains(searchText))
        }
    }
    private var visibleMembers: [GroupConversationMember] {
        guard !searchText.isEmpty else { return group.members }
        return group.members.filter { member in
            (member.displayName?.localizedCaseInsensitiveContains(searchText) ?? false) ||
                member.fingerprint.localizedCaseInsensitiveContains(searchText) ||
                (member.fingerprint == model.identityFingerprint && "本机".localizedCaseInsensitiveContains(searchText))
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("群聊成员").font(.system(size: 15, weight: .semibold))
                    Text(group.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                    Text("\(group.members.count)/\(GroupConversation.maximumMemberCount) 位成员")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button { isPresented = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless).help("关闭成员面板").accessibilityLabel("关闭成员面板")
            }

            TextField("搜索成员", text: $searchText)
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("搜索群聊成员")

            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    if visibleMembers.isEmpty {
                        Text("没有匹配的成员。").font(.caption).foregroundStyle(.secondary).padding(.vertical, 8)
                    } else {
                        ForEach(visibleMembers) { member in
                            memberRow(member)
                        }
                    }

                    if isOwner {
                        Divider().padding(.vertical, 3)
                        Text("添加已连接设备").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                        Text("受邀设备需要在对端接受邀请；只有成员名单事务全部确认并提交后，新成员才正式加入。")
                            .font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        if group.members.count >= GroupConversation.maximumMemberCount {
                            Text("群聊已达到 20 人上限。").font(.caption).foregroundStyle(.secondary)
                        } else if candidates.isEmpty {
                            Text(searchText.isEmpty ? "没有可添加的已连接设备。" : "没有匹配的可添加设备。")
                                .font(.caption).foregroundStyle(.secondary)
                        } else {
                            ForEach(candidates) { peer in
                                HStack(spacing: 9) {
                                    AvatarView(avatarID: model.avatarId(for: peer.id), size: 32)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(peer.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                                        Text("已连接 · 可邀请").font(.caption2).foregroundStyle(.secondary)
                                    }
                                    Spacer(minLength: 4)
                                    Button("添加成员") { addMember(peer.id) }
                                        .accessibilityLabel("添加成员 \(peer.name)")
                                        .controlSize(.small)
                                        .disabled(rosterOperationPending || !allOtherMembersConnected ||
                                                  group.members.count >= GroupConversation.maximumMemberCount)
                                }
                                .padding(7)
                                .background(Color.primary.opacity(0.025), in: RoundedRectangle(cornerRadius: 8))
                            }
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Divider()
            operationStatus
            if isOwner {
                Button(group.members.count == 1 ? "结束群聊" : "退出并转让群主", role: .destructive) { showOwnerLeaveConfirmation = true }
                    .buttonStyle(.bordered)
                    .disabled(rosterOperationPending || !allOtherMembersConnected)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                if model.pendingGroupLeaveGroupIDs.contains(group.id) {
                    Label("退出申请已发送，等待群主处理。", systemImage: "clock")
                        .font(.caption).foregroundStyle(.secondary)
                } else {
                    Button("申请退出群聊", role: .destructive) { model.requestLeaveGroup(group.id) }
                        .buttonStyle(.bordered)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .padding(14)
        .frame(width: 310)
        .frame(maxHeight: .infinity)
        .background(Color(nsColor: .controlBackgroundColor))
        .confirmationDialog("确认移出群聊？", isPresented: Binding(
            get: { pendingRemoval != nil },
            set: { if !$0 { pendingRemoval = nil } }
        ), titleVisibility: .visible) {
            if let member = pendingRemoval {
                Button("移出群聊", role: .destructive) { removeMember(member) }
            }
            Button("取消", role: .cancel) { pendingRemoval = nil }
        } message: {
            Text("将「\(pendingRemoval?.displayName ?? "未知设备")」移出群聊。成员名单变更需要当前成员确认后生效。")
        }
        .confirmationDialog(group.members.count == 1 ? "结束群聊？" : "退出并转让群主？",
                            isPresented: $showOwnerLeaveConfirmation, titleVisibility: .visible) {
            Button(group.members.count == 1 ? "结束群聊" : "退出并转让群主", role: .destructive) { leaveAsOwner() }
            Button("取消", role: .cancel) { }
        } message: {
            Text(group.members.count == 1
                 ? "群聊只有你一位成员。结束后会从本机群聊列表移除，历史记录仍保留在本机。"
                 : "群主身份会转交给一位现有成员，成员名单更新后你将离开群聊。")
        }
    }

    @ViewBuilder
    private var operationStatus: some View {
        if model.pendingGroupAborts[group.id] != nil {
            HStack {
                Label("取消成员变更待确认", systemImage: "exclamationmark.circle").font(.caption)
                Spacer()
                Button("重试") { Task { _ = await model.retryPendingGroupAbort(group.id) } }
                    .controlSize(.small).disabled(model.retryingGroupAborts.contains(group.id))
            }
        } else if model.pendingGroupCommits[group.id] != nil {
            HStack {
                Label("成员名单提交待完成", systemImage: "clock").font(.caption)
                Spacer()
                Button("重试") { Task { _ = await model.retryPendingGroupCommit(group.id) } }
                    .controlSize(.small).disabled(model.retryingGroupCommits.contains(group.id))
            }
        } else if model.pendingGroupPreparations[group.id] != nil {
            VStack(alignment: .leading, spacing: 6) {
                Label("成员变更等待成员确认", systemImage: "clock").font(.caption)
                HStack {
                    Button("重试") {
                        Task {
                            do { _ = try await model.retryPendingGroupPreparation(group.id) }
                            catch { model.errorMessage = error.localizedDescription }
                        }
                    }
                    .controlSize(.small).disabled(model.retryingGroupPreparations.contains(group.id))
                    if model.canCancelPendingGroupPreparation(group.id) {
                        Button("取消变更") {
                            Task {
                                do { _ = try await model.cancelPendingGroupPreparation(group.id) }
                                catch { model.errorMessage = error.localizedDescription }
                            }
                        }
                        .controlSize(.small)
                    }
                }
            }
        } else {
            if isOwner, model.pendingGroupLeaveRequests.contains(where: { $0.message.groupID == group.id }) {
                Label("有成员申请退出，请处理待办请求。", systemImage: "person.crop.circle.badge.exclamationmark")
                    .font(.caption).foregroundStyle(.secondary)
            } else if isOwner {
                Text("添加新成员时，对方须先接受邀请；成员名单只有在所有成员确认并提交后才生效。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else {
                Text("申请退出需群主在线处理；成员名单变更完成前，当前群聊状态保持不变。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func memberRow(_ member: GroupConversationMember) -> some View {
        let isLocal = member.fingerprint == model.identityFingerprint
        let avatarId = isLocal ? model.settings.avatarId : model.avatarId(for: member.fingerprint)
        let name = member.displayName ?? "未知设备"
        let role = isLocal
            ? (member.fingerprint == group.ownerFingerprint ? "本机 · 群主" : "本机 · 成员")
            : (member.fingerprint == group.ownerFingerprint ? "群主" : "成员")
        let connected = isLocal || model.isConnected(to: member.fingerprint)
        return VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 9) {
                AvatarView(avatarID: avatarId, size: 36)
                VStack(alignment: .leading, spacing: 2) {
                    Text(name).font(.system(size: 12, weight: .medium)).lineLimit(1)
                    Text(isLocal ? role : "\(role) · \(connected ? "在线" : "离线")")
                        .font(.caption2).foregroundStyle(.secondary)
                }
                Spacer(minLength: 3)
                Circle().fill(connected ? Color.green : Color.gray.opacity(0.55)).frame(width: 7, height: 7)
                    .accessibilityLabel(connected ? "在线" : "离线")
            }
            if !isLocal {
                HStack(spacing: 8) {
                    Button(connected ? "断开连接" : "重新连接") {
                        if connected { model.disconnect(peerID: member.fingerprint) }
                        else { model.reconnectGroupMember(group.id, fingerprint: member.fingerprint) }
                    }
                    .buttonStyle(.borderless).font(.caption)
                    .disabled(connected ? !model.isConnected(to: member.fingerprint) :
                              !model.canReconnectGroupMember(group.id, fingerprint: member.fingerprint))
                    if isOwner && member.fingerprint != group.ownerFingerprint {
                        Spacer()
                        Button("移出群聊") { pendingRemoval = member }
                            .accessibilityLabel("移出成员 \(name)")
                            .buttonStyle(.borderless).font(.caption).foregroundStyle(.red)
                            .disabled(rosterOperationPending || !allOtherMembersConnected)
                    }
                }
            }
        }
        .padding(8)
        .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 9))
    }

    private func addMember(_ fingerprint: String) {
        Task {
            do { try await model.addGroupMember(fingerprint, to: group.id) }
            catch { model.errorMessage = error.localizedDescription }
        }
    }

    private func removeMember(_ member: GroupConversationMember) {
        pendingRemoval = nil
        Task {
            do { try await model.removeGroupMember(member.fingerprint, from: group.id) }
            catch { model.errorMessage = error.localizedDescription }
        }
    }

    private func leaveAsOwner() {
        Task {
            do {
                try await model.leaveGroupAsOwner(group.id)
                isPresented = false
            } catch { model.errorMessage = error.localizedDescription }
        }
    }
}
