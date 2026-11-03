import SwiftUI

enum GroupMemberPickerAvailability: Equatable {
    case available
    case existingMember
    case notConnected
    case unsupportedVersion

    var label: String {
        switch self {
        case .available: "已连接 · 可邀请"
        case .existingMember: "已在群中"
        case .notConnected: "未连接"
        case .unsupportedVersion: "对方版本不支持群聊"
        }
    }

    fileprivate var sortRank: Int {
        switch self {
        case .available: 0
        case .existingMember: 1
        case .notConnected, .unsupportedVersion: 2
        }
    }
}

struct GroupMemberPickerCandidate: Identifiable, Equatable {
    let id: String
    let name: String
    let avatarID: String
    let availability: GroupMemberPickerAvailability
}

/// Selection rules for the member picker, kept free of SwiftUI so they can be unit tested.
struct GroupMemberPickerSelection: Equatable {
    /// Members already counted against the limit, including this device (1 when creating a group).
    let existingMemberCount: Int
    private(set) var selectedIDs: [String] = []

    init(existingMemberCount: Int) { self.existingMemberCount = existingMemberCount }

    var remaining: Int { max(0, GroupConversation.maximumMemberCount - existingMemberCount - selectedIDs.count) }

    func isSelected(_ candidate: GroupMemberPickerCandidate) -> Bool {
        candidate.availability == .existingMember || selectedIDs.contains(candidate.id)
    }

    func canToggle(_ candidate: GroupMemberPickerCandidate) -> Bool {
        guard candidate.availability == .available else { return false }
        return selectedIDs.contains(candidate.id) || remaining > 0
    }

    mutating func toggle(_ candidate: GroupMemberPickerCandidate) {
        guard canToggle(candidate) else { return }
        if let index = selectedIDs.firstIndex(of: candidate.id) { selectedIDs.remove(at: index) }
        else { selectedIDs.append(candidate.id) }
    }

    mutating func remove(_ id: String) { selectedIDs.removeAll { $0 == id } }

    /// Selected devices that are still invitable; a device that disconnected after being picked is dropped.
    func confirmedIDs(in candidates: [GroupMemberPickerCandidate]) -> [String] {
        selectedIDs.filter { id in candidates.contains { $0.id == id && $0.availability == .available } }
    }

    static func visible(_ candidates: [GroupMemberPickerCandidate], matching search: String) -> [GroupMemberPickerCandidate] {
        let query = search.trimmingCharacters(in: .whitespacesAndNewlines)
        return candidates
            .filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) || $0.id.localizedCaseInsensitiveContains(query) }
            .enumerated()
            .sorted { ($0.element.availability.sortRank, $0.offset) < ($1.element.availability.sortRank, $1.offset) }
            .map(\.element)
    }
}

extension TransferModel {
    /// Conversation peers for the picker; members of `group` other than this device are listed as already in the group.
    func groupMemberPickerCandidates(for group: GroupConversation?) -> [GroupMemberPickerCandidate] {
        let memberIDs = Set(group?.members.map(\.fingerprint) ?? [])
        var result = chatPeers.filter { !memberIDs.contains($0.id) }.map { peer in
            GroupMemberPickerCandidate(id: peer.id, name: peer.name, avatarID: avatarId(for: peer.id),
                                       availability: groupInviteAvailability(for: peer.id))
        }
        for member in group?.members ?? [] where member.fingerprint != identityFingerprint {
            result.append(GroupMemberPickerCandidate(id: member.fingerprint, name: member.displayName ?? "未知设备",
                                                     avatarID: avatarId(for: member.fingerprint),
                                                     availability: .existingMember))
        }
        return result
    }
}

struct GroupMemberPickerSheet: View {
    enum Mode: Equatable {
        case create
        case add(groupName: String)
    }

    let mode: Mode
    let candidates: [GroupMemberPickerCandidate]
    /// Shown next to the confirm button and disables it, for example when an existing member is offline.
    let blockingReason: String?
    let onConfirm: (_ name: String, _ peerIDs: [String]) -> Void
    let onCancel: () -> Void

    @State private var selection: GroupMemberPickerSelection
    @State private var search = ""
    @State private var name = "新群聊"

    init(mode: Mode, existingMemberCount: Int, candidates: [GroupMemberPickerCandidate], blockingReason: String? = nil,
         onConfirm: @escaping (_ name: String, _ peerIDs: [String]) -> Void, onCancel: @escaping () -> Void) {
        self.mode = mode
        self.candidates = candidates
        self.blockingReason = blockingReason
        self.onConfirm = onConfirm
        self.onCancel = onCancel
        _selection = State(initialValue: GroupMemberPickerSelection(existingMemberCount: existingMemberCount))
    }

    private var confirmedIDs: [String] { selection.confirmedIDs(in: candidates) }
    private var trimmedName: String { name.trimmingCharacters(in: .whitespacesAndNewlines) }
    private var canConfirm: Bool {
        !confirmedIDs.isEmpty && blockingReason == nil && (mode != .create || !trimmedName.isEmpty)
    }
    private var confirmTitle: String {
        let count = confirmedIDs.count
        switch mode {
        case .create: return count == 0 ? "创建并邀请" : "创建并邀请 \(count) 台设备"
        case .add: return count == 0 ? "邀请" : "邀请 \(count) 台设备"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("选择成员").font(.title3.bold())
                    if case .add(let groupName) = mode {
                        Text("添加到「\(groupName)」").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                Spacer()
                Button { onCancel() } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless).help("关闭").accessibilityLabel("关闭选择成员")
            }
            .padding(.horizontal, 20).padding(.top, 18).padding(.bottom, 12)
            Divider()
            HStack(spacing: 0) {
                candidateColumn.frame(width: 340)
                Divider()
                selectedColumn.frame(maxWidth: .infinity)
            }
        }
        .frame(width: 720, height: 520)
    }

    private var candidateColumn: some View {
        let visible = GroupMemberPickerSelection.visible(candidates, matching: search)
        return VStack(alignment: .leading, spacing: 10) {
            TextField("搜索设备名称", text: $search)
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("搜索可邀请设备")
            if candidates.isEmpty {
                emptyText("还没有可选择的设备。先与对方建立连接后再邀请。")
            } else if visible.isEmpty {
                emptyText("没有匹配的设备。")
            } else {
                ScrollView {
                    LazyVStack(spacing: 2) {
                        ForEach(visible) { candidate in candidateRow(candidate) }
                    }
                }
            }
        }
        .padding(16)
    }

    private func candidateRow(_ candidate: GroupMemberPickerCandidate) -> some View {
        let selected = selection.isSelected(candidate)
        let enabled = selection.canToggle(candidate)
        return Button { selection.toggle(candidate) } label: {
            HStack(spacing: 10) {
                Image(systemName: selected ? "checkmark.square.fill" : "square")
                    .font(.system(size: 15))
                    .foregroundStyle(selected ? Color.accentColor : Color.secondary)
                AvatarView(avatarID: candidate.avatarID, size: 32)
                VStack(alignment: .leading, spacing: 2) {
                    Text(candidate.name).font(.system(size: 13, weight: .medium)).lineLimit(1)
                    Text(candidate.availability.label).font(.caption2).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8).padding(.vertical, 6)
            .contentShape(Rectangle())
            .opacity(candidate.availability == .available || candidate.availability == .existingMember ? 1 : 0.45)
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .accessibilityLabel("\(candidate.name)，\(candidate.availability.label)")
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var selectedColumn: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("已选择 \(confirmedIDs.count) · 还可添加 \(selection.remaining)")
                .font(.system(size: 13, weight: .semibold))
            ScrollView {
                if confirmedIDs.isEmpty {
                    emptyText("在左侧勾选要邀请的设备。")
                } else {
                    LazyVStack(alignment: .leading, spacing: 6) {
                        ForEach(confirmedIDs, id: \.self) { id in
                            if let candidate = candidates.first(where: { $0.id == id }) { selectedChip(candidate) }
                        }
                    }
                }
            }
            .frame(maxHeight: .infinity)
            Divider()
            if mode == .create {
                TextField("群名称", text: $name).textFieldStyle(.roundedBorder).accessibilityLabel("群名称")
            }
            Text("对方接受邀请、全部成员确认后才正式加入；群聊最多 \(GroupConversation.maximumMemberCount) 人（含本机）。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                if let blockingReason {
                    Label(blockingReason, systemImage: "exclamationmark.circle")
                        .font(.caption).foregroundStyle(.orange).lineLimit(2)
                }
                Spacer()
                Button("取消") { onCancel() }.keyboardShortcut(.cancelAction)
                Button(confirmTitle) { onConfirm(trimmedName, confirmedIDs) }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canConfirm)
            }
        }
        .padding(16)
    }

    private func selectedChip(_ candidate: GroupMemberPickerCandidate) -> some View {
        HStack(spacing: 8) {
            AvatarView(avatarID: candidate.avatarID, size: 24)
            Text(candidate.name).font(.system(size: 12, weight: .medium)).lineLimit(1)
            Spacer(minLength: 0)
            Button { selection.remove(candidate.id) } label: { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless).foregroundStyle(.secondary)
                .accessibilityLabel("移除 \(candidate.name)")
        }
        .padding(.horizontal, 8).padding(.vertical, 5)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 7))
    }

    private func emptyText(_ text: String) -> some View {
        Text(text).font(.caption).foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 8)
    }
}
