import AppKit
import Combine
import FeigeIdentity
import Foundation
import Network
import UniformTypeIdentifiers

struct DeviceSettings: Codable {
    var deviceName: String
    var receiveDirectory: URL
    var firstRunComplete: Bool
    var setupStep: Int? = nil
    /// Preferences added after 0.3.3 keep their defaults when reading older files.
    var autoCheckUpdates: Bool = true
    var notificationsEnabled: Bool = false
    var backgroundTipSeen: Bool = false
    var connectionTipSeen: Bool = false
    var legacyReviewPending: Bool = false
    var avatarId: String = AvatarID.default
    var peerAvatarIds: [String: String] = [:]
    var probeTargets: [String] = []
    var allowsProbes: Bool = true
    /// Full fingerprints whose inbound connections are approved without a prompt and that are
    /// dialed automatically when discovered. File approval is unaffected.
    var trustedPeers: [String] = []

    static var `default`: DeviceSettings {
        let name = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? FileManager.default.homeDirectoryForCurrentUser
        return DeviceSettings(deviceName: name, receiveDirectory: downloads.appendingPathComponent("Feige Received", isDirectory: true), firstRunComplete: false)
    }

    private enum CodingKeys: String, CodingKey {
        case deviceName, receiveDirectory, firstRunComplete, setupStep
        case autoCheckUpdates, notificationsEnabled, backgroundTipSeen, connectionTipSeen, legacyReviewPending
        case avatarId, peerAvatarIds, probeTargets, allowsProbes, trustedPeers
    }

    /// Memberwise initializer kept explicit because declaring `init(from:)` suppresses synthesis.
    init(deviceName: String, receiveDirectory: URL, firstRunComplete: Bool, setupStep: Int? = nil,
         autoCheckUpdates: Bool = true, notificationsEnabled: Bool = false, backgroundTipSeen: Bool = false,
         connectionTipSeen: Bool = false, legacyReviewPending: Bool = false, avatarId: String = AvatarID.default,
         peerAvatarIds: [String: String] = [:]) {
        self.deviceName = deviceName
        self.receiveDirectory = receiveDirectory
        self.firstRunComplete = firstRunComplete
        self.setupStep = setupStep
        self.autoCheckUpdates = autoCheckUpdates
        self.notificationsEnabled = notificationsEnabled
        self.backgroundTipSeen = backgroundTipSeen
        self.connectionTipSeen = connectionTipSeen
        self.legacyReviewPending = legacyReviewPending
        self.avatarId = AvatarID.normalize(avatarId)
        self.peerAvatarIds = Self.normalizedPeerAvatarIds(peerAvatarIds)
    }

    private static func normalizedPeerAvatarIds(_ values: [String: String]) -> [String: String] {
        let entries = values.compactMap { entry -> (String, String)? in
            guard let fingerprint = AvatarID.normalizeFingerprint(entry.key), AvatarID.isKnown(entry.value) else { return nil }
            return (fingerprint, entry.value)
        }
        return Dictionary(entries.suffix(256), uniquingKeysWith: { _, latest in latest })
    }

    /// Synthesized decoding would fail on every older settings file because missing keys do not
    /// fall back to the member defaults; optional/defaulted fields are decoded individually.
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        deviceName = try values.decode(String.self, forKey: .deviceName)
        receiveDirectory = try values.decode(URL.self, forKey: .receiveDirectory)
        firstRunComplete = try values.decode(Bool.self, forKey: .firstRunComplete)
        setupStep = try values.decodeIfPresent(Int.self, forKey: .setupStep)
        autoCheckUpdates = try values.decodeIfPresent(Bool.self, forKey: .autoCheckUpdates) ?? true
        notificationsEnabled = try values.decodeIfPresent(Bool.self, forKey: .notificationsEnabled) ?? false
        backgroundTipSeen = try values.decodeIfPresent(Bool.self, forKey: .backgroundTipSeen) ?? false
        connectionTipSeen = try values.decodeIfPresent(Bool.self, forKey: .connectionTipSeen) ?? false
        legacyReviewPending = try values.decodeIfPresent(Bool.self, forKey: .legacyReviewPending) ?? false
        avatarId = AvatarID.normalize(try values.decodeIfPresent(String.self, forKey: .avatarId))
        peerAvatarIds = Self.normalizedPeerAvatarIds(try values.decodeIfPresent([String: String].self, forKey: .peerAvatarIds) ?? [:])
        probeTargets = try values.decodeIfPresent([String].self, forKey: .probeTargets) ?? []
        allowsProbes = try values.decodeIfPresent(Bool.self, forKey: .allowsProbes) ?? true
        trustedPeers = Self.normalizedTrustedPeers(try values.decodeIfPresent([String].self, forKey: .trustedPeers) ?? [])
    }

    static func normalizedTrustedPeers(_ values: [String]) -> [String] {
        var seen = Set<String>()
        return values.compactMap(AvatarID.normalizeFingerprint).filter { seen.insert($0).inserted }.suffix(256).map { $0 }
    }
}

enum SettingsError: LocalizedError {
    case invalidDeviceName
    case invalidProbeTarget

    var errorDescription: String? {
        switch self {
        case .invalidDeviceName: return "设备名称为空、过长或包含换行等控制字符，请修改后重试。"
        case .invalidProbeTarget: return "只能填写私网 IPv4 地址、三段网段（如 10.80.1）或 /24–/32 网段，最多 16 项。"
        }
    }
}

struct TransferRecord: Identifiable, Codable {
    enum Direction: String, Codable { case sent, received }
    var id = UUID()
    var fileName: String
    var size: Int64
    var date = Date()
    var direction: Direction
    var peerName: String?
    var savedPath: String?
}

@MainActor
final class TransferModel: ObservableObject, @unchecked Sendable {
    @Published var settings = DeviceSettings.default
    @Published var records: [TransferRecord] = []
    @Published var messages: [ChatMessage] = []
    @Published private(set) var probePortUnavailable = false
    /// Local unread counts keyed by conversation: a peer fingerprint, or `group:<UUID>`.
    @Published var unreadCounts: [String: Int] = [:]
    /// The conversation the main window shows; arrivals there are read while the app is active.
    var visibleConversationKey: String?
    let unreadURL: URL
    @Published private(set) var groups: [GroupConversation] = []
    @Published private(set) var groupMessages: [GroupMessageEnvelope] = []
    @Published private(set) var groupDeliveries: [GroupMessageDelivery] = []
    @Published private(set) var groupFileLocations: [UUID: String] = [:]
    @Published private(set) var pendingGroupFileApprovals: [PendingGroupFileApproval] = []
    @Published private(set) var pendingGroupInvitations: [PendingGroupInvitation] = []
    @Published private(set) var pendingGroupLeaveRequests: [PendingGroupLeaveRequest] = []
    @Published private(set) var pendingGroupLeaveGroupIDs: Set<UUID> = []
    @Published private(set) var hiddenConversationIDs: Set<String> = [] { didSet { updateDockBadge() } }
    @Published var connectedPeerID = ""
    @Published var isSendingText = false
    @Published private(set) var connectedPeerIDs: Set<String> = []
    @Published var isReceiving = false
    @Published var listenerPort: UInt16?
    @Published var nearbyDevices: [LANDevice] = [] {
        didSet {
            if isIdentityReady {
                connectEligibleGroupPeers()
                connectTrustedPeers()
            }
        }
    }
    @Published var localAddress = LANAddress.currentIPv4()
    @Published var identityFingerprint = "设备身份未加载"
    @Published var isPreparingIdentity = false
    @Published private(set) var isIdentityReady = false
    @Published private(set) var identityPreparationError: String?
    @Published var errorMessage: String?
    @Published private(set) var shouldSuggestNetworkHelp = false
    @Published var isFirstRun = false
    @Published var discoveryState = "正在启动设备发现…"
    @Published private(set) var isRefreshingPeers = false
    @Published private(set) var lastPeerRefreshAt: Date?
    @Published private(set) var peerRefreshFailed = false
    @Published var connectionState = "未连接"
    @Published var isConnecting = false
    @Published var isConnected = false
    @Published var connectedPeerName = ""
    @Published var pendingConnectionPeerName: String?
    @Published var pendingConnectionPeerFingerprint: String?
    @Published var isSending = false
    @Published var isReceivingFile = false
    @Published var transferProgress: Double = 0
    @Published var transferStatus = ""
    @Published var receiveProgress: Double = 0
    @Published var receiveStatus = ""
    @Published var canRetry = false
    @Published var queueCompleted = 0
    @Published var queueCount = 0
    @Published var drafts: [String: String] = [:]
    @Published private(set) var notificationInvitationPending = false
    @Published private(set) var firstTransferNoticePending = false
    @Published private(set) var backgroundNoticePending = false
    @Published private(set) var notifierStatus = ""
    @Published private(set) var notifierAvailable = false
    @Published private(set) var updates: UpdateController?
    var isBusy: Bool { isConnecting || !sendingPeers.isEmpty || !groupFileWorkers.isEmpty || !receivingPeers.isEmpty }

    /// Transfers or approvals in flight across all peer connections.
    var hasActiveWork: Bool {
        !sendingPeers.isEmpty || !groupFileWorkers.isEmpty || !receivingPeers.isEmpty || !sendingTextPeers.isEmpty || !approvalOrder.isEmpty ||
        !groupFileDecisionContinuations.isEmpty || !pendingGroupTextAckWaiters.isEmpty ||
        !pendingGroupControlWaiters.isEmpty
    }

    private let identityLoader: @Sendable () throws -> DeviceIdentity
    private let settingsURL: URL
    private let historyURL: URL
    private let messagesURL: URL
    private let draftsURL: URL
    private let supportDirectory: URL
    private let lan = LANService()
    let notifier = TransferNotifier()
    private var networkMonitor: NWPathMonitor?
    private let networkQueue = DispatchQueue(label: "com.feige.network-monitor")
    private var networkRefreshTask: Task<Void, Never>?
    private var networkAvailable: Bool?
    private var started = false
    private var preparedIdentity: DeviceIdentity?
    private var identityPreparationTask: Task<DeviceIdentity, Error>?
    private var identityPreparationID = UUID()
    private enum ConnectionPurpose { case direct; case group(UUID) }
    private var activeConnectionTokens: Set<UUID> = []
    private var purposeByToken: [UUID: ConnectionPurpose] = [:]
    // Only attempts the user started report failures in an alert; automatic redials just update status.
    private var userInitiatedConnectionTokens: Set<UUID> = []
    private var directConnectionTokens: Set<UUID> = []
    private var directPeerIDs: Set<String> = []
    private var groupPeerIDs: [UUID: Set<String>] = [:]
    private var groupOnlyPeerIDs: Set<String> { Set(channelsByPeer.keys).subtracting(directPeerIDs) }
    private var pendingGroupConnectionCount: Int {
        purposeByToken.values.reduce(0) { count, purpose in
            if case .group = purpose { return count + 1 }; return count
        }
    }
    @Published private(set) var pendingGroupCommits: [UUID: PendingGroupCommit] = [:]
    @Published private(set) var pendingGroupPreparations: [UUID: PendingGroupPreparation] = [:]
    @Published private(set) var pendingGroupAborts: [UUID: PendingGroupAbort] = [:]
    private var groupAbortTombstones: [UUID: GroupAbortTombstone] = [:]
    @Published private(set) var retryingGroupCommits: Set<UUID> = []
    @Published private(set) var retryingGroupPreparations: Set<UUID> = []
    @Published private(set) var retryingGroupAborts: Set<UUID> = []
    private var pendingGroupControlWaiters: [GroupControlWaitKey: GroupResponseSlot<GroupControlMessage>] = [:]
    private var pendingGroupTextAckWaiters: [GroupAckKey: GroupResponseSlot<GroupTextAck>] = [:]
    private var activeGroupTextSends: Set<GroupAckKey> = []
    private var pendingGroupSetups: [UUID: GroupConversation] = [:]
    private var resolvingGroupLeavePeers: Set<String> = []
    private var pendingGroupMembershipChanges: [UUID: GroupControlMessage] = [:]
    private var acceptedGroupInvitations: [UUID: GroupControlMessage] = [:]
    private var pendingLeaveRequestsByGroup: [UUID: Set<UUID>] = [:]
    private var pendingGroupLeaveMessagesByGroup: [UUID: [UUID: GroupControlMessage]] = [:]
    private var completedGroupLeaveReplies: [UUID: GroupControlMessage] = [:]
    private var sendingGroupLeaveRequests: [UUID: UUID] = [:]
    private var sendingGroupLeaveReplies: [UUID: UUID] = [:]
    private var archivedPendingGroupLeaveMessages: [GroupControlMessage] {
        pendingGroupLeaveMessagesByGroup.values.flatMap { $0.values }
            .sorted {
                if $0.groupID != $1.groupID { return $0.groupID.uuidString < $1.groupID.uuidString }
                return $0.eventID.uuidString < $1.eventID.uuidString
            }
    }
    private var archivedCompletedGroupLeaveReplies: [GroupControlMessage] {
        completedGroupLeaveReplies.values.sorted { $0.eventID.uuidString < $1.eventID.uuidString }
    }
    private var pendingGroupLeaveReconnectTasks: [String: Task<Void, Never>] = [:]
    private var pendingGroupLeaveReconnectAttempts: [String: Int] = [:]
    private var peerTokens: [String: UUID] = [:]
    private var channelsByPeer: [String: FileConnection] = [:]
    private var reconnectReplacementTokens: [String: UUID] = [:]
    private var readerTasksByPeer: [String: Task<Void, Never>] = [:]
    private var transportByToken: [UUID: NetworkByteStream] = [:]
    private var connectionTasksByToken: [UUID: Task<Void, Never>] = [:]
    private var serviceNameByToken: [UUID: String] = [:]
    private var connectingServiceNames: Set<String> = []
    private var approvalContinuations: [UUID: CheckedContinuation<Bool, Error>] = [:]
    private var approvalPeerNames: [UUID: String] = [:]
    private var approvalPeerFingerprints: [UUID: String] = [:]
    private var approvalOrder: [UUID] = []
    private struct TrustedDialState {
        var failures = 0
        var nextAttempt = Date.distantPast
        /// Set by a declined dial or a manual disconnect; cleared by a manual connect.
        var suppressed = false
        var firstSeen: Date?
    }
    private var trustedDialStates: [String: TrustedDialState] = [:]
    private var trustedDialTokens: [UUID: String] = [:]
    private var trustedDialTimer: Task<Void, Never>?
    private var isAutoDialing = false
    private static let trustedDialBackoff: [TimeInterval] = [30, 60, 300, 1800]
    /// Pending file confirmations are isolated by peer and transfer ID.
    private struct GroupFileJob { let envelope: GroupMessageEnvelope; let url: URL }
    private var groupFileQueues: [String: [GroupFileJob]] = [:]
    private var groupFileWorkers: Set<String> = []
    private var activeGroupFileSends: Set<GroupAckKey> = []
    private struct FileDecisionKey: Hashable { let peerID: String; let transferID: UUID }
    private var sendingPeers: Set<String> = []
    private var sendingTextPeers: Set<String> = []
    private var sendTasksByPeer: [String: Task<Void, Never>] = [:]
    private var textTasksByPeer: [String: Task<Void, Never>] = [:]
    private var retryURLsByPeer: [String: [URL]] = [:]
    private var receivingPeers: Set<String> = []
    private var fileDecisions: [FileDecisionKey: CheckedContinuation<Bool, Never>] = [:]
    private var groupFileDecisionContinuations: [FileDecisionKey: CheckedContinuation<Bool, Never>] = [:]
    /// Time allowed to answer a file confirmation, matching the Windows client.
    static let fileApprovalTimeout: Duration = .seconds(120)
    private var channel: FileConnection?
    private var connectionTask: Task<Void, Never>?
    private var readerTask: Task<Void, Never>?
    private var sendTask: Task<Void, Never>?
    private var textTask: Task<Void, Never>?
    private var retryURLs: [URL] = []
    private var conversationArchiveLoadFailed = false

    init(supportDirectory: URL? = nil, identityService: String = EncryptedIdentityStore.defaultServiceIdentifier,
         /// Kept for source compatibility with callers that also pass an explicit loader; unused when a
         /// loader is supplied, because the loader owns identity creation in that case.
         identityStore: DeviceIdentityStore? = nil,
         identityLoader: (@Sendable () throws -> DeviceIdentity)? = nil,
         updateSource: UpdateSource? = nil, updateVersion: AppVersionInfo = .current,
         makeUpdates: Bool = true) {
        let support = supportDirectory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FeigeTransfer", isDirectory: true)
        try? FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        self.supportDirectory = support
        // The private key lives in an encrypted file plus a non-ACL Keychain wrapping key, so start-up,
        // signing and device fingerprint lookups never open the "Keychain wants your password" prompt.
        let encryptedStore = EncryptedIdentityStore(directory: support.appendingPathComponent("Identity", isDirectory: true),
                                                    serviceIdentifier: identityService)
        self.identityLoader = identityLoader ?? {
            // Keep an upgraded install's existing fingerprint before falling back to a new key.
            encryptedStore.migrateLegacyKeyIfNeeded()
            return try encryptedStore.loadOrCreate()
        }
        settingsURL = support.appendingPathComponent("settings.json")
        historyURL = support.appendingPathComponent("history.json")
        messagesURL = support.appendingPathComponent("messages.json")
        draftsURL = support.appendingPathComponent("drafts.json")
        unreadURL = support.appendingPathComponent("unread.json")
        load()
        loadUnreadCounts()
        drafts = (try? Data(contentsOf: draftsURL)).flatMap { try? JSONDecoder().decode([String: String].self, from: $0) } ?? [:]
        if makeUpdates {
            let controller = UpdateController(supportDirectory: support, version: updateVersion, source: updateSource)
            updates = controller
            Task { await self.refreshNotifierStatus() }
        }
        notifierStatus = notifier.status
        notifierAvailable = notifier.isAvailable
        lan.onDevices = { [weak self] devices in
            self?.nearbyDevices = devices
            if self?.isRefreshingPeers == true { self?.peerRefreshFailed = false }
        }
        lan.onStatus = { [weak self] status, port in
            self?.discoveryState = status
            self?.listenerPort = port
            self?.isReceiving = port != nil
        }
        lan.onConnection = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            self.accept(connection)
        }
        lan.onProbePortUnavailable = { [weak self] in self?.probePortUnavailable = true }
        startNetworkMonitoring()
        if settings.notificationsEnabled {
            Task { await self.refreshNotifierStatus() }
        }
    }

    func start() async {
        guard settings.firstRunComplete else {
            isFirstRun = true
            return
        }
        await prepareIdentity()
        startOutboxExpiryTimer()
        if settings.firstRunComplete {
            updates?.start()
            updates?.preferencesChanged(autoCheckEnabled: settings.autoCheckUpdates, setupEnded: true)
        }
    }

    func prepareIdentityForSetup() async {
        await prepareIdentity()
    }

    private func prepareIdentity() async {
        guard !started, !isPreparingIdentity else { return }
        started = true
        let preparationID = UUID()
        identityPreparationID = preparationID
        isPreparingIdentity = true
        identityPreparationError = nil
        discoveryState = "正在准备设备身份，请完成系统钥匙串授权…"
        let loader = identityLoader
        let preparation = Task.detached(priority: .userInitiated) {
            let identity = try loader()
            try Task.checkCancellation()
            try identity.validateSigningAccess()
            try Task.checkCancellation()
            return identity
        }
        identityPreparationTask = preparation
        defer {
            if identityPreparationID == preparationID {
                isPreparingIdentity = false
                identityPreparationTask = nil
            }
        }
        do {
            let identity = try await preparation.value
            guard started, identityPreparationID == preparationID else { return }
            preparedIdentity = identity
            identityFingerprint = identity.fingerprint
            isIdentityReady = true
            if settings.firstRunComplete {
                refreshDiscovery()
                connectEligibleGroupPeers()
            }
        } catch {
            guard identityPreparationID == preparationID else { return }
            started = false
            preparedIdentity = nil
            isIdentityReady = false
            connectionState = "设备身份不可用"
            discoveryState = "系统授权未完成，请点击重新授权"
            identityPreparationError = error.localizedDescription
        }
    }

    func saveSettings(deviceName: String, receiveDirectory: URL, avatarId: String? = nil) throws {
        let trimmed = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 48, trimmed.utf8.count <= 128,
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw SettingsError.invalidDeviceName
        }
        try validateReceiveDirectory(receiveDirectory)
        try cleanupOrphanedTransfers(in: receiveDirectory)
        var updated = settings
        updated.deviceName = trimmed
        updated.receiveDirectory = receiveDirectory
        updated.avatarId = AvatarID.normalize(avatarId ?? updated.avatarId)
        updated.firstRunComplete = true
        updated.setupStep = nil
        try persist(updated, to: settingsURL)
        settings = updated
        isFirstRun = false
        if started && isIdentityReady { refreshDiscovery() }
    }

    func saveSetupDraft(deviceName: String, receiveDirectory: URL, nextStep: Int, avatarId: String? = nil) throws {
        let trimmed = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 48, trimmed.utf8.count <= 128,
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw SettingsError.invalidDeviceName
        }
        try validateReceiveDirectory(receiveDirectory)
        var updated = settings
        updated.deviceName = trimmed
        updated.receiveDirectory = receiveDirectory
        updated.avatarId = AvatarID.normalize(avatarId ?? updated.avatarId)
        updated.firstRunComplete = false
        updated.setupStep = nextStep
        try persist(updated, to: settingsURL)
        settings = updated
    }

    func saveAvatarId(_ avatarId: String) throws {
        let normalized = AvatarID.normalize(avatarId)
        guard settings.avatarId != normalized else { return }
        var updated = settings
        updated.avatarId = normalized
        try persist(updated, to: settingsURL)
        settings = updated
        if started && isIdentityReady { refreshDiscovery() }
    }

    func avatarId(for fingerprint: String, hint: String? = nil) -> String {
        if let normalized = AvatarID.normalizeFingerprint(fingerprint),
           let saved = settings.peerAvatarIds[normalized], AvatarID.isKnown(saved) {
            return saved
        }
        return AvatarID.normalize(hint)
    }

    /// Called only after an authenticated connection reports its full device fingerprint.
    private func cacheDiscoveredAvatar(for peerFingerprint: String) {
        guard let fingerprint = AvatarID.normalizeFingerprint(peerFingerprint),
              let device = nearbyDevices.first(where: { AvatarID.normalizeFingerprint($0.fingerprintHint) == fingerprint }),
              let avatarId = device.avatarId, AvatarID.isKnown(avatarId),
              settings.peerAvatarIds[fingerprint] != avatarId else { return }
        var updated = settings
        updated.peerAvatarIds[fingerprint] = avatarId
        if updated.peerAvatarIds.count > 256 {
            let keysToRemove = updated.peerAvatarIds.keys.sorted().filter { $0 != fingerprint }
                .prefix(updated.peerAvatarIds.count - 256)
            for key in keysToRemove { updated.peerAvatarIds.removeValue(forKey: key) }
        }
        do {
            try persist(updated, to: settingsURL)
            settings = updated
        } catch { }
    }

    func isGroupLeaveRequestPending(_ groupID: UUID) -> Bool {
        pendingGroupLeaveGroupIDs.contains(groupID)
    }

    private func refreshPendingGroupLeaveGroupIDs() {
        pendingGroupLeaveGroupIDs = Set(pendingGroupLeaveMessagesByGroup.compactMap { entry in
            entry.value.isEmpty ? nil : entry.key
        })
    }

    func deferFirstRunSetup() { isFirstRun = false }

    // MARK: preferences

    func setAutoCheckUpdates(_ enabled: Bool) throws {
        var updated = settings
        updated.autoCheckUpdates = enabled
        try persist(updated, to: settingsURL)
        settings = updated
        updates?.preferencesChanged(autoCheckEnabled: enabled, setupEnded: settings.firstRunComplete)
    }

    /// Persists the preference first, then asks macOS for permission. Failure keeps the old value.
    @discardableResult
    func enableNotifications() async throws -> Bool {
        let granted = await notifier.requestAuthorization()
        var updated = settings
        updated.notificationsEnabled = granted
        try persist(updated, to: settingsURL)
        settings = updated
        notificationInvitationPending = false
        await refreshNotifierStatus()
        return granted
    }

    func disableNotifications() throws {
        var updated = settings
        updated.notificationsEnabled = false
        try persist(updated, to: settingsURL)
        settings = updated
    }

    func refreshNotifierStatus() async {
        await notifier.refreshStatus()
        notifierStatus = notifier.status
        notifierAvailable = notifier.isAvailable
    }

    func sendTestNotification() async {
        await refreshNotifierStatus()
        notifier.show(title: "飞鸽快传测试通知", body: "能看到这条说明系统通知工作正常。")
    }

    func dismissNotificationInvitation() { notificationInvitationPending = false }
    func dismissTransferNotice() { firstTransferNoticePending = false }
    func dismissBackgroundNotice() { backgroundNoticePending = false }
    /// Clears the "原有资料已保留" tip. A failed save keeps the tip dismissed for this run only and
    /// is recorded in the log; the Windows client surfaces the save error instead.
    func dismissLegacyReviewQuietly() {
        guard settings.legacyReviewPending else { return }
        var updated = settings
        updated.legacyReviewPending = false
        settings = updated
        do { try persist(updated, to: settingsURL) }
        catch { AppLogFile.write("settings: legacy tip save failed \(type(of: error))", in: supportDirectory) }
    }

    /// Records that the first-connection tip was shown; failures keep the in-memory value only.
    func markConnectionTipSeen() {
        guard !settings.connectionTipSeen else { return }
        var updated = settings
        updated.connectionTipSeen = true
        try? persist(updated, to: settingsURL)
        settings = updated
    }

    /// Closing the window keeps the app running; explain that once, the way Windows does.
    func markBackgroundNotice() {
        guard !settings.backgroundTipSeen else { return }
        var updated = settings
        updated.backgroundTipSeen = true
        try? persist(updated, to: settingsURL)
        settings = updated
        backgroundNoticePending = true
        if notifier.isAvailable {
            notifier.show(title: "飞鸽快传仍在后台运行", body: "可从 Dock 图标或“文件 → 显示主窗口”重新打开；停止运行请按 ⌘Q。")
        }
    }

    // MARK: drafts

    func setDraft(_ text: String, for peerID: String) {
        let key = peerID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        drafts[key] = text.isEmpty ? nil : text
        saveDrafts()
    }

    func clearDraft(for peerID: String) {
        drafts[peerID] = nil
        saveDrafts()
    }

    private func saveDrafts() {
        do {
            let data = try JSONEncoder().encode(drafts.filter { !$0.value.isEmpty })
            try data.write(to: draftsURL, options: .atomic)
        } catch {
            AppLogFile.write("draft save: \(type(of: error))", in: supportDirectory)
        }
    }

    // MARK: network changes

    private func startNetworkMonitoring() {
        guard networkMonitor == nil else { return }
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            let available = path.status == .satisfied
            Task { @MainActor in self?.handleNetworkChange(available: available) }
        }
        monitor.start(queue: networkQueue)
        networkMonitor = monitor
    }

    /// Discards stale channels on network path updates, then rebuilds discovery.
    /// Conversation history and drafts stay local; only unfinished messages are marked for retry.
    private func handleNetworkChange(available: Bool) {
        let previouslyAvailable = networkAvailable
        networkAvailable = available
        guard started, settings.firstRunComplete, isIdentityReady else { return }

        if !available {
            networkRefreshTask?.cancel()
            networkRefreshTask = nil
            if previouslyAvailable != false {
                disconnectAll(failureReason: "网络中断，未完成的消息可重试。", preservePendingRequests: true)
            }
            discoveryState = "网络不可用，等待网络恢复"
            return
        }

        // NWPathMonitor reports path changes even when the interface name stays the same.
        if previouslyAvailable != nil {
            disconnectAll(failureReason: "网络已变化，未完成的消息可重试。", preservePendingRequests: true)
        }
        networkRefreshTask?.cancel()
        networkRefreshTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(750))
            guard let self, !Task.isCancelled else { return }
            self.localAddress = LANAddress.currentIPv4()
            self.refreshDiscovery()
            self.connectEligibleGroupPeers()
            AppLogFile.write("network: discovery refreshed", in: self.supportDirectory)
        }
    }

    private func validateReceiveDirectory(_ directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let probe = directory.appendingPathComponent(".feige-write-check-\(UUID().uuidString)")
        try Data().write(to: probe, options: .withoutOverwriting)
        try FileManager.default.removeItem(at: probe)
    }

    private func cleanupOrphanedTransfers(in directory: URL) throws {
        _ = try TemporaryFileSinkRecovery.removeOrphanedFiles(in: directory)
        ResumableStorageCleanup.removeExpired(in: directory)
    }

    func refreshDiscovery() {
        guard settings.firstRunComplete else { return }
        guard isIdentityReady else {
            if !isPreparingIdentity { Task { await start() } }
            return
        }
        do { try lan.start(deviceName: settings.deviceName, fingerprint: identityFingerprint, avatarId: settings.avatarId) }
        catch { errorMessage = "无法启动局域网服务：\(error.localizedDescription)" }
    }

    /// Replaces the saved subnets and restarts discovery so the first round runs at once.
    func saveProbeSettings(targets: [String], allowsProbes: Bool) throws {
        let cleaned = targets.map(ProbeTargets.normalized).filter { !$0.isEmpty }
        guard ProbeTargets.hosts(in: cleaned) != nil else { throw SettingsError.invalidProbeTarget }
        var updated = settings
        updated.probeTargets = cleaned
        updated.allowsProbes = allowsProbes
        try persist(updated, to: settingsURL)
        settings = updated
        probePortUnavailable = false
        if started { refreshDiscovery() }
    }

    func refreshPeers() {
        guard !isRefreshingPeers else { return }
        guard settings.firstRunComplete, isIdentityReady else {
            refreshDiscovery()
            return
        }
        isRefreshingPeers = true
        peerRefreshFailed = false
        refreshDiscovery()
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard let self else { return }
            self.lastPeerRefreshAt = Date()
            self.isRefreshingPeers = false
            if self.discoveryState.localizedCaseInsensitiveContains("失败") || self.discoveryState.localizedCaseInsensitiveContains("不可用") {
                self.peerRefreshFailed = true
            }
        }
    }

    func stopDiscovery() {
        lan.stop()
        isReceiving = false
        listenerPort = nil
        nearbyDevices = []
        discoveryState = "设备发现已停止"
    }

    func chooseReceiveFolder() -> URL? {
        let panel = NSOpenPanel()
        panel.title = "选择接收文件夹"
        panel.prompt = "选择此文件夹"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        return panel.runModal() == .OK ? panel.url : nil
    }

    func chooseAndSendFiles(imagesOnly: Bool = false, to peerID: String? = nil) {
        let panel = NSOpenPanel()
        panel.title = imagesOnly ? "选择要发送的图片" : "选择要发送的文件或文件夹"
        if imagesOnly { panel.allowedContentTypes = [.image] }
        panel.canChooseFiles = true
        panel.canChooseDirectories = !imagesOnly
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { startSending(panel.urls, to: peerID ?? connectedPeerID) }
    }

    func chooseAndSendGroupFiles(groupID: UUID, imagesOnly: Bool = false) {
        guard canSendGroupText(groupID) else { return }
        let panel = NSOpenPanel()
        panel.title = imagesOnly ? "选择要发送到群聊的图片" : "选择要发送到群聊的文件"
        if imagesOnly { panel.allowedContentTypes = [.image] }
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { startSendingGroupFiles(panel.urls, groupID: groupID) }
    }

    func startSendingGroupFiles(_ urls: [URL], groupID: UUID) {
        guard let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity, canSendGroupText(groupID), !urls.isEmpty else { return }
        var envelopes: [(GroupMessageEnvelope, URL)] = []
        for url in urls where url.isFileURL {
            do {
                let values = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
                guard values.isRegularFile == true, let size = values.fileSize, size >= 0 else { continue }
                let transferID = UUID()
                let metadata = try GroupFileMetadata(transferID: transferID, fileName: url.lastPathComponent,
                                                     size: UInt64(size))
                let envelope = try GroupMessageEnvelope(groupID: group.id, senderFingerprint: identity.fingerprint,
                    memberRevision: group.revision, kind: Self.isImage(url) ? .image : .file,
                    fileMetadata: metadata)
                let delivery = try GroupMessageDelivery(envelope: envelope, group: group)
                groupMessages.append(envelope)
                groupDeliveries.append(delivery)
                groupFileLocations[transferID] = url.path
                envelopes.append((envelope, url))
            } catch { errorMessage = error.localizedDescription }
        }
        do { try persistGroupState() }
        catch { errorMessage = "保存群文件消息失败：\(error.localizedDescription)"; return }
        for recipient in group.members where recipient.fingerprint != identity.fingerprint {
            for (envelope, url) in envelopes { enqueueGroupFile(envelope, from: url, to: recipient.fingerprint) }
        }
    }

    private func enqueueGroupFile(_ envelope: GroupMessageEnvelope, from url: URL, to peerID: String) {
        groupFileQueues[peerID, default: []].append(GroupFileJob(envelope: envelope, url: url))
        guard groupFileWorkers.insert(peerID).inserted else { return }
        Task { [weak self] in await self?.drainGroupFileQueue(for: peerID) }
    }

    private func drainGroupFileQueue(for peerID: String) async {
        while !Task.isCancelled {
            guard var queue = groupFileQueues[peerID], !queue.isEmpty else { break }
            let job = queue.removeFirst()
            groupFileQueues[peerID] = queue
            if let directSend = sendTasksByPeer[peerID] { await directSend.value }
            await transmitGroupFile(job.envelope, from: job.url, to: peerID)
        }
        groupFileQueues.removeValue(forKey: peerID)
        groupFileWorkers.remove(peerID)
    }


    func manualDevice(host: String, port: String) throws -> LANDevice {
        let trimmed = host.trimmingCharacters(in: .whitespacesAndNewlines)
        let digits = port.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let number = UInt16(digits), number > 0 else { throw NetworkTransportError.invalidPort }
        // Manual entry must be a literal local/private address: no host names, public ranges or brackets.
        guard !trimmed.isEmpty, !trimmed.hasPrefix("["), trimmed.contains(":") || trimmed.allSatisfy({ $0.isNumber || $0 == "." }) else {
            throw NetworkTransportError.invalidHost
        }
        // Require a canonical literal so "999.1.1.1" cannot be reinterpreted later.
        if trimmed.contains(":") {
            guard IPv6Address(trimmed) != nil else { throw NetworkTransportError.invalidHost }
        } else {
            guard IPv4Address(trimmed) != nil else { throw NetworkTransportError.invalidHost }
        }
        let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(trimmed), port: NWEndpoint.Port(rawValue: number)!)
        guard LANEndpointPolicy.allows(endpoint) else { throw NetworkTransportError.invalidHost }
        return LANDevice(serviceName: trimmed, displayName: trimmed, endpoint: endpoint)
    }

    private func peerServiceNames(for peerID: String) -> Set<String> {
        let shortFingerprint = String(peerID.replacingOccurrences(of: " ", with: "").prefix(20))
        return ["Feige-\(shortFingerprint)", "Feige4-\(shortFingerprint)"]
    }

    private func nearbyDevice(for peerID: String, requiringV4: Bool = false) -> LANDevice? {
        let names = peerServiceNames(for: peerID)
        let candidates = nearbyDevices.filter {
            names.contains($0.serviceName) && (!requiringV4 || $0.protocolVersions.contains(4) || $0.protocolVersions.contains(5))
        }
        return candidates.first(where: { $0.protocolVersions.contains(5) }) ??
            candidates.first(where: { $0.protocolVersions.contains(4) }) ?? candidates.first
    }

    private func preferredGroupProtocolVersion(for device: LANDevice) -> UInt16 {
        device.protocolVersions.contains(5) ? 5 : 4
    }

    private func isConnectionPending(for peerID: String) -> Bool {
        peerServiceNames(for: peerID).contains { connectingServiceNames.contains($0) }
    }

    private func connectEligibleGroupPeers() {
        guard let localID = preparedIdentity?.fingerprint, settings.firstRunComplete, isIdentityReady else { return }
        for group in groups where group.members.contains(where: { $0.fingerprint == localID }) {
            startGroupMeshConnections(groupID: group.id, roster: group.members)
        }
        for message in pendingGroupMembershipChanges.values {
            if let roster = message.roster { startGroupMeshConnections(groupID: message.groupID, roster: roster) }
        }
        for pending in pendingGroupCommits.values {
            startGroupMeshConnections(groupID: pending.group.id, roster: pending.group.members)
            for target in pending.targets where channelsByPeer[target.peerID] == nil {
                guard let device = nearbyDevice(for: target.peerID, requiringV4: true),
                      !connectingServiceNames.contains(device.serviceName), activeConnectionTokens.count < 39,
                      groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19 else { continue }
                connectingServiceNames.insert(device.serviceName)
                beginConnection(NetworkByteStream(endpoint: device.endpoint), role: .initiator, pendingFiles: [],
                                serviceName: device.serviceName, purpose: .group(pending.group.id), protocolVersion: preferredGroupProtocolVersion(for: device))
            }
        }
        for pending in pendingGroupPreparations.values {
            startGroupMeshConnections(groupID: pending.group.id, roster: pending.group.members)
            for target in pending.prepareTargets where channelsByPeer[target.peerID] == nil {
                guard let device = nearbyDevice(for: target.peerID, requiringV4: true),
                      !connectingServiceNames.contains(device.serviceName), activeConnectionTokens.count < 39,
                      groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19 else { continue }
                connectingServiceNames.insert(device.serviceName)
                beginConnection(NetworkByteStream(endpoint: device.endpoint), role: .initiator, pendingFiles: [],
                                serviceName: device.serviceName, purpose: .group(pending.group.id), protocolVersion: preferredGroupProtocolVersion(for: device))
            }
        }
        for request in pendingGroupLeaveRequests {
            let peerID = request.message.targetFingerprint ?? ""
            guard !peerID.isEmpty, channelsByPeer[peerID] == nil,
                  let device = nearbyDevice(for: peerID, requiringV4: true),
                  !connectingServiceNames.contains(device.serviceName), activeConnectionTokens.count < 39,
                  groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19 else { continue }
            connectingServiceNames.insert(device.serviceName)
            beginConnection(NetworkByteStream(endpoint: device.endpoint), role: .initiator, pendingFiles: [],
                            serviceName: device.serviceName, purpose: .group(request.message.groupID), protocolVersion: preferredGroupProtocolVersion(for: device))
        }
        for (groupID, messages) in pendingGroupLeaveMessagesByGroup {
            guard let group = groups.first(where: { $0.id == groupID }),
                  group.ownerFingerprint != localID,
                  messages.values.contains(where: { $0.ownerFingerprint == group.ownerFingerprint }),
                  channelsByPeer[group.ownerFingerprint] == nil,
                  let device = nearbyDevice(for: group.ownerFingerprint, requiringV4: true),
                  !connectingServiceNames.contains(device.serviceName), activeConnectionTokens.count < 39,
                  groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19 else { continue }
            connectingServiceNames.insert(device.serviceName)
            beginConnection(NetworkByteStream(endpoint: device.endpoint), role: .initiator, pendingFiles: [],
                            serviceName: device.serviceName, purpose: .group(groupID),
                            protocolVersion: preferredGroupProtocolVersion(for: device))
        }
    }

    private func startGroupMeshConnections(groupID: UUID, roster: [GroupConversationMember]) {
        guard let localID = preparedIdentity?.fingerprint, settings.firstRunComplete, isIdentityReady else { return }
        for member in roster where localID < member.fingerprint && channelsByPeer[member.fingerprint] == nil {
            guard let device = nearbyDevice(for: member.fingerprint, requiringV4: true),
                  !connectingServiceNames.contains(device.serviceName), activeConnectionTokens.count < 39,
                  groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19 else { continue }
            connectingServiceNames.insert(device.serviceName)
            beginConnection(NetworkByteStream(endpoint: device.endpoint), role: .initiator, pendingFiles: [],
                            serviceName: device.serviceName, purpose: .group(groupID), protocolVersion: preferredGroupProtocolVersion(for: device))
        }
    }

    private func waitForGroupMesh(groupID: UUID, roster: [GroupConversationMember], timeout: Duration = .seconds(90)) async -> Bool {
        guard let localID = preparedIdentity?.fingerprint else { return false }
        let deadline = ContinuousClock.now + timeout
        while ContinuousClock.now < deadline {
            startGroupMeshConnections(groupID: groupID, roster: roster)
            let allConnected = roster.filter { $0.fingerprint != localID }.allSatisfy {
                (channelsByPeer[$0.fingerprint]?.protocolVersion ?? 0) >= 4
            }
            if allConnected { return true }
            do { try await Task.sleep(for: .milliseconds(50)) } catch { return false }
        }
        return false
    }

    private func isKnownGroupMember(_ peerID: String, groupID: UUID) -> Bool {
        if let group = groups.first(where: { $0.id == groupID }),
           group.members.contains(where: { $0.fingerprint == peerID }) || group.previousOwnerFingerprint == peerID {
            return true
        }
        if pendingGroupSetups[groupID]?.members.contains(where: { $0.fingerprint == peerID }) == true { return true }
        if pendingGroupMembershipChanges[groupID]?.roster?.contains(where: { $0.fingerprint == peerID }) == true { return true }
        if pendingGroupLeaveRequests.contains(where: {
            $0.message.groupID == groupID && $0.message.targetFingerprint == peerID
        }) { return true }
        if completedGroupLeaveReplies.values.contains(where: {
            $0.groupID == groupID && $0.targetFingerprint == peerID
        }) { return true }
        return acceptedGroupInvitations.values.contains {
            $0.groupID == groupID && $0.roster?.contains(where: { $0.fingerprint == peerID }) == true
        }
    }

    func canReconnectGroupMember(_ groupID: UUID, fingerprint: String) -> Bool {
        guard let group = groups.first(where: { $0.id == groupID }),
              let localID = preparedIdentity?.fingerprint,
              fingerprint != localID,
              group.members.contains(where: { $0.fingerprint == fingerprint }) else { return false }
        if channelsByPeer[fingerprint] != nil { return false }
        return nearbyDevice(for: fingerprint, requiringV4: true) != nil && !isConnectionPending(for: fingerprint) &&
            activeConnectionTokens.count < 39 && groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19
    }

    func reconnectGroupMember(_ groupID: UUID, fingerprint: String) {
        guard canReconnectGroupMember(groupID, fingerprint: fingerprint),
              let group = groups.first(where: { $0.id == groupID }),
              let device = nearbyDevice(for: fingerprint, requiringV4: true) else { return }
        guard settings.firstRunComplete, isIdentityReady else { isFirstRun = true; return }
        connectingServiceNames.insert(device.serviceName)
        beginConnection(NetworkByteStream(endpoint: device.endpoint), role: .initiator, pendingFiles: [],
                        serviceName: device.serviceName, purpose: .group(group.id), protocolVersion: preferredGroupProtocolVersion(for: device),
                        userInitiated: true)
    }

    func requestConnection(to device: LANDevice, pendingFiles: [URL] = []) {
        let selectedVersion: UInt16 = device.protocolVersions.contains(6) ? 6 :
            device.protocolVersions.contains(5) ? 5 : device.protocolVersions.contains(4) ? 4 : 3
        requestConnection(to: device, protocolVersion: selectedVersion, pendingFiles: pendingFiles)
    }

    func requestConnection(to device: LANDevice, protocolVersion: UInt16, pendingFiles: [URL] = []) {
        guard (3...6).contains(protocolVersion),
              device.protocolVersions.contains(protocolVersion),
              !connectingServiceNames.contains(device.serviceName), activeConnectionTokens.count < 39,
              directPeerIDs.count + directConnectionTokens.count < 20 else { return }
        guard settings.firstRunComplete else { isFirstRun = true; return }
        guard isIdentityReady else { errorMessage = "请先完成启动时的系统钥匙串授权，再连接设备。"; return }
        if !isAutoDialing, let peerID = AvatarID.normalizeFingerprint(device.fingerprintHint) {
            trustedDialStates[peerID] = nil
        }
        shouldSuggestNetworkHelp = false
        connectingServiceNames.insert(device.serviceName)
        let stream = NetworkByteStream(endpoint: device.endpoint)
        beginConnection(stream, role: .initiator, pendingFiles: pendingFiles,
                        serviceName: device.serviceName, protocolVersion: protocolVersion,
                        userInitiated: !isAutoDialing)
    }

    func clearError() {
        errorMessage = nil
        shouldSuggestNetworkHelp = false
    }

    func accept(_ connection: NWConnection) {
        guard settings.firstRunComplete, isIdentityReady, activeConnectionTokens.count < 39 else {
            connection.cancel()
            return
        }
        beginConnection(NetworkByteStream(connection: connection), role: .responder, pendingFiles: [])
    }

    func selectConversation(_ peerID: String) {
        connectedPeerID = peerID
        channel = channelsByPeer[peerID]
        connectedPeerName = channel?.peerName ?? chatPeers.first(where: { $0.id == peerID })?.name ?? ""
        isConnected = channel != nil
        isSending = sendingPeers.contains(peerID)
        isSendingText = sendingTextPeers.contains(peerID)
        isReceivingFile = receivingPeers.contains(peerID)
        retryURLs = retryURLsByPeer[peerID] ?? []
        canRetry = !retryURLs.isEmpty
        sendTask = sendTasksByPeer[peerID]
        textTask = textTasksByPeer[peerID]
        readerTask = readerTasksByPeer[peerID]
        connectionTask = connectionTasksByToken[peerTokens[peerID] ?? UUID()]
        isConnecting = !connectionTasksByToken.isEmpty
        if isConnected { connectionState = "已连接：\(connectedPeerName)" }
        else if isConnecting { connectionState = "正在连接…" }
        else { connectionState = "未连接" }
    }

    func isConnected(to peerID: String) -> Bool { channelsByPeer[peerID] != nil }

    /// Why `requestConnection(to:)` would currently decline, or `ok`. The guard rejects silently
    /// in production; acceptance runs need the reason.
    func connectionReadiness(for device: LANDevice, protocolVersion: UInt16 = 5) -> String {
        if !(3...6).contains(protocolVersion) { return "unsupported-version" }
        if !device.protocolVersions.contains(protocolVersion) { return "peer-lacks-version" }
        if connectingServiceNames.contains(device.serviceName) { return "already-connecting" }
        if activeConnectionTokens.count >= 39 { return "connection-limit" }
        if directPeerIDs.count + directConnectionTokens.count >= 20 { return "direct-limit" }
        if !settings.firstRunComplete { return "setup-incomplete" }
        if !isIdentityReady { return "identity-not-ready" }
        return "ok"
    }

    /// Read-only view of the channel used for group operations with `peerID`, for tests and
    /// support diagnostics. Returns `none` when no channel exists.
    func groupChannelSummary(_ peerID: String) -> String {
        guard let channel = channelsByPeer[peerID] else { return "none" }
        return "v\(channel.protocolVersion)|direct=\(directPeerIDs.contains(peerID))"
    }
    func isSending(to peerID: String) -> Bool { sendingPeers.contains(peerID) || groupFileWorkers.contains(peerID) }
    func isSendingText(to peerID: String) -> Bool { sendingTextPeers.contains(peerID) }
    func isReceiving(from peerID: String) -> Bool { receivingPeers.contains(peerID) }
    func isConnectionPending(serviceName: String) -> Bool { connectingServiceNames.contains(serviceName) }
    func isConnectionPending(peerID: String) -> Bool { isConnectionPending(for: peerID) }

    func cancelConnection(serviceName: String) {
        let matches = serviceNameByToken.filter { $0.value == serviceName }.map(\.key)
        for token in matches {
            connectionTasksByToken.removeValue(forKey: token)?.cancel()
            transportByToken.removeValue(forKey: token)?.close()
            finishConnectionAttempt(token)
        }
        refreshConnectionSummary()
    }

    private func beginConnection(_ stream: NetworkByteStream, role: HandshakeRole, pendingFiles: [URL],
                                 serviceName: String? = nil, purpose: ConnectionPurpose = .direct,
                                 protocolVersion: UInt16 = 3, userInitiated: Bool = false) {
        guard let identity = preparedIdentity else {
            stream.close()
            if let serviceName { connectingServiceNames.remove(serviceName) }
            errorMessage = "设备身份尚未准备完成，请先完成系统钥匙串授权。"
            return
        }
        guard activeConnectionTokens.count < 39 else { stream.close(); return }
        let token = UUID()
        activeConnectionTokens.insert(token)
        purposeByToken[token] = purpose
        if userInitiated { userInitiatedConnectionTokens.insert(token) }
        if case .direct = purpose { directConnectionTokens.insert(token) }
        transportByToken[token] = stream
        if let serviceName { serviceNameByToken[token] = serviceName }
        isConnecting = true
        connectionState = "正在建立局域网连接…"
        let task = Task { [self] in
            do {
                try await stream.start(timeout: 10)
                let connection = try await ConnectionService.establish(transport: stream, identity: identity,
                                                                         role: role, deviceName: settings.deviceName,
                                                                          protocolVersion: protocolVersion,
                                                                         onStage: { [weak self] stage in
                    await self?.handshakeProgress(stage, role: role, generation: token)
                }) { [weak self] peer in
                    guard let self else { return false }
                    return try await self.askForConnection(peer, generation: token)
                }
                trustedDialTokens.removeValue(forKey: token)
                guard activeConnectionTokens.contains(token), !Task.isCancelled else { connection.close(); return }
                install(connection, generation: token)
                if !pendingFiles.isEmpty { startSending(pendingFiles, to: connection.peerFingerprint) }
            } catch {
                let trustedPeerID = trustedDialTokens.removeValue(forKey: token)
                guard activeConnectionTokens.contains(token) else { return }
                let userInitiated = userInitiatedConnectionTokens.contains(token)
                finishConnectionAttempt(token)
                let wasRejected = (error as? ConnectionError).map {
                    if case .rejected = $0 { return true }; return false
                } == true
                if let trustedPeerID, !(error is CancellationError) {
                    noteTrustedDialFailure(trustedPeerID, rejected: wasRejected)
                }
                let rejectedLocally = role == .responder && wasRejected
                if userInitiated && !(error is CancellationError) {
                    errorMessage = error.localizedDescription
                    shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                } else if !rejectedLocally && !(error is CancellationError) {
                    connectionState = "连接未建立：\(error.localizedDescription)"
                }
                if !pendingFiles.isEmpty { canRetry = true }
                refreshConnectionSummary()
                if case .group(let groupID) = purpose {
                    let hasPendingLeave = pendingGroupLeaveRequests.contains(where: { $0.message.groupID == groupID }) ||
                        pendingGroupLeaveMessagesByGroup[groupID]?.isEmpty == false
                    if wasRejected && hasPendingLeave {
                        errorMessage = "群连接被拒绝，退出申请仍待处理；再次处理申请可重试连接。"
                    } else if !wasRejected && !(error is CancellationError) {
                        schedulePendingGroupLeaveReconnects(groupID: groupID)
                    }
                }
                if rejectedLocally { connectionState = "已拒绝连接" }
            }
        }
        connectionTasksByToken[token] = task
        connectionTask = task
    }

    private func clearReconnectReplacement(peerID: String, token: UUID) {
        if reconnectReplacementTokens[peerID] == token { reconnectReplacementTokens.removeValue(forKey: peerID) }
    }

    private func finishConnectionAttempt(_ token: UUID, closeTransport: Bool = true) {
        activeConnectionTokens.remove(token)
        directConnectionTokens.remove(token)
        purposeByToken.removeValue(forKey: token)
        userInitiatedConnectionTokens.remove(token)
        if closeTransport { transportByToken.removeValue(forKey: token)?.close() }
        else { transportByToken.removeValue(forKey: token) }
        connectionTasksByToken.removeValue(forKey: token)
        if let serviceName = serviceNameByToken.removeValue(forKey: token) {
            connectingServiceNames.remove(serviceName)
        }
        isConnecting = !connectingServiceNames.isEmpty || !connectionTasksByToken.isEmpty
    }

    private func refreshConnectionSummary() {
        connectedPeerIDs = Set(channelsByPeer.keys)
        selectConversation(connectedPeerID)
    }

    private func handshakeProgress(_ stage: HandshakeStage, role: HandshakeRole, generation token: UUID) {
        switch stage {
        case .exchangingOffers: connectionState = "正在交换设备信息…"
        case .authorizingIdentity: connectionState = "正在验证设备身份，如有钥匙串弹窗请完成授权…"
        case .waitingForPeer: connectionState = "等待对方完成身份验证或系统授权…"
        case .verified: connectionState = role == .initiator ? "等待对方同意连接…" : "请确认是否同意连接…"
        }
    }

    private func askForConnection(_ peer: PendingPeerApproval, generation token: UUID) async throws -> Bool {
        let otherDirectReservations = max(0, directConnectionTokens.count - 1)
        let directSlotsReserved = directPeerIDs.count + otherDirectReservations
        if directSlotsReserved >= 20 {
            let isKnownGroupMember = groups.contains { group in group.members.contains { $0.fingerprint == peer.peerFingerprint } }
            guard isKnownGroupMember, groupOnlyPeerIDs.count + pendingGroupConnectionCount < 19 else { return false }
        }
        if isTrustedPeer(peer.peerFingerprint), trustedDialStates[trustKey(peer.peerFingerprint)]?.suppressed != true {
            return true
        }
        let id = UUID()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            guard activeConnectionTokens.contains(token) else { throw CancellationError() }
            approvalPeerNames[id] = peer.peer.deviceName
            approvalPeerFingerprints[id] = peer.peerFingerprint
            cacheDiscoveredAvatar(for: peer.peerFingerprint)
            approvalOrder.append(id)
            refreshPendingApprovalName()
            return try await withCheckedThrowingContinuation { approvalContinuations[id] = $0 }
        } onCancel: {
            Task { @MainActor [weak self] in self?.cancelConnectionApproval(id) }
        }
    }

    private func cancelConnectionApproval(_ id: UUID) {
        let continuation = approvalContinuations.removeValue(forKey: id)
        approvalPeerNames.removeValue(forKey: id)
        approvalPeerFingerprints.removeValue(forKey: id)
        approvalOrder.removeAll { $0 == id }
        refreshPendingApprovalName()
        continuation?.resume(returning: false)
    }

    private func refreshPendingApprovalName() {
        let currentID = approvalOrder.first
        pendingConnectionPeerName = currentID.flatMap { approvalPeerNames[$0] }
        pendingConnectionPeerFingerprint = currentID.flatMap { approvalPeerFingerprints[$0] }
    }

    /// `trust` also adds the peer to the persistent contacts when the request is accepted.
    func approveConnection(_ accepted: Bool, trust: Bool = false) {
        guard let id = approvalOrder.first else { return }
        if accepted, trust, let fingerprint = approvalPeerFingerprints[id] {
            try? setTrustedPeer(fingerprint, trusted: true)
        }
        let continuation = approvalContinuations.removeValue(forKey: id)
        approvalPeerNames.removeValue(forKey: id)
        approvalPeerFingerprints.removeValue(forKey: id)
        approvalOrder.removeFirst()
        refreshPendingApprovalName()
        continuation?.resume(returning: accepted)
    }

    private func install(_ connection: FileConnection, generation token: UUID) {
        let peerID = connection.peerFingerprint
        if trustedDialStates[trustKey(peerID)]?.suppressed != true { trustedDialStates[trustKey(peerID)] = nil }
        cacheDiscoveredAvatar(for: peerID)
        var purpose = purposeByToken[token] ?? .direct
        if case .direct = purpose, !directPeerIDs.contains(peerID), !messages.contains(where: { $0.peerID == peerID }) {
            if let group = groups.first(where: { $0.members.contains(where: { $0.fingerprint == peerID }) }) {
                purpose = .group(group.id)
            } else if let pending = pendingGroupMembershipChanges.values.first(where: {
                $0.roster?.contains(where: { $0.fingerprint == peerID }) == true
            }) {
                purpose = .group(pending.groupID)
            } else if let invitation = acceptedGroupInvitations.values.first(where: {
                $0.roster?.contains(where: { $0.fingerprint == peerID }) == true
            }) {
                purpose = .group(invitation.groupID)
            }
        }
        if channelsByPeer[peerID] != nil {
            let hasOtherDirectAttempt = directConnectionTokens.contains { $0 != token }
            if case .direct = purpose, reconnectReplacementTokens[peerID] == nil, !hasOtherDirectAttempt {
                reconnectReplacementTokens[peerID] = token
                disconnect(peerID: peerID, failureReason: "网络连接已更换，未完成的消息可重试。", preservePendingRequests: true)
            } else {
                connection.close()
                finishConnectionAttempt(token)
                refreshConnectionSummary()
                return
            }
        }
        if case .direct = purpose {
            let reservedDirectCount = directPeerIDs.count + directConnectionTokens.count
            if reservedDirectCount > 20 {
                guard let group = groups.first(where: { $0.members.contains { $0.fingerprint == peerID } }),
                      groupOnlyPeerIDs.count < 19 else {
                    connection.close()
                    clearReconnectReplacement(peerID: peerID, token: token)
                    finishConnectionAttempt(token)
                    refreshConnectionSummary()
                    return
                }
                purpose = .group(group.id)
            }
        }
        if case .group(let groupID) = purpose {
            guard isKnownGroupMember(peerID, groupID: groupID),
                  groupOnlyPeerIDs.contains(peerID) || groupOnlyPeerIDs.count < 19 else {
                connection.close()
                clearReconnectReplacement(peerID: peerID, token: token)
                finishConnectionAttempt(token)
                refreshConnectionSummary()
                return
            }
        }
        finishConnectionAttempt(token, closeTransport: false)
        activeConnectionTokens.insert(token)
        peerTokens[peerID] = token
        channelsByPeer[peerID] = connection
        switch purpose {
        case .direct: directPeerIDs.insert(peerID)
        case .group(let groupID): groupPeerIDs[groupID, default: []].insert(peerID)
        }
        for group in groups where group.members.contains(where: { $0.fingerprint == peerID }) {
            groupPeerIDs[group.id, default: []].insert(peerID)
        }
        connectedPeerIDs = Set(channelsByPeer.keys)
        if case .direct = purpose, connectedPeerID.isEmpty || channelsByPeer[connectedPeerID] == nil {
            connectedPeerID = peerID
        }
        restoreConversation(peerID)
        connectionState = "已连接：\(connection.peerName)"
        markConnectionTipSeen()
        defer { flushOutbox(peerID) }
        if !pendingGroupAborts.isEmpty {
            Task { @MainActor [weak self] in await self?.retryPendingGroupAborts() }
        }
        if !pendingGroupCommits.isEmpty {
            Task { @MainActor [weak self] in await self?.retryPendingGroupCommits() }
        }
        if !pendingGroupPreparations.isEmpty {
            Task { @MainActor [weak self] in await self?.retryPendingGroupPreparations() }
        }
        let directory = settings.receiveDirectory
        let reader = Task { [self] in
            do {
                try await connection.run(receiveDirectory: directory, directoryProvider: { [weak self] in
                    await self?.settings.receiveDirectory ?? directory
                }, fileApproval: { [weak self] metadata in
                    guard let self else { return false }
                    return await self.requestFileApproval(metadata, peer: connection.peerName,
                                                          peerID: peerID, generation: token)
                }, didReceive: { [weak self] metadata, url in
                    await self?.received(metadata, at: url, peer: connection.peerName, peerID: peerID, generation: token)
                }, progress: { [weak self] metadata, count in
                    await self?.receiving(metadata, count: count, peer: connection.peerName, peerID: peerID, generation: token)
                }, didReceiveText: { [weak self] message in
                    guard let self else { throw ConnectionError.closed }
                    try await self.receiveText(message, peer: connection.peerName, peerID: peerID, generation: token)
                }, groupFileApproval: { [weak self] manifest in
                    guard let self else { return false }
                    return await self.requestGroupFileApproval(manifest, peer: connection.peerName,
                                                               peerID: peerID, generation: token)
                }, groupFileDidReceive: { [weak self] manifest, url in
                    guard let self else { throw ConnectionError.closed }
                    try await self.storeReceivedGroupFile(manifest, at: url, peerID: peerID, generation: token)
                }, didReceiveGroupFrame: { [weak self] type, payload in
                    guard let self else { throw ConnectionError.closed }
                    try await self.receiveGroupFrame(type, payload: payload, peer: connection.peerName,
                                                     peerID: peerID, generation: token)
                })
            } catch {
                guard activeConnectionTokens.contains(token) else { return }
                let interrupted = messages.contains { $0.peerID == peerID && $0.isPending }
                // A peer going offline only changes the conversation status; it is not an error to acknowledge.
                disconnect(peerID: peerID, failureReason: error.localizedDescription,
                           preservePendingRequests: true)
                schedulePendingGroupLeaveReconnect(peerID: peerID)
                if interrupted { transferStatus = "连接中断" }
            }
        }
        readerTasksByPeer[peerID] = reader
        readerTask = reader
        refreshConnectionSummary()
        resendPendingGroupLeaveMessages(to: peerID, generation: token)
        resendPendingGroupLeaveReplies(to: peerID, generation: token)
    }

    /// Shows an incoming file as "等待你确认接收" and suspends until the user decides.
    ///
    /// A file is never written to disk before this returns `true`, so declining cannot leave a partial
    /// file. Asking fails closed: a disconnected connection or cancelled task declines.
    private func requestFileApproval(_ metadata: FileMetadata, peer: String, peerID: String,
                                     generation token: UUID) async -> Bool {
        guard activeConnectionTokens.contains(token) else { return false }
        if let index = messages.firstIndex(where: { $0.id == metadata.transferID && $0.peerID == peerID && $0.direction == .received }) {
            messages[index].status = .awaitingApproval
            messages[index].failureReason = nil
            saveMessages()
        } else {
            messages.append(ChatMessage(id: metadata.transferID, peerID: peerID, peerName: peer,
                                        direction: .received,
                                        kind: metadata.kind == .image && metadata.folder == nil ? .image : .file,
                                        fileName: metadata.displayName, size: Int64(metadata.displaySize),
                                        status: .awaitingApproval))
            saveMessages()
        }
        let key = FileDecisionKey(peerID: peerID, transferID: metadata.transferID)
        let timeout = Task { [weak self] in
            try? await Task.sleep(for: Self.fileApprovalTimeout)
            guard !Task.isCancelled else { return }
            self?.resolveFileDecision(key, accepted: false, timedOut: true)
        }
        let accepted = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                guard activeConnectionTokens.contains(token) else { continuation.resume(returning: false); return }
                fileDecisions[key] = continuation
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.resolveFileDecision(key, accepted: false, timedOut: false) }
        }
        timeout.cancel()
        if !accepted { markFileDeclined(metadata.transferID, peerID: peerID) }
        return accepted
    }

    private func requestGroupFileApproval(_ manifest: GroupFileManifestWire, peer: String, peerID: String,
                                          generation token: UUID) async -> Bool {
        guard activeConnectionTokens.contains(token), pendingGroupMembershipChanges[manifest.groupID] == nil,
              let identity = preparedIdentity,
              let group = groups.first(where: { $0.id == manifest.groupID }),
              manifest.senderFingerprint == peerID, manifest.memberRevision == group.revision,
              group.members.contains(where: { $0.fingerprint == peerID }),
              group.members.contains(where: { $0.fingerprint == identity.fingerprint }),
              manifest.metadata.size <= UInt64(Int64.max) else { return false }
        let key = FileDecisionKey(peerID: peerID, transferID: manifest.metadata.transferID)
        guard groupFileDecisionContinuations[key] == nil else { return false }
        pendingGroupFileApprovals.append(PendingGroupFileApproval(manifest: manifest, peerName: peer,
                                                                  groupName: group.name))
        let timeout = Task { [weak self] in
            try? await Task.sleep(for: Self.fileApprovalTimeout)
            guard !Task.isCancelled else { return }
            self?.resolveGroupFileDecision(key, accepted: false)
        }
        let accepted = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                guard activeConnectionTokens.contains(token) else { continuation.resume(returning: false); return }
                groupFileDecisionContinuations[key] = continuation
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.resolveGroupFileDecision(key, accepted: false) }
        }
        timeout.cancel()
        if accepted, activeConnectionTokens.contains(token) {
            receivingPeers.insert(peerID)
            isReceivingFile = true
            if connectedPeerID == peerID {
                receiveProgress = 0
                receiveStatus = "正在接收群文件：\(manifest.metadata.fileName)"
            }
        }
        return accepted
    }

    func decideGroupFile(_ transferID: UUID, peerID: String, accepted: Bool) {
        resolveGroupFileDecision(FileDecisionKey(peerID: peerID, transferID: transferID), accepted: accepted)
    }

    private func resolveGroupFileDecision(_ key: FileDecisionKey, accepted: Bool) {
        pendingGroupFileApprovals.removeAll {
            $0.manifest.metadata.transferID == key.transferID &&
            $0.manifest.senderFingerprint == key.peerID
        }
        groupFileDecisionContinuations.removeValue(forKey: key)?.resume(returning: accepted)
    }

    private func storeReceivedGroupFile(_ manifest: GroupFileManifestWire, at url: URL,
                                        peerID: String, generation token: UUID) throws {
        guard activeConnectionTokens.contains(token), pendingGroupMembershipChanges[manifest.groupID] == nil,
              let identity = preparedIdentity,
              let group = groups.first(where: { $0.id == manifest.groupID }),
              manifest.senderFingerprint == peerID, manifest.memberRevision == group.revision,
              group.members.contains(where: { $0.fingerprint == peerID }),
              group.members.contains(where: { $0.fingerprint == identity.fingerprint }) else {
            throw GroupConversationError.senderNotMember
        }
        let metadata = try GroupFileMetadata(transferID: manifest.metadata.transferID,
                                             fileName: manifest.metadata.fileName,
                                             size: manifest.metadata.size,
                                             sha256: manifest.sha256)
        let envelope = try GroupMessageEnvelope(id: manifest.messageID, groupID: group.id,
            senderFingerprint: peerID, memberRevision: manifest.memberRevision, kind: metadataKind(manifest),
            fileMetadata: metadata)
        if !groupMessages.contains(where: { $0.id == envelope.id && $0.groupID == envelope.groupID }) {
            groupMessages.append(envelope)
        }
        groupFileLocations[metadata.transferID] = url.path
        try persistGroupState()
        receivingPeers.remove(peerID)
        isReceivingFile = !receivingPeers.isEmpty
        if connectedPeerID == peerID { receiveStatus = "群文件已接收：\(metadata.fileName)"; receiveProgress = 1 }
        addRecord(TransferRecord(fileName: metadata.fileName, size: Int64(metadata.size), direction: .received,
                                 peerName: "\(channelsByPeer[peerID]?.peerName ?? peerID) · 群聊", savedPath: url.path))
        noteFirstSuccessfulTransfer(title: "收到群文件", body: "\(group.name) 中收到 \(metadata.fileName)。")
        noteIncoming(conversation: Self.groupConversationKey(group.id), title: group.name, body: metadata.fileName, notify: false)
    }

    private func metadataKind(_ manifest: GroupFileManifestWire) -> GroupMessageKind {
        manifest.metadata.kind == .image ? .image : .file
    }

    /// The card supplies the sender identity as well as the transfer ID, avoiding cross-peer ambiguity.
    func decideIncomingFile(_ transferID: UUID, peerID: String, accepted: Bool) {
        resolveFileDecision(FileDecisionKey(peerID: peerID, transferID: transferID), accepted: accepted, timedOut: false)
    }

    /// Kept for older call sites and tests; a transfer ID is resolved only when unambiguous.
    func decideIncomingFile(_ transferID: UUID, accepted: Bool) {
        let matches = fileDecisions.keys.filter { $0.transferID == transferID }
        guard matches.count == 1, let key = matches.first else { return }
        resolveFileDecision(key, accepted: accepted, timedOut: false)
    }

    private func resolveFileDecision(_ key: FileDecisionKey, accepted: Bool, timedOut: Bool) {
        guard let continuation = fileDecisions.removeValue(forKey: key) else { return }
        if timedOut { markFileDeclined(key.transferID, peerID: key.peerID, reason: "等待接收确认超时。") }
        continuation.resume(returning: accepted)
    }

    private func markFileDeclined(_ transferID: UUID, peerID: String, reason: String? = nil) {
        guard let index = messages.firstIndex(where: { $0.id == transferID && $0.peerID == peerID && $0.direction == .received }) else { return }
        messages[index].status = .declined
        messages[index].failureReason = reason ?? "已拒绝接收该文件。"
        saveMessages()
    }

    private func receiving(_ metadata: FileMetadata, count: UInt64, peer: String, peerID: String,
                           generation token: UUID) {
        guard activeConnectionTokens.contains(token) else { return }
        receivingPeers.insert(peerID)
        isReceivingFile = !receivingPeers.isEmpty
        // Folder progress is reported against the whole folder.
        let total = metadata.displaySize
        let progress = total == 0 ? 0 : min(1, Double(count) / Double(total))
        if connectedPeerID == peerID {
            receiveStatus = metadata.folder.map { "正在接收文件夹 \($0.rootName)：\($0.relativePath)" } ?? "正在接收：\(metadata.fileName)"
            receiveProgress = progress
        }
        if let index = messages.firstIndex(where: { $0.id == metadata.transferID && $0.peerID == peerID && $0.direction == .received && $0.kind != .text }) {
            if progress - messages[index].progress >= 0.01 { messages[index].progress = progress }
        } else {
            messages.append(ChatMessage(id: metadata.transferID, peerID: peerID, peerName: peer,
                                        direction: .received, kind: metadata.kind == .image && metadata.folder == nil ? .image : .file,
                                        fileName: metadata.displayName, size: Int64(total), status: .sending,
                                        progress: progress))
        }
    }

    private func received(_ metadata: FileMetadata, at url: URL, peer: String, peerID: String, generation token: UUID) {
        guard activeConnectionTokens.contains(token) else { return }
        addRecord(TransferRecord(fileName: url.lastPathComponent, size: Int64(metadata.displaySize), direction: .received,
                                 peerName: peer, savedPath: url.path))
        restoreConversation(peerID)
        if let index = messages.firstIndex(where: { $0.id == metadata.transferID && $0.peerID == peerID && $0.direction == .received && $0.kind != .text }) {
            messages[index].status = .received
            messages[index].progress = 1
            messages[index].localPath = url.path
            messages[index].fileName = url.lastPathComponent
            saveMessages()
        }
        receivingPeers.remove(peerID)
        isReceivingFile = !receivingPeers.isEmpty
        if connectedPeerID == peerID {
            receiveProgress = 1
            receiveStatus = "已接收：\(url.lastPathComponent)"
        }
        noteIncoming(conversation: peerID, title: peer, body: url.lastPathComponent, notify: false)
        noteFirstSuccessfulTransfer(title: "收到文件", body: "\(peer) 发送了 \(url.lastPathComponent)。")
    }

    func isSendingGroupFile(_ groupID: UUID) -> Bool {
        guard let group = groups.first(where: { $0.id == groupID }) else { return false }
        return group.members.contains { sendingPeers.contains($0.fingerprint) || groupFileWorkers.contains($0.fingerprint) }
    }

    func startSendingPastedImage(_ image: NSImage, toGroupID groupID: UUID) {
        guard canSendGroupText(groupID), let png = encodedPNG(image) else {
            errorMessage = "无法读取剪贴板中的图片。"
            return
        }
        do {
            let directory = settingsURL.deletingLastPathComponent().appendingPathComponent("PastedImages", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("粘贴图片-\(UUID().uuidString).png")
            try png.write(to: url, options: .atomic)
            startSendingGroupFiles([url], groupID: groupID)
        } catch { errorMessage = "无法准备剪贴板图片：\(error.localizedDescription)" }
    }

    func startSendingPastedImage(_ image: NSImage, to peerID: String? = nil) {
        let peerID = peerID ?? connectedPeerID
        guard isConnected(to: peerID) || canQueueOffline(peerID), !isSending(to: peerID) else { return }
        guard let png = encodedPNG(image) else { errorMessage = "无法读取剪贴板中的图片。"; return }
        do {
            let directory = settingsURL.deletingLastPathComponent().appendingPathComponent("PastedImages", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("粘贴图片-\(UUID().uuidString).png")
            try png.write(to: url, options: .atomic)
            startSending([url], to: peerID)
        } catch { errorMessage = "无法准备剪贴板图片：\(error.localizedDescription)" }
    }

    private func encodedPNG(_ image: NSImage) -> Data? {
        guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff) else { return nil }
        return bitmap.representation(using: .png, properties: [:])
    }

    func startSending(_ urls: [URL]) {
        startSending(urls, to: connectedPeerID)
    }

    /// `existing` delivers outbox messages, which already have their bubbles, instead of adding new ones.
    func startSending(_ urls: [URL], to peerID: String, existing: [ChatMessage]? = nil) {
        guard !urls.isEmpty else { return }
        guard let connection = channelsByPeer[peerID], let token = peerTokens[peerID],
              existing != nil || !hasOutbox(peerID) else {
            let name = outboxPeerName(peerID)
            enqueueOutbox(urls.map { url in
                let folder = (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
                return ChatMessage(peerID: peerID, peerName: name, direction: .sent,
                                   kind: !folder && Self.isImage(url) ? .image : .file, fileName: url.lastPathComponent,
                                   size: folder ? nil : (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init),
                                   localPath: url.path, status: .queued, outboxSince: Date())
            })
            return
        }
        guard sendTasksByPeer[peerID] == nil, !groupFileWorkers.contains(peerID) else { return }
        let isFolder = urls.map { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
        if isFolder.contains(true), !connection.supportsResume {
            errorMessage = "对方版本不支持发送文件夹，请让对方升级后再试，或打包后发送。"
            return
        }
        retryURLsByPeer[peerID] = urls
        if connectedPeerID == peerID {
            retryURLs = urls
            canRetry = false
            queueCount = urls.count
            queueCompleted = 0
            transferProgress = 0
        }
        sendingPeers.insert(peerID)
        isSending = !sendingPeers.isEmpty
        let queued = existing ?? zip(urls, isFolder).map { url, folder in
            ChatMessage(peerID: peerID, peerName: connection.peerName, direction: .sent,
                        kind: !folder && Self.isImage(url) ? .image : .file, fileName: url.lastPathComponent,
                        size: folder ? nil : (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init),
                        localPath: url.path, status: .queued)
        }
        if existing == nil { messages.append(contentsOf: queued) }
        saveMessages()
        let task = Task { [self] in
            defer {
                sendingPeers.remove(peerID)
                isSending = !sendingPeers.isEmpty
                if connectedPeerID == peerID { sendTask = nil }
                sendTasksByPeer.removeValue(forKey: peerID)
                flushOutbox(peerID)
            }
            for (index, url) in urls.enumerated() {
                do {
                    try Task.checkCancellation()
                    if connectedPeerID == peerID { transferStatus = "正在发送（\(index + 1)/\(urls.count)）：\(url.lastPathComponent)" }
                    let messageID = queued[index].id
                    updateMessage(messageID, status: .sending)
                    let progress: @Sendable (UInt64, UInt64) async -> Void = { [weak self] count, total in
                        await self?.sendingProgress(count: count, total: total, index: index,
                                                    messageID: messageID, peerID: peerID, generation: token)
                    }
                    let sentName: String, sentSize: UInt64
                    if isFolder[index] {
                        let result = try await connection.sendFolder(url, transferID: messageID, progress: progress)
                        (sentName, sentSize) = (result.rootName, result.totalBytes)
                        if let position = messages.firstIndex(where: { $0.id == messageID && $0.peerID == peerID }) {
                            messages[position].size = Int64(result.totalBytes)
                        }
                    } else {
                        let metadata = try await connection.sendFile(url, transferID: messageID,
                                                                     kind: queued[index].kind == .image ? .image : .file,
                                                                     progress: progress)
                        (sentName, sentSize) = (metadata.fileName, metadata.size)
                    }
                    guard activeConnectionTokens.contains(token) else { return }
                    addRecord(TransferRecord(fileName: sentName, size: Int64(sentSize), direction: .sent,
                                             peerName: connection.peerName, savedPath: nil))
                    updateMessage(messageID, status: .delivered)
                    retryURLsByPeer[peerID] = Array(urls.dropFirst(index + 1))
                    if connectedPeerID == peerID {
                        queueCompleted = index + 1
                        transferProgress = Double(queueCompleted) / Double(max(1, queueCount))
                        retryURLs = retryURLsByPeer[peerID] ?? []
                    }
                } catch {
                    guard activeConnectionTokens.contains(token) else { return }
                    // A declined file is final; it is never retried from the outbox.
                    if let transferError = error as? TransferSessionError, case .peerRejected = transferError {
                        updateMessage(queued[index].id, status: .declined, failureReason: "对方拒绝接收该文件。")
                    }
                    if Task.isCancelled {
                        if connectedPeerID == peerID { transferStatus = "发送已取消" }
                    } else if connectedPeerID == peerID {
                        transferStatus = "发送未完成"
                    }
                    retryURLsByPeer[peerID] = Array(urls.dropFirst(index))
                    if connectedPeerID == peerID { canRetry = true; retryURLs = retryURLsByPeer[peerID] ?? [] }
                    disconnect(peerID: peerID, failureReason: error.localizedDescription,
                               preservePendingRequests: true)
                    schedulePendingGroupLeaveReconnect(peerID: peerID)
                    if !(error is CancellationError) && !isConnectionLoss(error) {
                        errorMessage = error.localizedDescription
                        shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                    }
                    return
                }
            }
            if connectedPeerID == peerID { transferStatus = "已发送 \(urls.count) 个文件" }
            if queued.count > 0 {
                let last = urls.last?.lastPathComponent ?? "文件"
                noteFirstSuccessfulTransfer(title: "发送完成", body: "已发送给 \(connection.peerName)：\(last)。")
            }
        }
        sendTasksByPeer[peerID] = task
        if connectedPeerID == peerID { sendTask = task }
    }


    /// First completed transfer shows a closable tip and, once, invites turning on notifications.
    /// Accepting or skipping the invitation never repeats it.
    private func noteFirstSuccessfulTransfer(title: String, body: String) {
        if !settings.notificationsEnabled && !notifierAvailable && !firstTransferNoticePending {
            notificationInvitationPending = true
        }
        firstTransferNoticePending = true
        if settings.notificationsEnabled {
            notifier.show(title: title, body: body)
        }
    }

    private func sendingProgress(count: UInt64, total: UInt64, index: Int, messageID: UUID,
                                 peerID: String, generation token: UUID) {
        guard activeConnectionTokens.contains(token) else { return }
        if let position = messages.firstIndex(where: { $0.id == messageID && $0.peerID == peerID }) {
            let value = total == 0 ? 0 : Double(count) / Double(total)
            if value - messages[position].progress >= 0.01 { messages[position].progress = value }
        }
        if connectedPeerID == peerID {
            transferProgress = (Double(index) + (total == 0 ? 0 : Double(count) / Double(total))) / Double(max(1, queueCount))
        }
    }

    func retrySending() {
        retrySending(to: connectedPeerID)
    }

    func retrySending(to peerID: String) {
        let pending = retryURLsByPeer[peerID] ?? (peerID == connectedPeerID ? retryURLs : [])
        guard !pending.isEmpty else { return }
        if channelsByPeer[peerID] != nil { startSending(pending, to: peerID) }
        else {
            if let device = nearbyDevice(for: peerID) {
                requestConnection(to: device, pendingFiles: pending)
            } else if peerID == connectedPeerID {
                errorMessage = "请先重新连接对方，再重试未完成的文件。"
            }
        }
    }

    func cancelTransfer() {
        if isSending { transferStatus = "发送已取消"; canRetry = !retryURLs.isEmpty }
        if isReceivingFile { receiveStatus = "接收已取消，未完成的文件已清理" }
        if channelsByPeer[connectedPeerID] != nil {
            disconnect(peerID: connectedPeerID, preservePendingRequests: true)
            schedulePendingGroupLeaveReconnect(peerID: connectedPeerID)
            schedulePendingGroupLeaveReconnects()
        } else {
            cancelConnectionAttempts()
            schedulePendingGroupLeaveReconnects()
        }
    }

    func disconnect(failureReason: String? = nil) {
        if let active = channelsByPeer[connectedPeerID] {
            _ = active
            disconnect(peerID: connectedPeerID, failureReason: failureReason)
        } else {
            cancelConnectionAttempts()
        }
    }

    func disconnect(peerID: String, failureReason: String? = nil, preservePendingRequests: Bool = false) {
        guard let token = peerTokens.removeValue(forKey: peerID) else { return }
        if reconnectReplacementTokens[peerID] == token { reconnectReplacementTokens.removeValue(forKey: peerID) }
        activeConnectionTokens.remove(token)
        connectionTasksByToken.removeValue(forKey: token)?.cancel()
        transportByToken.removeValue(forKey: token)?.close()
        readerTasksByPeer.removeValue(forKey: peerID)?.cancel()
        sendTasksByPeer[peerID]?.cancel()
        textTasksByPeer[peerID]?.cancel()
        channelsByPeer.removeValue(forKey: peerID)?.close()
        for (key, slot) in Array(pendingGroupControlWaiters) where key.peerFingerprint == peerID {
            slot.resolve(.failure(ConnectionError.closed))
            pendingGroupControlWaiters.removeValue(forKey: key)
        }
        for (key, slot) in Array(pendingGroupTextAckWaiters) where key.peerFingerprint == peerID {
            slot.resolve(.failure(ConnectionError.closed))
            pendingGroupTextAckWaiters.removeValue(forKey: key)
        }
        if !preservePendingRequests {
            pendingGroupInvitations.removeAll(where: { $0.peerFingerprint == peerID })
            pendingGroupLeaveRequests.removeAll(where: { $0.message.targetFingerprint == peerID })
            clearPendingGroupLeaveReconnect(peerID: peerID)
            for group in groups where group.ownerFingerprint == peerID {
                pendingLeaveRequestsByGroup.removeValue(forKey: group.id)
                pendingGroupLeaveMessagesByGroup.removeValue(forKey: group.id)
                pendingGroupMembershipChanges.removeValue(forKey: group.id)
            }
            refreshPendingGroupLeaveGroupIDs()
        }
        directPeerIDs.remove(peerID)
        for groupID in Array(groupPeerIDs.keys) {
            groupPeerIDs[groupID]?.remove(peerID)
            if groupPeerIDs[groupID]?.isEmpty == true { groupPeerIDs.removeValue(forKey: groupID) }
        }
        receivingPeers.remove(peerID)
        for index in messages.indices where messages[index].peerID == peerID && messages[index].isPending {
            messages[index].status = messages[index].interruptedStatus
            messages[index].progress = 0
            if let failureReason { messages[index].failureReason = failureReason }
        }
        let decisions = fileDecisions.filter { $0.key.peerID == peerID }
        for (key, continuation) in decisions {
            markFileDeclined(key.transferID, peerID: peerID, reason: "连接已断开，未开始接收。")
            fileDecisions.removeValue(forKey: key)
            continuation.resume(returning: false)
        }
        let groupDecisions = groupFileDecisionContinuations.filter { $0.key.peerID == peerID }
        for (key, continuation) in groupDecisions {
            pendingGroupFileApprovals.removeAll { $0.manifest.metadata.transferID == key.transferID }
            groupFileDecisionContinuations.removeValue(forKey: key)
            continuation.resume(returning: false)
        }
        if !preservePendingRequests {
            acceptedGroupInvitations = acceptedGroupInvitations.filter { $0.value.ownerFingerprint != peerID }
        }
        saveMessages()
        if connectedPeerID == peerID {
            channel = nil
            isConnected = false
            connectedPeerName = chatPeers.first(where: { $0.id == peerID })?.name ?? ""
            isSending = !sendingPeers.isEmpty
            isSendingText = !sendingTextPeers.isEmpty
            if connectedPeerID == peerID { sendTask = nil; textTask = nil }
            canRetry = !(retryURLsByPeer[peerID] ?? []).isEmpty
        }
        isReceivingFile = !receivingPeers.isEmpty
        connectedPeerIDs = Set(channelsByPeer.keys)
        connectionState = isConnected ? "已连接：\(connectedPeerName)" : "已断开：\(connectedPeerName)"
        if isTrustedPeer(peerID), trustedDialStates[trustKey(peerID)]?.suppressed != true {
            // Waiting first keeps a peer that just closed the connection from being redialed at once.
            trustedDialStates[trustKey(peerID), default: TrustedDialState()].nextAttempt = Date().addingTimeInterval(Self.trustedDialBackoff[0])
            connectTrustedPeers()
        }
    }

    // MARK: Offline outbox

    static let outboxLimit = 200
    static let outboxLifetime: TimeInterval = 7 * 24 * 3600

    func hasOutbox(_ peerID: String) -> Bool { messages.contains { $0.peerID == peerID && $0.isOutbox } }

    func outboxCount(_ peerID: String) -> Int { messages.filter { $0.peerID == peerID && $0.isOutbox }.count }

    /// A direct conversation can hold pending messages only when its peer is identified by fingerprint.
    func canQueueOffline(_ peerID: String) -> Bool { AvatarID.normalizeFingerprint(peerID) != nil }

    private func outboxPeerName(_ peerID: String) -> String {
        channelsByPeer[peerID]?.peerName ?? chatPeers.first { $0.id == peerID }?.name ?? trustedPeerName(trustKey(peerID))
    }

    @discardableResult
    private func enqueueOutbox(_ items: [ChatMessage]) -> Bool {
        guard let peerID = items.first?.peerID, canQueueOffline(peerID) else { errorMessage = "请先连接设备。"; return false }
        guard outboxCount(peerID) + items.count <= Self.outboxLimit else {
            errorMessage = "该会话已有 \(Self.outboxLimit) 条待发送消息，请等对方上线送达后再发送。"
            return false
        }
        messages.append(contentsOf: items)
        hiddenConversationIDs.remove(peerID)
        saveMessages()
        flushOutbox(peerID)
        return true
    }

    /// Manual resend of a failed message: delivered now when connected and idle, otherwise queued again.
    private func requeueOutbox(_ message: ChatMessage) {
        guard canQueueOffline(message.peerID),
              let index = messages.firstIndex(where: { $0.id == message.id && $0.direction == .sent }) else {
            errorMessage = "请先连接这台设备，再重试消息。"
            return
        }
        guard outboxCount(message.peerID) < Self.outboxLimit else {
            errorMessage = "该会话已有 \(Self.outboxLimit) 条待发送消息，请等对方上线送达后再发送。"
            return
        }
        messages[index].status = .queued
        messages[index].progress = 0
        messages[index].failureReason = nil
        messages[index].outboxSince = Date()
        saveMessages()
        flushOutbox(message.peerID)
    }

    /// Removes a message that has not started delivery.
    func cancelOutboxMessage(_ message: ChatMessage) {
        guard let index = messages.firstIndex(where: { $0.id == message.id && $0.direction == .sent && $0.isOutbox }),
              messages[index].status == .queued else { return }
        messages.remove(at: index)
        saveMessages()
    }

    /// Delivers the oldest pending message for a connected peer; each completion calls this again,
    /// so messages leave in the order they were written. Only persistent contacts are dialed for it;
    /// other peers receive pending messages once either side connects.
    func flushOutbox(_ peerID: String) {
        expireOutbox()
        guard hasOutbox(peerID) else { return }
        guard let connection = channelsByPeer[peerID] else { connectTrustedPeers(); return }
        guard textTasksByPeer[peerID] == nil, sendTasksByPeer[peerID] == nil, !groupFileWorkers.contains(peerID),
              let next = messages.filter({ $0.peerID == peerID && $0.isOutbox && $0.status == .queued })
                .min(by: { $0.date < $1.date }) else { return }
        if next.kind == .text {
            transmitText(next)
            return
        }
        var isDirectory: ObjCBool = false
        guard let path = next.localPath, FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) else {
            updateMessage(next.id, status: .failed, failureReason: "源文件已不存在，无法发送。")
            flushOutbox(peerID)
            return
        }
        guard !isDirectory.boolValue || connection.supportsResume else {
            updateMessage(next.id, status: .failed, failureReason: "对方版本不支持接收文件夹。")
            flushOutbox(peerID)
            return
        }
        startSending([URL(fileURLWithPath: path)], to: peerID, existing: [next])
    }

    private var outboxExpiryTimer: Task<Void, Never>?

    /// Checks hourly, so a pending message expires even when no discovery or connection event happens.
    private func startOutboxExpiryTimer() {
        outboxExpiryTimer?.cancel()
        outboxExpiryTimer = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(3600))
                guard !Task.isCancelled else { return }
                self?.expireOutbox()
            }
        }
    }

    /// Pending messages older than seven days fail and wait for a manual resend.
    private func expireOutbox(now: Date = Date()) {
        var changed = false
        for index in messages.indices where messages[index].isOutbox && messages[index].status == .queued {
            guard let since = messages[index].outboxSince, now.timeIntervalSince(since) > Self.outboxLifetime else { continue }
            messages[index].status = .failed
            messages[index].failureReason = "超过 7 天未送达，请手动重发。"
            changed = true
        }
        if changed { saveMessages() }
    }

    // MARK: Persistent contacts

    /// Trusted peers are stored as compact uppercase fingerprints; peer IDs elsewhere keep the
    /// grouped display form, so every lookup goes through this key.
    private func trustKey(_ peerID: String) -> String { AvatarID.normalizeFingerprint(peerID) ?? peerID }

    func isTrustedPeer(_ peerID: String) -> Bool { settings.trustedPeers.contains(trustKey(peerID)) }

    func setTrustedPeer(_ peerID: String, trusted: Bool) throws {
        guard let fingerprint = AvatarID.normalizeFingerprint(peerID) else { return }
        var updated = settings
        updated.trustedPeers.removeAll { $0 == fingerprint }
        if trusted { updated.trustedPeers.append(fingerprint) }
        updated.trustedPeers = DeviceSettings.normalizedTrustedPeers(updated.trustedPeers)
        try persist(updated, to: settingsURL)
        settings = updated
        trustedDialStates[fingerprint] = nil
        if trusted { connectTrustedPeers() }
    }

    func trustedPeerName(_ peerID: String) -> String {
        if let device = nearbyDevice(for: peerID) { return device.displayName }
        if let peer = chatPeers.first(where: { trustKey($0.id) == peerID }) { return peer.name }
        for group in groups {
            if let name = group.members.first(where: { trustKey($0.fingerprint) == peerID })?.displayName { return name }
        }
        return String(peerID.prefix(12))
    }

    /// A user-initiated disconnect also stops automatic redialing of this peer until the user connects again.
    func disconnectByUser(peerID: String) {
        if isTrustedPeer(peerID) { trustedDialStates[trustKey(peerID), default: TrustedDialState()].suppressed = true }
        disconnect(peerID: peerID)
    }

    private func noteTrustedDialFailure(_ peerID: String, rejected: Bool) {
        var state = trustedDialStates[peerID] ?? TrustedDialState()
        if rejected {
            state.suppressed = true
        } else {
            state.failures += 1
            state.nextAttempt = Date().addingTimeInterval(Self.trustedDialBackoff[min(state.failures, Self.trustedDialBackoff.count) - 1])
        }
        trustedDialStates[peerID] = state
        connectTrustedPeers()
    }

    /// Dials discovered persistent contacts that are not connected. The peer with the larger
    /// fingerprint waits 10 seconds after discovery so both sides rarely dial at once.
    func connectTrustedPeers(now: Date = Date()) {
        trustedDialTimer?.cancel()
        trustedDialTimer = nil
        expireOutbox(now: now)
        guard settings.firstRunComplete, isIdentityReady, !settings.trustedPeers.isEmpty else { return }
        let ownID = AvatarID.normalizeFingerprint(identityFingerprint) ?? ""
        var nextWake: Date?
        let connected = Set(channelsByPeer.keys.map(trustKey))
        let awaitingApproval = Set(approvalPeerFingerprints.values.map(trustKey))
        for peerID in settings.trustedPeers where !connected.contains(peerID) {
            guard let device = nearbyDevice(for: peerID) else {
                trustedDialStates[peerID]?.firstSeen = nil
                continue
            }
            var state = trustedDialStates[peerID] ?? TrustedDialState()
            if state.firstSeen == nil { state.firstSeen = now }
            trustedDialStates[peerID] = state
            guard !state.suppressed, !isConnectionPending(for: peerID),
                  !awaitingApproval.contains(peerID) else { continue }
            var due = state.nextAttempt
            if ownID > peerID, let seen = state.firstSeen { due = max(due, seen.addingTimeInterval(10)) }
            if due > now {
                nextWake = min(nextWake ?? due, due)
                continue
            }
            guard directPeerIDs.count + directConnectionTokens.count < 20 else { break }
            isAutoDialing = true
            requestConnection(to: device)
            isAutoDialing = false
            if let token = serviceNameByToken.first(where: { $0.value == device.serviceName })?.key {
                trustedDialTokens[token] = peerID
            }
        }
        guard let nextWake else { return }
        trustedDialTimer = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(max(1, nextWake.timeIntervalSinceNow)))
            guard !Task.isCancelled else { return }
            self?.connectTrustedPeers()
        }
    }

    private func cancelConnectionAttempts() {
        let attempts = Array(connectionTasksByToken.keys)
        for token in attempts {
            connectionTasksByToken.removeValue(forKey: token)?.cancel()
            finishConnectionAttempt(token)
        }
        connectionTask = nil
        isConnecting = false
        for id in approvalOrder { approvalContinuations.removeValue(forKey: id)?.resume(returning: false) }
        approvalOrder.removeAll()
        approvalPeerNames.removeAll()
        approvalPeerFingerprints.removeAll()
        pendingConnectionPeerName = nil
        pendingConnectionPeerFingerprint = nil
    }

    private func disconnectAll(failureReason: String? = nil, preservePendingRequests: Bool = false) {
        cancelConnectionAttempts()
        for (peerID, token) in peerTokens {
            activeConnectionTokens.remove(token)
            readerTasksByPeer.removeValue(forKey: peerID)?.cancel()
            channelsByPeer.removeValue(forKey: peerID)?.close()
        }
        peerTokens.removeAll()
        reconnectReplacementTokens.removeAll()
        for slot in pendingGroupControlWaiters.values { slot.resolve(.failure(ConnectionError.closed)) }
        for slot in pendingGroupTextAckWaiters.values { slot.resolve(.failure(ConnectionError.closed)) }
        pendingGroupControlWaiters.removeAll()
        pendingGroupTextAckWaiters.removeAll()
        if !preservePendingRequests { pendingGroupInvitations.removeAll() }
        channelsByPeer.removeAll()
        directPeerIDs.removeAll()
        directConnectionTokens.removeAll()
        purposeByToken.removeAll()
        groupPeerIDs.removeAll()
        readerTasksByPeer.removeAll()
        transportByToken.values.forEach { $0.close() }
        transportByToken.removeAll()
        for index in messages.indices where messages[index].isPending {
            messages[index].status = messages[index].interruptedStatus
            messages[index].progress = 0
            if let failureReason { messages[index].failureReason = failureReason }
        }
        for (key, continuation) in fileDecisions {
            markFileDeclined(key.transferID, peerID: key.peerID, reason: "连接已断开，未开始接收。")
            continuation.resume(returning: false)
        }
        fileDecisions.removeAll()
        saveMessages()
        receivingPeers.removeAll()
        connectedPeerIDs.removeAll()
        isConnected = false
        isConnecting = false
        isSending = false
        isReceivingFile = false
        isSendingText = false
        channel = nil
        connectionTask = nil
        readerTask = nil
        sendTask = nil
        textTask = nil
        connectedPeerName = ""
        connectedPeerID = ""
        connectionState = "未连接"
    }

    func shutdown() {
        outboxExpiryTimer?.cancel()
        outboxExpiryTimer = nil
        started = false
        identityPreparationID = UUID()
        identityPreparationTask?.cancel()
        identityPreparationTask = nil
        isPreparingIdentity = false
        isIdentityReady = false
        preparedIdentity = nil
        disconnectAll()
        stopDiscovery()
        networkRefreshTask?.cancel()
        networkRefreshTask = nil
        networkMonitor?.cancel()
        networkMonitor = nil
        updates?.stop()
    }

    var totalTransferredBytes: Int64 {
        records.reduce(0) { sum, record in
            let (result, overflow) = sum.addingReportingOverflow(max(0, record.size))
            return overflow ? Int64.max : result
        }
    }

    func addRecord(_ record: TransferRecord) {
        let updated = Array(([record] + records).prefix(1000))
        do { try persist(updated, to: historyURL); records = updated }
        catch { errorMessage = "文件已传输，但保存记录失败：\(error.localizedDescription)" }
    }

    /// Clears the transfer record list only; received files and conversation text stay on disk.
    func clearHistory() throws {
        try persist([TransferRecord](), to: historyURL)
        records = []
    }

    var groupEligiblePeers: [ChatPeer] {
        chatPeers.filter { (channelsByPeer[$0.id]?.protocolVersion ?? 0) >= 4 }
    }

    /// Why a conversation peer can or cannot be invited; peers that cannot are still listed in the picker.
    func groupInviteAvailability(for peerID: String) -> GroupMemberPickerAvailability {
        guard let channel = channelsByPeer[peerID] else { return .notConnected }
        return channel.protocolVersion >= 4 ? .available : .unsupportedVersion
    }

    var chatPeers: [ChatPeer] {
        var peers: [String: ChatPeer] = [:]
        for message in messages {
            peers[message.peerID] = ChatPeer(id: message.peerID, name: message.peerName, preview: message.preview, date: message.date)
        }
        for (id, peer) in peers {
            if let device = nearbyDevice(forPeer: id) {
                peers[id] = ChatPeer(id: id, name: device.displayName, preview: peer.preview, date: peer.date)
            }
        }
        for id in directPeerIDs {
            guard let connection = channelsByPeer[id] else { continue }
            let prior = peers[id]
            peers[id] = ChatPeer(id: id, name: connection.peerName,
                                 preview: prior?.preview ?? "已连接，可以开始聊天", date: prior?.date ?? Date())
        }
        return peers.values.filter { !hiddenConversationIDs.contains($0.id) }.sorted { $0.date > $1.date }
    }

    func canDeleteConversation(_ id: String) -> Bool {
        let isConnectionTarget = isConnectionPending(for: id)
        let selectedConnectionIsActive = isConnected && connectedPeerID == id
        guard !connectedPeerIDs.contains(id), !selectedConnectionIsActive, !isConnectionTarget else { return false }
        return !messages.contains { $0.peerID == id && $0.isPending }
    }

    func hideConversation(_ id: String) {
        var updated = hiddenConversationIDs
        updated.insert(id)
        do { try persistConversationState(messages: messages, hidden: updated) }
        catch { errorMessage = "无法保存会话管理更改：\(error.localizedDescription)" }
    }

    func restoreConversation(_ id: String) {
        guard hiddenConversationIDs.contains(id) else { return }
        var updated = hiddenConversationIDs
        updated.remove(id)
        do { try persistConversationState(messages: messages, hidden: updated) }
        catch { errorMessage = "无法保存会话管理更改：\(error.localizedDescription)" }
    }

    func restoreAllConversations() {
        do { try persistConversationState(messages: messages, hidden: []) }
        catch { errorMessage = "无法保存会话管理更改：\(error.localizedDescription)" }
    }

    func deleteConversation(_ id: String) throws {
        guard canDeleteConversation(id) else { throw ConversationManagementError.activeConversation }
        let remainingMessages = messages.filter { $0.peerID != id }
        var remainingHidden = hiddenConversationIDs
        remainingHidden.remove(id)
        try persistConversationState(messages: remainingMessages, hidden: remainingHidden)
        markConversationRead(id)
    }

    /// The nearby entry for a conversation: the advertised full fingerprint first, then the legacy service name.
    /// Discovery data is an unauthenticated hint; connecting still verifies the peer's identity.
    func nearbyDevice(forPeer id: String) -> LANDevice? {
        let key = AvatarID.normalizeFingerprint(id)
        if let key, let device = nearbyDevices.first(where: { AvatarID.normalizeFingerprint($0.fingerprintHint) == key }) {
            return device
        }
        let serviceName = ChatPeer(id: id, name: "", preview: "", date: Date()).serviceName
        return nearbyDevices.first { $0.serviceName == serviceName }
    }

    func mergeTargets(for id: String) -> [ChatPeer] { chatPeers.filter { $0.id != id } }

    /// Folds one conversation into another after the user confirms they are the same device, for example
    /// after that device reset its identity and appears with a new fingerprint. Never done automatically.
    func mergeConversation(_ source: String, into target: String) throws {
        guard source != target, canDeleteConversation(source) else { throw ConversationManagementError.activeConversation }
        var merged = messages
        for index in merged.indices where merged[index].peerID == source { merged[index].peerID = target }
        var hidden = hiddenConversationIDs
        hidden.remove(source)
        hidden.remove(target)
        try persistConversationState(messages: merged.sorted { $0.date < $1.date }, hidden: hidden)
        if let draft = drafts[source], (drafts[target] ?? "").isEmpty { setDraft(draft, for: target) }
        clearDraft(for: source)
        if isTrustedPeer(source) { try? setTrustedPeer(source, trusted: false) }
        markConversationRead(source)
    }

    var hiddenConversations: [ChatPeer] {
        var peers: [String: ChatPeer] = [:]
        for message in messages where hiddenConversationIDs.contains(message.peerID) {
            peers[message.peerID] = ChatPeer(id: message.peerID, name: message.peerName, preview: message.preview, date: message.date)
        }
        for (id, peer) in peers {
            if let device = nearbyDevice(forPeer: id) {
                peers[id] = ChatPeer(id: id, name: device.displayName, preview: peer.preview, date: peer.date)
            }
        }
        return peers.values.sorted { $0.date > $1.date }
    }

    @discardableResult
    func createGroup(name: String, memberPeerIDs: [String]) async throws -> UUID {
        guard let identity = preparedIdentity else { throw GroupConversationError.invalidControlMessage }
        let normalizedIDs = try memberPeerIDs.map(GroupConversationValidation.fingerprint)
        guard !normalizedIDs.isEmpty, normalizedIDs.count <= GroupConversation.maximumMemberCount - 1,
              Set(normalizedIDs).count == normalizedIDs.count,
              normalizedIDs.allSatisfy({ (channelsByPeer[$0]?.protocolVersion ?? 0) >= 4 }) else {
            throw GroupConversationError.invalidControlMessage
        }
        let localFingerprint = try GroupConversationValidation.fingerprint(identity.fingerprint)
        let owner = try GroupConversationMember(fingerprint: localFingerprint, displayName: settings.deviceName)
        let invitees = try normalizedIDs.map { id -> GroupConversationMember in
            guard let channel = channelsByPeer[id] else { throw GroupConversationError.invalidControlMessage }
            return try GroupConversationMember(fingerprint: id, displayName: channel.peerName)
        }
        let group = try GroupConversation(name: name, ownerFingerprint: localFingerprint, members: [owner] + invitees)
        guard !groups.contains(where: { $0.id == group.id }) else { throw GroupConversationError.invalidControlMessage }
        let eventIDs = Dictionary(uniqueKeysWithValues: normalizedIDs.map { ($0, UUID()) })
        let invitationTargets = try normalizedIDs.map { peerID -> GroupCommitTarget in
            let invite = try GroupControlMessage(kind: .invite, eventID: eventIDs[peerID]!,
                groupID: group.id, ownerFingerprint: localFingerprint, epoch: group.revision,
                targetFingerprint: peerID, name: group.name, roster: group.members)
            return GroupCommitTarget(peerID: peerID, message: invite)
        }
        let prepareTargets = try normalizedIDs.map { peerID -> GroupCommitTarget in
            let prepare = try GroupControlMessage(kind: .membershipPrepare, eventID: eventIDs[peerID]!,
                groupID: group.id, ownerFingerprint: localFingerprint, epoch: group.revision,
                targetFingerprint: peerID, name: group.name, roster: group.members)
            return GroupCommitTarget(peerID: peerID, message: prepare)
        }
        let preparation = PendingGroupPreparation(group: group, invitationTargets: invitationTargets,
            prepareTargets: prepareTargets,
            durableAbortTargetFingerprints: prepareTargets.compactMap {
                (channelsByPeer[$0.peerID]?.protocolVersion ?? 0) >= 5 ? $0.peerID : nil
            })
        pendingGroupSetups[group.id] = group
        pendingGroupPreparations[group.id] = preparation
        do { try persistGroupState() }
        catch {
            pendingGroupPreparations.removeValue(forKey: group.id)
            pendingGroupSetups.removeValue(forKey: group.id)
            throw error
        }
        do {
            guard try await retryPendingGroupPreparation(group.id) else {
                throw ConnectionError.closed
            }
            return group.id
        } catch {
            errorMessage = "群组创建事务已持久化，等待成员恢复后重试：\(error.localizedDescription)"
            throw GroupConversationError.groupPreparationPending
        }
    }

    func addGroupMember(_ fingerprint: String, to groupID: UUID) async throws {
        try await addGroupMembers([fingerprint], to: groupID)
    }

    /// Adds every selected device in one roster transaction, so all invitees join under the same revision.
    func addGroupMembers(_ fingerprints: [String], to groupID: UUID) async throws {
        guard let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity,
              group.ownerFingerprint == identity.fingerprint,
              !fingerprints.isEmpty, Set(fingerprints).count == fingerprints.count,
              group.members.count + fingerprints.count <= GroupConversation.maximumMemberCount else {
            throw GroupConversationError.invalidControlMessage
        }
        let newMembers = try fingerprints.map { fingerprint -> GroupConversationMember in
            guard !group.members.contains(where: { $0.fingerprint == fingerprint }),
                  let channel = channelsByPeer[fingerprint], channel.protocolVersion >= 4 else {
                throw GroupConversationError.invalidControlMessage
            }
            return try GroupConversationMember(fingerprint: fingerprint, displayName: channel.peerName)
        }
        try await updateGroupRoster(groupID: groupID, members: group.members + newMembers)
    }

    func removeGroupMember(_ fingerprint: String, from groupID: UUID) async throws {
        guard let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity,
              group.ownerFingerprint == identity.fingerprint,
              fingerprint != group.ownerFingerprint,
              group.members.contains(where: { $0.fingerprint == fingerprint }) else {
            throw GroupConversationError.invalidControlMessage
        }
        try await updateGroupRoster(groupID: groupID,
                                    members: group.members.filter { $0.fingerprint != fingerprint })
        if pendingGroupCommits[groupID] == nil, !directPeerIDs.contains(fingerprint) { disconnect(peerID: fingerprint) }
    }

    func leaveGroupAsOwner(_ groupID: UUID) async throws {
        guard let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity, group.ownerFingerprint == identity.fingerprint,
              pendingGroupSetups[groupID] == nil, pendingGroupCommits[groupID] == nil,
              !pendingGroupTextAckWaiters.keys.contains(where: { $0.groupID == groupID }),
              !activeGroupTextSends.contains(where: { $0.groupID == groupID }),
              !activeGroupFileSends.contains(where: { $0.groupID == groupID }),
              !groupFileQueues.values.contains(where: { queue in queue.contains { $0.envelope.groupID == groupID } }) else {
            throw GroupConversationError.invalidControlMessage
        }
        let candidates = group.members.filter { $0.fingerprint != identity.fingerprint }
        guard !candidates.isEmpty else {
            let previousGroups = groups
            groups.removeAll { $0.id == groupID }
            groupPeerIDs.removeValue(forKey: groupID)
            do { try persistGroupState() }
            catch {
                groups = previousGroups
                throw error
            }
            return
        }
        var random = SystemRandomNumberGenerator()
        guard let nextOwner = candidates.randomElement(using: &random) else {
            throw GroupConversationError.invalidControlMessage
        }
        try await updateGroupRoster(groupID: groupID, members: candidates,
                                    newOwnerFingerprint: nextOwner.fingerprint)
    }

    func requestLeaveGroup(_ groupID: UUID) {
        guard let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity,
              group.ownerFingerprint != identity.fingerprint else {
            errorMessage = "只有群成员可以申请退出群聊。"
            return
        }
        if let pending = pendingGroupLeaveMessagesByGroup[groupID]?.values.first {
            guard pending.ownerFingerprint == group.ownerFingerprint,
                  pending.targetFingerprint == identity.fingerprint else {
                errorMessage = "群主或成员身份已变化，退出申请仍待处理。"
                return
            }
            guard let ownerChannel = channelsByPeer[group.ownerFingerprint], ownerChannel.protocolVersion >= 4 else {
                errorMessage = "退出申请仍待处理，正在尝试重新连接群主。"
                connectEligibleGroupPeers()
                schedulePendingGroupLeaveReconnect(peerID: group.ownerFingerprint)
                return
            }
            sendGroupLeaveRequest(pending)
            return
        }
        guard let ownerChannel = channelsByPeer[group.ownerFingerprint], ownerChannel.protocolVersion >= 4 else {
            errorMessage = "需要连接群主后才能申请退出群聊。"
            return
        }
        do {
            let message = try GroupControlMessage(kind: .leave, groupID: groupID,
                ownerFingerprint: group.ownerFingerprint, epoch: group.revision,
                targetFingerprint: identity.fingerprint)
            pendingLeaveRequestsByGroup[groupID, default: []].insert(message.eventID)
            pendingGroupLeaveMessagesByGroup[groupID, default: [:]][message.eventID] = message
            do { try persistGroupState() }
            catch {
                pendingLeaveRequestsByGroup[groupID]?.remove(message.eventID)
                pendingGroupLeaveMessagesByGroup[groupID]?.removeValue(forKey: message.eventID)
                if pendingLeaveRequestsByGroup[groupID]?.isEmpty == true {
                    pendingLeaveRequestsByGroup.removeValue(forKey: groupID)
                }
                if pendingGroupLeaveMessagesByGroup[groupID]?.isEmpty == true {
                    pendingGroupLeaveMessagesByGroup.removeValue(forKey: groupID)
                }
                errorMessage = "保存退出申请失败：\(error.localizedDescription)"
                return
            }
            refreshPendingGroupLeaveGroupIDs()
            sendGroupLeaveRequest(message)
        } catch { errorMessage = error.localizedDescription }
    }

    private func beginGroupLeaveSend(eventID: UUID, generation token: UUID) -> Bool {
        guard sendingGroupLeaveRequests[eventID] != token else { return false }
        sendingGroupLeaveRequests[eventID] = token
        return true
    }

    private func finishGroupLeaveSend(eventID: UUID, generation token: UUID) {
        if sendingGroupLeaveRequests[eventID] == token {
            sendingGroupLeaveRequests.removeValue(forKey: eventID)
        }
    }

    private func isCurrentConnection(_ peerID: String, generation token: UUID) -> Bool {
        activeConnectionTokens.contains(token) && peerTokens[peerID] == token
    }

    private func sendGroupLeaveRequest(_ message: GroupControlMessage) {
        guard let channel = channelsByPeer[message.ownerFingerprint], channel.protocolVersion >= 4,
              let token = peerTokens[message.ownerFingerprint], isCurrentConnection(message.ownerFingerprint, generation: token) else {
            errorMessage = "退出申请仍待处理，正在尝试重新连接群主。"
            schedulePendingGroupLeaveReconnect(peerID: message.ownerFingerprint)
            return
        }
        guard beginGroupLeaveSend(eventID: message.eventID, generation: token) else { return }
        Task { [weak self] in
            guard let self else { return }
            defer { self.finishGroupLeaveSend(eventID: message.eventID, generation: token) }
            guard self.isCurrentConnection(message.ownerFingerprint, generation: token) else { return }
            do {
                try await channel.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(message))
            } catch {
                guard self.isCurrentConnection(message.ownerFingerprint, generation: token) else { return }
                self.errorMessage = "发送退出申请失败，申请仍待处理：\(error.localizedDescription)"
                self.disconnect(peerID: message.ownerFingerprint, failureReason: error.localizedDescription,
                                preservePendingRequests: true)
                self.schedulePendingGroupLeaveReconnect(peerID: message.ownerFingerprint)
            }
        }
    }

    func resolveGroupLeaveRequest(_ eventID: UUID, accepted: Bool) {
        if let completedReply = completedGroupLeaveReplies[eventID] {
            let leavingPeerID = completedReply.targetFingerprint ?? ""
            if let token = peerTokens[leavingPeerID] {
                resendPendingGroupLeaveReplies(to: leavingPeerID, generation: token)
            } else {
                errorMessage = "退出回执已保存，正在尝试重连成员。"
                connectEligibleGroupPeers()
                schedulePendingGroupLeaveReconnect(peerID: leavingPeerID)
            }
            return
        }
        guard let request = pendingGroupLeaveRequests.first(where: { $0.id == eventID }) else { return }
        let leavingPeerID = request.message.targetFingerprint ?? ""
        guard channelsByPeer[leavingPeerID] != nil else {
            errorMessage = "网络未连接，正在尝试重连；连接恢复后再次处理退出申请。"
            connectEligibleGroupPeers()
            return
        }
        guard resolvingGroupLeavePeers.insert(leavingPeerID).inserted else { return }
        Task { [weak self] in
            guard let self else { return }
            defer { self.resolvingGroupLeavePeers.remove(leavingPeerID) }
            guard self.channelsByPeer[leavingPeerID] != nil,
                  let group = self.groups.first(where: { $0.id == request.message.groupID }) else {
                self.errorMessage = "网络或群组状态已变化，成员退出申请仍待处理。"
                return
            }
            let memberStillInGroup = group.members.contains { $0.fingerprint == leavingPeerID }
            let removalPending = self.groupRosterRemovesPeer(leavingPeerID, groupID: group.id)
            if accepted && removalPending {
                self.errorMessage = "成员移除事务仍在提交，完成后再次处理退出申请回执。"
                return
            }
            if !accepted && (!memberStillInGroup || removalPending) {
                self.errorMessage = "成员移除已在处理中或已完成，不能拒绝此退出申请。"
                return
            }
            var finalAccepted = accepted
            if accepted && memberStillInGroup && !removalPending {
                do {
                    try await self.updateGroupRoster(groupID: group.id,
                        members: group.members.filter { $0.fingerprint != leavingPeerID })
                    if self.groupRosterRemovesPeer(leavingPeerID, groupID: group.id) {
                        self.errorMessage = "成员移除事务仍在提交，完成后再次处理退出申请回执。"
                        return
                    }
                } catch {
                    if self.groupRosterRemovesPeer(leavingPeerID, groupID: group.id) {
                        self.errorMessage = "成员移除事务已保存，等待设备恢复后提交。"
                        return
                    }
                    finalAccepted = false
                    self.errorMessage = "处理成员退出申请失败：\(error.localizedDescription)"
                }
            }
            guard let identity = self.preparedIdentity,
                  let reply = try? GroupControlMessage(kind: .leaveReply, eventID: eventID,
                      groupID: request.message.groupID, ownerFingerprint: identity.fingerprint,
                      epoch: request.message.epoch, targetFingerprint: leavingPeerID,
                      accepted: finalAccepted) else { return }
            do { try self.persistCompletedGroupLeaveReply(reply) }
            catch {
                self.errorMessage = "保存退出回执失败：\(error.localizedDescription)"
                return
            }
            return
        }
    }

    private func persistCompletedGroupLeaveReply(_ reply: GroupControlMessage) throws {
        let previousReply = completedGroupLeaveReplies[reply.eventID]
        completedGroupLeaveReplies[reply.eventID] = reply
        do { try persistGroupState() }
        catch {
            if let previousReply { completedGroupLeaveReplies[reply.eventID] = previousReply }
            else { completedGroupLeaveReplies.removeValue(forKey: reply.eventID) }
            throw error
        }
        guard let peerID = reply.targetFingerprint,
              let token = peerTokens[peerID], channelsByPeer[peerID] != nil else {
            errorMessage = "退出回执已保存，正在尝试重连成员。"
            connectEligibleGroupPeers()
            if let peerID = reply.targetFingerprint { schedulePendingGroupLeaveReconnect(peerID: peerID) }
            return
        }
        resendPendingGroupLeaveReplies(to: peerID, generation: token)
    }

    private func memberIsInGroup(_ peerID: String, groupID: UUID) -> Bool {
        groups.first(where: { $0.id == groupID })?.members.contains(where: { $0.fingerprint == peerID }) == true
    }

    private func groupRosterRemovesPeer(_ peerID: String, groupID: UUID) -> Bool {
        let pendingGroups = [
            pendingGroupSetups[groupID],
            pendingGroupPreparations[groupID]?.group,
            pendingGroupCommits[groupID]?.group
        ].compactMap { $0 }
        return pendingGroups.contains { pending in
            !pending.members.contains(where: { $0.fingerprint == peerID })
        }
    }

    private func hasPendingGroupLeaveExchange(with peerID: String) -> Bool {
        pendingGroupLeaveRequests.contains(where: { $0.message.targetFingerprint == peerID }) ||
            pendingGroupLeaveMessagesByGroup.values.contains(where: { messages in
                messages.values.contains(where: { $0.ownerFingerprint == peerID })
            })
    }

    private func schedulePendingGroupLeaveReconnect(peerID: String) {
        guard channelsByPeer[peerID] == nil, hasPendingGroupLeaveExchange(with: peerID),
              pendingGroupLeaveReconnectTasks[peerID] == nil else { return }
        let attempt = pendingGroupLeaveReconnectAttempts[peerID] ?? 0
        let delaySeconds = min(1 << min(attempt, 6), 60)
        pendingGroupLeaveReconnectAttempts[peerID] = min(attempt + 1, 6)
        pendingGroupLeaveReconnectTasks[peerID] = Task { [weak self] in
            defer { self?.pendingGroupLeaveReconnectTasks.removeValue(forKey: peerID) }
            do { try await Task.sleep(for: .seconds(delaySeconds)) } catch { return }
            guard let self,
                  self.channelsByPeer[peerID] == nil,
                  self.hasPendingGroupLeaveExchange(with: peerID) else { return }
            self.connectEligibleGroupPeers()
        }
    }

    private func schedulePendingGroupLeaveReconnects() {
        let peerIDs = pendingGroupLeaveRequests.compactMap { $0.message.targetFingerprint } +
            pendingGroupLeaveMessagesByGroup.values.flatMap { $0.values.map(\.ownerFingerprint) }
        for peerID in Set(peerIDs) { schedulePendingGroupLeaveReconnect(peerID: peerID) }
    }

    private func schedulePendingGroupLeaveReconnects(groupID: UUID) {
        for request in pendingGroupLeaveRequests where request.message.groupID == groupID {
            if let peerID = request.message.targetFingerprint {
                schedulePendingGroupLeaveReconnect(peerID: peerID)
            }
        }
        if let messages = pendingGroupLeaveMessagesByGroup[groupID] {
            for message in messages.values {
                schedulePendingGroupLeaveReconnect(peerID: message.ownerFingerprint)
            }
        }
    }

    private func clearPendingGroupLeaveReconnect(peerID: String) {
        pendingGroupLeaveReconnectTasks.removeValue(forKey: peerID)?.cancel()
        pendingGroupLeaveReconnectAttempts.removeValue(forKey: peerID)
    }

    private func resendPendingGroupLeaveMessages(to peerID: String, generation token: UUID) {
        let messages = pendingGroupLeaveMessagesByGroup.values.flatMap { $0.values }
            .filter { $0.ownerFingerprint == peerID }
            .sorted { $0.eventID.uuidString < $1.eventID.uuidString }
        guard !messages.isEmpty, let channel = channelsByPeer[peerID], channel.protocolVersion >= 4 else { return }
        Task { [weak self] in
            guard let self else { return }
            for message in messages {
                guard self.isCurrentConnection(peerID, generation: token) else { return }
                guard self.beginGroupLeaveSend(eventID: message.eventID, generation: token) else { continue }
                defer { self.finishGroupLeaveSend(eventID: message.eventID, generation: token) }
                do {
                    try await channel.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(message))
                } catch {
                    guard self.isCurrentConnection(peerID, generation: token) else { return }
                    self.disconnect(peerID: peerID, failureReason: error.localizedDescription,
                                    preservePendingRequests: true)
                    self.schedulePendingGroupLeaveReconnect(peerID: peerID)
                    return
                }
            }
        }
    }

    private func resendPendingGroupLeaveReplies(to peerID: String, generation token: UUID) {
        let replies = completedGroupLeaveReplies.values
            .filter { $0.targetFingerprint == peerID }
            .sorted { $0.eventID.uuidString < $1.eventID.uuidString }
        guard !replies.isEmpty, let channel = channelsByPeer[peerID], channel.protocolVersion >= 4 else { return }
        Task { [weak self] in
            guard let self else { return }
            for reply in replies {
                guard self.isCurrentConnection(peerID, generation: token) else { return }
                guard self.sendingGroupLeaveReplies[reply.eventID] != token else { continue }
                self.sendingGroupLeaveReplies[reply.eventID] = token
                defer {
                    if self.sendingGroupLeaveReplies[reply.eventID] == token {
                        self.sendingGroupLeaveReplies.removeValue(forKey: reply.eventID)
                    }
                }
                do {
                    try await channel.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                } catch {
                    guard self.isCurrentConnection(peerID, generation: token) else { return }
                    self.errorMessage = "重发退出回执失败：\(error.localizedDescription)"
                    self.disconnect(peerID: peerID, failureReason: error.localizedDescription,
                                    preservePendingRequests: true)
                    self.schedulePendingGroupLeaveReconnect(peerID: peerID)
                    return
                }
                guard self.isCurrentConnection(peerID, generation: token) else { return }
                self.finalizeGroupLeaveReply(reply)
            }
        }
    }

    private func finalizeGroupLeaveReply(_ reply: GroupControlMessage) {
        let leavingPeerID = reply.targetFingerprint ?? ""
        pendingGroupLeaveRequests.removeAll { $0.id == reply.eventID }
        saveMessages()
        if !hasPendingGroupLeaveExchange(with: leavingPeerID) {
            clearPendingGroupLeaveReconnect(peerID: leavingPeerID)
        }
        if reply.accepted == true,
           !memberIsInGroup(leavingPeerID, groupID: reply.groupID),
           !groupRosterRemovesPeer(leavingPeerID, groupID: reply.groupID) {
            groupPeerIDs[reply.groupID]?.remove(leavingPeerID)
            if groupPeerIDs[reply.groupID]?.isEmpty == true {
                groupPeerIDs.removeValue(forKey: reply.groupID)
            }
            if !directPeerIDs.contains(leavingPeerID),
               !groupPeerIDs.values.contains(where: { $0.contains(leavingPeerID) }) {
                disconnect(peerID: leavingPeerID)
            }
        }
    }

    private func updateGroupRoster(groupID: UUID, members: [GroupConversationMember],
                                   newOwnerFingerprint: String? = nil) async throws {
        guard pendingGroupSetups[groupID] == nil,
              let current = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity, current.ownerFingerprint == identity.fingerprint,
              current.revision < UInt64.max,
              !pendingGroupTextAckWaiters.keys.contains(where: { $0.groupID == groupID }),
              !activeGroupTextSends.contains(where: { $0.groupID == groupID }),
              !activeGroupFileSends.contains(where: { $0.groupID == groupID }),
              !groupFileQueues.values.contains(where: { queue in queue.contains { $0.envelope.groupID == groupID } }) else {
             throw GroupConversationError.invalidControlMessage
         }
        let proposed = try GroupConversation(id: current.id, name: current.name,
            ownerFingerprint: newOwnerFingerprint ?? current.ownerFingerprint, revision: current.revision + 1,
            members: members,
            previousOwnerFingerprint: newOwnerFingerprint == nil ? nil : current.ownerFingerprint)
        let localID = try GroupConversationValidation.fingerprint(identity.fingerprint)
        let currentIDs = Set(current.members.map(\.fingerprint).filter { $0 != localID })
        let proposedIDs = Set(proposed.members.map(\.fingerprint).filter { $0 != localID })
        let participants = currentIDs.union(proposedIDs).sorted()
        guard !participants.isEmpty,
              participants.allSatisfy({ (channelsByPeer[$0]?.protocolVersion ?? 0) >= 4 }) else {
            throw GroupConversationError.invalidControlMessage
        }
        let eventID = UUID()
        let invitationTargets = try proposedIDs.subtracting(currentIDs).sorted().map { peerID -> GroupCommitTarget in
            let invitation = try GroupControlMessage(kind: .invite, eventID: eventID,
                groupID: groupID, ownerFingerprint: localID, epoch: proposed.revision,
                targetFingerprint: peerID, name: proposed.name, roster: proposed.members)
            return GroupCommitTarget(peerID: peerID, message: invitation)
        }
        let prepareTargets = try participants.map { peerID -> GroupCommitTarget in
            let prepare = try GroupControlMessage(kind: .membershipPrepare, eventID: eventID,
                groupID: groupID, ownerFingerprint: localID, epoch: proposed.revision,
                baseEpoch: current.revision, targetFingerprint: peerID,
                newOwnerFingerprint: newOwnerFingerprint, name: proposed.name, roster: proposed.members)
            return GroupCommitTarget(peerID: peerID, message: prepare)
        }
        let preparation = PendingGroupPreparation(group: proposed, invitationTargets: invitationTargets,
            prepareTargets: prepareTargets,
            durableAbortTargetFingerprints: prepareTargets.compactMap {
                (channelsByPeer[$0.peerID]?.protocolVersion ?? 0) >= 5 ? $0.peerID : nil
            })
        pendingGroupSetups[groupID] = proposed
        pendingGroupPreparations[groupID] = preparation
        do { try persistGroupState() }
        catch {
            pendingGroupPreparations.removeValue(forKey: groupID)
            pendingGroupSetups.removeValue(forKey: groupID)
            throw error
        }
        do {
            guard try await retryPendingGroupPreparation(groupID) else { throw ConnectionError.closed }
        } catch {
            errorMessage = "成员变更事务已持久化，等待成员恢复后重试：\(error.localizedDescription)"
            throw GroupConversationError.groupPreparationPending
        }
    }

    func acceptGroupInvitation(_ invitationID: UUID, accepted: Bool) {
        guard let index = pendingGroupInvitations.firstIndex(where: { $0.id == invitationID }) else { return }
        let invitation = pendingGroupInvitations[index]
        guard channelsByPeer[invitation.peerFingerprint] != nil else {
            errorMessage = "网络未连接，重新连接后可处理群邀请。"
            return
        }
        pendingGroupInvitations.remove(at: index)
        var finalAccepted = accepted
        if accepted {
            acceptedGroupInvitations[invitationID] = invitation.message
            do { try persistGroupState() }
            catch {
                acceptedGroupInvitations.removeValue(forKey: invitationID)
                finalAccepted = false
                errorMessage = "无法保存群邀请状态：\(error.localizedDescription)"
            }
        }
        Task { [weak self] in
            guard let self else { return }
            let restoreInvitation = {
                // A saved acceptance is final; do not expose it for a later rejection.
                guard !finalAccepted,
                      !self.pendingGroupInvitations.contains(where: { $0.id == invitation.id }) else { return }
                self.pendingGroupInvitations.append(invitation)
            }
            guard let channel = self.channelsByPeer[invitation.peerFingerprint] else {
                restoreInvitation()
                self.errorMessage = finalAccepted
                    ? "已记录接受决定，等待邀请方重试时自动回复。"
                    : "网络未连接，群邀请仍待处理。"
                return
            }
            guard let identity = self.preparedIdentity,
                  let reply = try? GroupControlMessage(kind: .inviteReply, eventID: invitation.message.eventID,
                                                       groupID: invitation.message.groupID,
                                                       ownerFingerprint: invitation.message.ownerFingerprint,
                                                       epoch: invitation.message.epoch,
                                                       targetFingerprint: identity.fingerprint,
                                                       accepted: finalAccepted),
                  let payload = try? GroupWireJSON.encode(reply) else {
                restoreInvitation()
                if finalAccepted { self.errorMessage = "已记录接受决定，等待邀请方重试时自动回复。" }
                return
            }
            do { try await channel.sendGroupFrame(.groupControl, payload: payload) }
            catch {
                restoreInvitation()
                self.errorMessage = finalAccepted
                    ? "已记录接受决定，等待邀请方重试时自动回复。"
                    : error.localizedDescription
            }
        }
    }

    func sendGroupText(_ text: String, groupID: UUID) -> Bool {
        guard canSendGroupText(groupID), let identity = preparedIdentity,
              let group = groups.first(where: { $0.id == groupID }),
              let envelope = try? GroupMessageEnvelope(groupID: group.id, senderFingerprint: identity.fingerprint,
                                                      memberRevision: group.revision, kind: .text, text: text),
              let delivery = try? GroupMessageDelivery(envelope: envelope, group: group) else { return false }
        groupMessages.append(envelope)
        groupDeliveries.append(delivery)
        do { try persistGroupState() }
        catch { errorMessage = "保存群消息失败：\(error.localizedDescription)"; return false }
        for recipient in delivery.recipients {
            let key = GroupAckKey(groupID: groupID, messageID: envelope.id,
                                  peerFingerprint: recipient.memberFingerprint)
            activeGroupTextSends.insert(key)
            Task { [weak self] in await self?.transmitGroupText(envelope, to: recipient.memberFingerprint) }
        }
        return true
    }

    func canRetryGroupMessage(_ groupID: UUID, messageID: UUID) -> Bool {
        guard pendingGroupSetups[groupID] == nil, pendingGroupMembershipChanges[groupID] == nil,
              let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity,
              let envelope = groupMessages.first(where: { $0.groupID == groupID && $0.id == messageID }),
              envelope.memberRevision == group.revision,
              envelope.senderFingerprint == identity.fingerprint,
              let delivery = groupDeliveries.first(where: { $0.groupID == groupID && $0.id == messageID }),
              delivery.isComplete,
              delivery.recipients.contains(where: { $0.status != .delivered }),
              !delivery.recipients.contains(where: { $0.status == .transferring || $0.status == .pending }) else { return false }
        let retryRecipients = delivery.recipients.filter { $0.status != .delivered }.map(\.memberFingerprint)
        if let metadata = envelope.fileMetadata {
            guard !retryRecipients.contains(where: { groupFileWorkers.contains($0) || sendingPeers.contains($0) }),
                  let path = groupFileLocations[metadata.transferID],
                  FileManager.default.fileExists(atPath: path) else { return false }
        }
        return true
    }

    func retryGroupMessage(_ groupID: UUID, messageID: UUID) {
        guard canRetryGroupMessage(groupID, messageID: messageID),
              let envelope = groupMessages.first(where: { $0.groupID == groupID && $0.id == messageID }),
              let index = groupDeliveries.firstIndex(where: { $0.groupID == groupID && $0.id == messageID }) else { return }
        let recipients = groupDeliveries[index].recipients.filter { $0.status != .delivered }.map(\.memberFingerprint)
        for peerID in recipients {
            do { try groupDeliveries[index].update(memberFingerprint: peerID, status: .pending) }
            catch { errorMessage = error.localizedDescription; return }
        }
        do { try persistGroupState() }
        catch { errorMessage = "保存群消息重试状态失败：\(error.localizedDescription)"; return }
        for peerID in recipients {
            if let metadata = envelope.fileMetadata, let path = groupFileLocations[metadata.transferID] {
                enqueueGroupFile(envelope, from: URL(fileURLWithPath: path), to: peerID)
            } else {
                activeGroupTextSends.insert(GroupAckKey(groupID: groupID, messageID: envelope.id,
                                                        peerFingerprint: peerID))
                Task { [weak self] in await self?.transmitGroupText(envelope, to: peerID) }
            }
        }
    }

    func canSendGroupText(_ groupID: UUID) -> Bool {
        guard pendingGroupSetups[groupID] == nil, pendingGroupMembershipChanges[groupID] == nil,
              let group = groups.first(where: { $0.id == groupID }),
              let identity = preparedIdentity,
              group.members.contains(where: { $0.fingerprint == identity.fingerprint }) else { return false }
        return true
    }

    private func transmitGroupText(_ envelope: GroupMessageEnvelope, to peerID: String) async {
        let key = GroupAckKey(groupID: envelope.groupID, messageID: envelope.id, peerFingerprint: peerID)
        defer { activeGroupTextSends.remove(key) }
        guard let channel = channelsByPeer[peerID], channel.protocolVersion >= 4,
              let token = peerTokens[peerID] else {
            updateGroupDelivery(key, status: .unreachable, reason: "成员当前未连接")
            return
        }
        let slot = GroupResponseSlot<GroupTextAck>()
        pendingGroupTextAckWaiters[key] = slot
        do {
            try await channel.sendGroupFrame(.groupText, payload: GroupWireJSON.encode(envelope))
            let ack = try await slot.wait(timeout: .seconds(30))
            guard activeConnectionTokens.contains(token), ack.groupID == key.groupID,
                  ack.messageID == key.messageID, ack.epoch == envelope.memberRevision else {
                throw GroupConversationError.invalidControlMessage
            }
            updateGroupDelivery(key, status: ack.accepted ? .delivered : .declined,
                                reason: ack.accepted ? nil : (ack.reason ?? "成员拒绝此消息"))
        } catch {
            updateGroupDelivery(key, status: .unreachable, reason: error.localizedDescription)
        }
        pendingGroupTextAckWaiters.removeValue(forKey: key)
    }

    private func transmitGroupFile(_ envelope: GroupMessageEnvelope, from url: URL, to peerID: String) async {
        guard let metadata = envelope.fileMetadata else { return }
        let key = GroupAckKey(groupID: envelope.groupID, messageID: envelope.id, peerFingerprint: peerID)
        guard let channel = channelsByPeer[peerID], channel.protocolVersion >= 4,
              let token = peerTokens[peerID] else {
            updateGroupDelivery(key, status: .unreachable, reason: "成员当前未连接")
            return
        }
        activeGroupFileSends.insert(key)
        sendingPeers.insert(peerID)
        isSending = true
        do {
            _ = try await channel.sendGroupFile(url, groupID: envelope.groupID, messageID: envelope.id,
                senderFingerprint: envelope.senderFingerprint, memberRevision: envelope.memberRevision,
                transferID: UUID(), kind: envelope.kind == .image ? .image : .file,
                progress: { [weak self] count, total in
                    await self?.updateGroupFileDelivery(key, progress: total == 0 ? 1 : Double(count) / Double(total))
                })
            guard activeConnectionTokens.contains(token) else { throw ConnectionError.closed }
            updateGroupFileDelivery(key, progress: 1, complete: true)
            let memberName = channelsByPeer[peerID]?.peerName ?? peerID
            addRecord(TransferRecord(fileName: metadata.fileName, size: Int64(metadata.size), direction: .sent,
                                     peerName: "\(memberName) · 群聊"))
        } catch {
            if let transferError = error as? TransferSessionError, case .peerRejected = transferError {
                updateGroupDelivery(key, status: .declined, reason: "成员拒绝接收文件")
            } else {
                updateGroupDelivery(key, status: activeConnectionTokens.contains(token) ? .failed : .unreachable,
                                    reason: error.localizedDescription)
            }
        }
        sendingPeers.remove(peerID)
        activeGroupFileSends.remove(key)
        isSending = !sendingPeers.isEmpty
    }

    private func updateGroupFileDelivery(_ key: GroupAckKey, progress: Double, complete: Bool = false) {
        guard let index = groupDeliveries.firstIndex(where: { $0.groupID == key.groupID && $0.id == key.messageID }),
              let member = groupDeliveries[index].recipients.first(where: { $0.memberFingerprint == key.peerFingerprint }) else { return }
        if !complete, member.status == .transferring, progress - member.progress < 0.01 { return }
        do {
            try groupDeliveries[index].update(memberFingerprint: key.peerFingerprint,
                status: complete ? .delivered : .transferring,
                progress: complete ? 1 : min(1, max(0, progress)))
            try persistGroupState()
        } catch { errorMessage = "更新群文件进度失败：\(error.localizedDescription)" }
    }

    private func hasDurableAbortIntent(groupID: UUID, eventIDs: Set<UUID>) -> Bool {
        let owner = preparedIdentity?.fingerprint
        if let pending = pendingGroupAborts[groupID], pending.targets.contains(where: {
            eventIDs.contains($0.message.eventID)
        }) { return true }
        return groupAbortTombstones.values.contains {
            $0.accepted && $0.abort.groupID == groupID && $0.abort.ownerFingerprint == owner &&
            eventIDs.contains($0.abort.eventID)
        }
    }

    func canCancelPendingGroupPreparation(_ groupID: UUID) -> Bool {
        guard let preparation = pendingGroupPreparations[groupID], pendingGroupCommits[groupID] == nil else {
            return false
        }
        let durablePeers = Set(preparation.durableAbortTargetFingerprints ?? [])
        return preparation.prepareTargets.allSatisfy { target in
            if let channel = channelsByPeer[target.peerID] { return channel.protocolVersion >= 5 }
            return durablePeers.contains(target.peerID)
        }
    }

    func cancelPendingGroupPreparation(_ groupID: UUID) async throws -> Bool {
        guard pendingGroupCommits[groupID] == nil else {
            throw GroupConversationError.invalidControlMessage
        }
        if pendingGroupAborts[groupID] != nil { return await retryPendingGroupAbort(groupID) }
        guard let preparation = pendingGroupPreparations[groupID],
              let identity = preparedIdentity else { throw GroupConversationError.invalidControlMessage }
        let targets = preparation.prepareTargets
        let durablePeers = Set(preparation.durableAbortTargetFingerprints ?? [])
        guard !targets.isEmpty,
              targets.allSatisfy({ target in
                  let supportsAbort: Bool
                  if let channel = channelsByPeer[target.peerID] { supportsAbort = channel.protocolVersion >= 5 }
                  else { supportsAbort = durablePeers.contains(target.peerID) }
                  return supportsAbort && target.message.targetFingerprint == target.peerID &&
                      target.message.ownerFingerprint == identity.fingerprint
              }) else {
            throw GroupConversationError.invalidControlMessage
        }
        let abortTargets = try targets.map { target -> GroupCommitTarget in
            let prepare = target.message
            let abort = try GroupControlMessage(kind: .membershipAbort, eventID: prepare.eventID,
                groupID: prepare.groupID, ownerFingerprint: prepare.ownerFingerprint,
                epoch: prepare.epoch, baseEpoch: prepare.baseEpoch,
                targetFingerprint: target.peerID)
            return GroupCommitTarget(peerID: target.peerID, message: abort)
        }
        let pending = PendingGroupAbort(group: preparation.group,
            ownerFingerprint: identity.fingerprint, targets: abortTargets, acknowledgedPeers: [])
        pendingGroupAborts[groupID] = pending
        pendingGroupPreparations.removeValue(forKey: groupID)
        pendingGroupSetups[groupID] = preparation.group
        do { try persistGroupState() }
        catch {
            pendingGroupAborts.removeValue(forKey: groupID)
            pendingGroupPreparations[groupID] = preparation
            pendingGroupSetups[groupID] = preparation.group
            throw error
        }
        return await retryPendingGroupAbort(groupID)
    }

    @discardableResult
    func retryPendingGroupAbort(_ groupID: UUID) async -> Bool {
        guard let pending = pendingGroupAborts[groupID], retryingGroupAborts.insert(groupID).inserted else {
            return false
        }
        defer { retryingGroupAborts.remove(groupID) }
        do {
            for target in pending.targets where !pending.acknowledgedPeers.contains(target.peerID) {
                guard (channelsByPeer[target.peerID]?.protocolVersion ?? 0) >= 5 else { return false }
                let reply = try await sendGroupControlAndWait(target.message, to: target.peerID,
                                                               responseKind: .membershipAbortReply)
                guard reply.accepted == true,
                      pendingGroupAborts[groupID]?.acknowledgedPeers.contains(target.peerID) == true else {
                    return false
                }
            }
            guard let updated = pendingGroupAborts[groupID],
                  updated.targets.allSatisfy({ updated.acknowledgedPeers.contains($0.peerID) }) else { return false }
            pendingGroupAborts.removeValue(forKey: groupID)
            pendingGroupSetups.removeValue(forKey: groupID)
            do { try persistGroupState() }
            catch {
                pendingGroupAborts[groupID] = updated
                pendingGroupSetups[groupID] = updated.group
                throw error
            }
            return true
        } catch {
            errorMessage = "群组取消等待成员恢复，取消意图已保存：\(error.localizedDescription)"
            return false
        }
    }

    private func retryPendingGroupAborts() async {
        for groupID in pendingGroupAborts.keys.sorted(by: { $0.uuidString < $1.uuidString }) {
            _ = await retryPendingGroupAbort(groupID)
        }
    }

    private func retryPendingGroupPreparations() async {
        let groupIDs = pendingGroupPreparations.keys.sorted { $0.uuidString < $1.uuidString }
        for groupID in groupIDs {
            do { _ = try await retryPendingGroupPreparation(groupID) }
            catch { errorMessage = "群组成员变更仍待恢复：\(error.localizedDescription)" }
        }
    }

    @discardableResult
    func retryPendingGroupPreparation(_ groupID: UUID) async throws -> Bool {
        guard let preparation = pendingGroupPreparations[groupID],
              retryingGroupPreparations.insert(groupID).inserted else { return false }
        defer { retryingGroupPreparations.remove(groupID) }

        let allTargets = preparation.invitationTargets + preparation.prepareTargets
        guard Set(allTargets.map(\.peerID)).allSatisfy({ (channelsByPeer[$0]?.protocolVersion ?? 0) >= 4 }) else {
            return false
        }
        let eventIDs = Set(preparation.prepareTargets.map { $0.message.eventID })
        let invitations = preparation.invitationTargets.map { ($0.peerID, $0.message) }
        if !invitations.isEmpty {
            try await sendGroupControlsAndWait(invitations, responseKind: .inviteReply)
            if hasDurableAbortIntent(groupID: groupID, eventIDs: eventIDs) { return false }
        }
        let prepares = preparation.prepareTargets.map { ($0.peerID, $0.message) }
        try await sendGroupControlsAndWait(prepares, responseKind: .membershipPrepareReply)
        guard !hasDurableAbortIntent(groupID: groupID, eventIDs: eventIDs) else { return false }
        let commits = try preparation.prepareTargets.map { target -> (String, GroupControlMessage) in
            let prepare = target.message
            let commit = try GroupControlMessage(kind: .membershipCommit, eventID: prepare.eventID,
                groupID: prepare.groupID, ownerFingerprint: prepare.ownerFingerprint,
                epoch: prepare.epoch, baseEpoch: prepare.baseEpoch,
                targetFingerprint: prepare.targetFingerprint,
                newOwnerFingerprint: prepare.newOwnerFingerprint,
                name: prepare.name, roster: prepare.roster)
            return (target.peerID, commit)
        }
        try await beginGroupCommit(preparation.group, controls: commits)
        return true
    }

    private func beginGroupCommit(_ group: GroupConversation, controls: [(String, GroupControlMessage)]) async throws {
        let eventIDs = Set(controls.map { $0.1.eventID })
        guard pendingGroupAborts[group.id] == nil,
              !hasDurableAbortIntent(groupID: group.id, eventIDs: eventIDs),
              pendingGroupPreparations[group.id] != nil else { throw GroupConversationError.invalidControlMessage }
        let targets = controls.map { GroupCommitTarget(peerID: $0.0, message: $0.1) }
        let preparation = pendingGroupPreparations.removeValue(forKey: group.id)
        pendingGroupSetups[group.id] = group
        pendingGroupCommits[group.id] = PendingGroupCommit(group: group, targets: targets)
        do { try persistGroupState() }
        catch {
            pendingGroupCommits.removeValue(forKey: group.id)
            if let preparation { pendingGroupPreparations[group.id] = preparation }
            pendingGroupSetups[group.id] = group
            throw error
        }
        if !(await retryPendingGroupCommit(group.id)) {
            errorMessage = "群组成员变更已进入提交阶段，等待所有成员重新连接后继续提交。"
        }
    }

    private func retryPendingGroupCommits() async {
        let groupIDs = pendingGroupCommits.keys.sorted { $0.uuidString < $1.uuidString }
        for groupID in groupIDs { _ = await retryPendingGroupCommit(groupID) }
    }

    @discardableResult
    func retryPendingGroupCommit(_ groupID: UUID) async -> Bool {
        guard let pending = pendingGroupCommits[groupID], retryingGroupCommits.insert(groupID).inserted else {
            return false
        }
        defer { retryingGroupCommits.remove(groupID) }
        do {
            let controls = pending.targets.map { ($0.peerID, $0.message) }
            try await sendGroupControlsAndWait(controls, responseKind: .membershipCommitReply)
            let previousGroup = groups.first(where: { $0.id == groupID })
            let previousMembers = previousGroup?.members ?? []
            let previousPeerIDs = groupPeerIDs[groupID]
            if let index = groups.firstIndex(where: { $0.id == groupID }) { groups[index] = pending.group }
            else { groups.append(pending.group) }
            let localID = preparedIdentity?.fingerprint ?? ""
            let localRemainsInGroup = pending.group.members.contains { $0.fingerprint == localID }
            if localRemainsInGroup {
                groupPeerIDs[groupID] = Set(pending.group.members.map(\.fingerprint).filter { $0 != localID })
            } else {
                groupPeerIDs.removeValue(forKey: groupID)
            }
            pendingGroupCommits.removeValue(forKey: groupID)
            pendingGroupSetups.removeValue(forKey: groupID)
            do { try persistGroupState() }
            catch {
                if let previousGroup {
                    if let index = groups.firstIndex(where: { $0.id == groupID }) { groups[index] = previousGroup }
                } else {
                    groups.removeAll { $0.id == groupID }
                }
                if let previousPeerIDs { groupPeerIDs[groupID] = previousPeerIDs }
                else { groupPeerIDs.removeValue(forKey: groupID) }
                pendingGroupCommits[groupID] = pending
                pendingGroupSetups[groupID] = pending.group
                throw error
            }
            let remainingIDs = localRemainsInGroup ? Set(pending.group.members.map(\.fingerprint)) : Set<String>()
            // Keep a departing peer connected until its leave reply is delivered.
            for member in previousMembers where !remainingIDs.contains(member.fingerprint) &&
                !directPeerIDs.contains(member.fingerprint) &&
                !groupPeerIDs.values.contains(where: { $0.contains(member.fingerprint) }) &&
                !pendingGroupLeaveRequests.contains(where: {
                    $0.message.groupID == groupID && $0.message.targetFingerprint == member.fingerprint
                }) {
                disconnect(peerID: member.fingerprint)
            }
            connectEligibleGroupPeers()
            return true
        } catch {
            errorMessage = "群组成员变更等待设备恢复：\(error.localizedDescription)"
            return false
        }
    }

    private func sendGroupControlsAndWait(_ controls: [(String, GroupControlMessage)],
                                          responseKind: GroupControlKind) async throws {
        let tasks = controls.map { peerID, message in
            Task { @MainActor [weak self] in
                guard let self else { throw CancellationError() }
                return try await self.sendGroupControlAndWait(message, to: peerID, responseKind: responseKind)
            }
        }
        var firstError: Error?
        for task in tasks {
            do {
                let reply = try await task.value
                if reply.accepted != true, firstError == nil {
                    firstError = GroupConversationError.invalidControlMessage
                }
            } catch {
                if firstError == nil { firstError = error }
            }
        }
        if let firstError { throw firstError }
    }

    private func sendGroupControlAndWait(_ message: GroupControlMessage, to peerID: String,
                                         responseKind: GroupControlKind) async throws -> GroupControlMessage {
        guard let channel = channelsByPeer[peerID], channel.protocolVersion >= 4 else {
            throw GroupConversationError.invalidControlMessage
        }
        let key = GroupControlWaitKey(peerFingerprint: peerID, eventID: message.eventID,
                                      responseKind: responseKind)
        let slot = GroupResponseSlot<GroupControlMessage>()
        pendingGroupControlWaiters[key] = slot
        do {
            try await channel.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(message))
            let timeout: Duration = message.kind == .membershipPrepare ? .seconds(120) : .seconds(30)
            let reply = try await slot.wait(timeout: timeout)
            pendingGroupControlWaiters.removeValue(forKey: key)
            guard reply.kind == responseKind, reply.eventID == message.eventID,
                  reply.groupID == message.groupID, reply.ownerFingerprint == message.ownerFingerprint,
                  reply.epoch == message.epoch, reply.targetFingerprint == peerID else { throw GroupConversationError.invalidControlMessage }
            return reply
        } catch {
            pendingGroupControlWaiters.removeValue(forKey: key)
            throw error
        }
    }

    private func receiveGroupFrame(_ type: TransferMessageType, payload: Data, peer: String,
                                  peerID: String, generation token: UUID) async throws {
        guard activeConnectionTokens.contains(token), (channelsByPeer[peerID]?.protocolVersion ?? 0) >= 4 else {
            throw ConnectionError.closed
        }
        switch type {
        case .groupControl:
            let message = try GroupControlMessage.decodeStrict(payload)
            try await receiveGroupControl(message, peer: peer, peerID: peerID)
        case .groupText:
            let envelope = try GroupMessageEnvelope.decodeStrict(payload)
            guard envelope.senderFingerprint == peerID,
                  pendingGroupMembershipChanges[envelope.groupID] == nil,
                  let group = groups.first(where: { $0.id == envelope.groupID }),
                  envelope.memberRevision == group.revision,
                  group.members.contains(where: { $0.fingerprint == peerID }) else {
                throw GroupConversationError.senderNotMember
            }
            if !groupMessages.contains(where: { $0.groupID == envelope.groupID && $0.id == envelope.id &&
                                                  $0.senderFingerprint == envelope.senderFingerprint }) {
                groupMessages.append(envelope)
                try persistGroupState()
                let sender = channelsByPeer[peerID]?.peerName ?? peer
                noteIncoming(conversation: Self.groupConversationKey(group.id), title: "\(group.name) · \(sender)",
                             body: envelope.text ?? "新消息", notify: true)
            }
            let ack = try GroupTextAck(groupID: envelope.groupID, messageID: envelope.id,
                                       epoch: envelope.memberRevision, accepted: true)
            try await channelsByPeer[peerID]?.sendGroupFrame(.groupTextAck, payload: GroupWireJSON.encode(ack))
        case .groupTextAck:
            let ack = try GroupTextAck.decodeStrict(payload)
            let key = GroupAckKey(groupID: ack.groupID, messageID: ack.messageID, peerFingerprint: peerID)
            guard let slot = pendingGroupTextAckWaiters[key] else { throw GroupConversationError.invalidControlMessage }
            slot.resolve(.success(ack))
        default:
            throw TransferCoordinatorError.wrongMessage
        }
    }

    private func receiveGroupControl(_ message: GroupControlMessage, peer: String, peerID: String) async throws {
        guard let local = preparedIdentity else { throw GroupConversationError.invalidControlMessage }
        if message.kind == .membershipAbort || message.kind == .membershipAbortReply {
            guard (channelsByPeer[peerID]?.protocolVersion ?? 0) >= 5 else {
                throw GroupConversationError.invalidControlMessage
            }
        }
        switch message.kind {
        case .invite:
            if let tombstone = groupAbortTombstones[message.eventID] {
                guard message.ownerFingerprint == peerID, message.targetFingerprint == local.fingerprint,
                      tombstone.abort.groupID == message.groupID,
                      tombstone.abort.ownerFingerprint == message.ownerFingerprint,
                      tombstone.abort.epoch == message.epoch,
                      tombstone.abort.targetFingerprint == local.fingerprint else {
                    throw GroupConversationError.invalidControlMessage
                }
                let reply = try GroupControlMessage(kind: .inviteReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                    epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: false)
                try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                return
            }
            guard message.ownerFingerprint == peerID, message.targetFingerprint == local.fingerprint,
                  let roster = message.roster, let name = message.name,
                  roster.contains(where: { $0.fingerprint == peerID }),
                  roster.contains(where: { $0.fingerprint == local.fingerprint }) else {
                throw GroupConversationError.invalidControlMessage
            }
            if let existing = groups.first(where: { $0.id == message.groupID }) {
                guard existing.ownerFingerprint == message.ownerFingerprint, existing.revision == message.epoch,
                      existing.name == name, existing.members == roster else {
                    throw GroupConversationError.invalidControlMessage
                }
                let reply = try GroupControlMessage(kind: .inviteReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                    epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: true)
                try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                return
            }
            if let accepted = acceptedGroupInvitations[message.eventID] {
                guard accepted.groupID == message.groupID, accepted.ownerFingerprint == message.ownerFingerprint,
                      accepted.roster == roster, accepted.epoch == message.epoch else {
                    throw GroupConversationError.invalidControlMessage
                }
                let reply = try GroupControlMessage(kind: .inviteReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                    epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: true)
                try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                return
            }
            if let pending = pendingGroupInvitations.first(where: { $0.id == message.eventID }) {
                guard pending.message == message, pending.peerFingerprint == peerID else {
                    throw GroupConversationError.invalidControlMessage
                }
                return
            }
            pendingGroupInvitations.append(PendingGroupInvitation(message: message,
                peerFingerprint: peerID, peerName: peer))
            _ = try GroupConversation(id: message.groupID, name: name, ownerFingerprint: message.ownerFingerprint,
                                      revision: message.epoch, members: roster)
        case .inviteReply, .membershipPrepareReply, .membershipCommitReply:
            guard message.ownerFingerprint == local.fingerprint, message.targetFingerprint == peerID else {
                throw GroupConversationError.invalidControlMessage
            }
            let key = GroupControlWaitKey(peerFingerprint: peerID, eventID: message.eventID,
                                          responseKind: message.kind)
            guard let slot = pendingGroupControlWaiters[key] else { throw GroupConversationError.invalidControlMessage }
            slot.resolve(.success(message))
        case .membershipAbortReply:
            guard (channelsByPeer[peerID]?.protocolVersion ?? 0) >= 5,
                  message.ownerFingerprint == local.fingerprint, message.targetFingerprint == peerID else {
                throw GroupConversationError.invalidControlMessage
            }
            let key = GroupControlWaitKey(peerFingerprint: peerID, eventID: message.eventID,
                                          responseKind: message.kind)
            if var pending = pendingGroupAborts[message.groupID], pending.ownerFingerprint == message.ownerFingerprint,
               let target = pending.targets.first(where: { $0.peerID == peerID && $0.message.eventID == message.eventID }),
               target.message.groupID == message.groupID, target.message.epoch == message.epoch {
                if pending.acknowledgedPeers.contains(peerID), message.accepted != true {
                    throw GroupConversationError.invalidControlMessage
                }
                if message.accepted == true && !pending.acknowledgedPeers.contains(peerID) {
                    let old = pending
                    pending.acknowledgedPeers.insert(peerID)
                    pendingGroupAborts[message.groupID] = pending
                    groupAbortTombstones[message.eventID] = GroupAbortTombstone(abort: target.message, accepted: true)
                    do { try persistGroupState() }
                    catch {
                        pendingGroupAborts[message.groupID] = old
                        groupAbortTombstones.removeValue(forKey: message.eventID)
                        throw error
                    }
                }
                guard let slot = pendingGroupControlWaiters[key] else { throw GroupConversationError.invalidControlMessage }
                slot.resolve(.success(message))
            } else if let completed = groupAbortTombstones[message.eventID], completed.accepted,
                      completed.abort.groupID == message.groupID,
                      completed.abort.ownerFingerprint == message.ownerFingerprint,
                      completed.abort.targetFingerprint == peerID, completed.abort.epoch == message.epoch {
                if let slot = pendingGroupControlWaiters[key] { slot.resolve(.success(message)) }
            } else {
                throw GroupConversationError.invalidControlMessage
            }
        case .leave:
            guard message.targetFingerprint == peerID else { throw GroupConversationError.invalidControlMessage }
            if let completedReply = completedGroupLeaveReplies[message.eventID] {
                guard completedReply.groupID == message.groupID,
                      completedReply.ownerFingerprint == message.ownerFingerprint,
                      completedReply.epoch == message.epoch,
                      completedReply.targetFingerprint == peerID else {
                    throw GroupConversationError.invalidControlMessage
                }
                if let token = peerTokens[peerID] {
                    resendPendingGroupLeaveReplies(to: peerID, generation: token)
                }
                return
            }
            guard let group = groups.first(where: { $0.id == message.groupID }),
                  group.ownerFingerprint == message.ownerFingerprint else {
                throw GroupConversationError.invalidControlMessage
            }
            if let existing = pendingGroupLeaveRequests.first(where: { $0.id == message.eventID }) {
                guard existing.message == message else { throw GroupConversationError.invalidControlMessage }
                return
            }
            if group.ownerFingerprint == local.fingerprint,
               !group.members.contains(where: { $0.fingerprint == peerID }), group.revision > message.epoch {
                let reply = try GroupControlMessage(kind: .leaveReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: local.fingerprint, epoch: message.epoch,
                    targetFingerprint: peerID, accepted: true)
                try persistCompletedGroupLeaveReply(reply)
                return
            }
            guard group.revision >= message.epoch,
                  group.members.contains(where: { $0.fingerprint == peerID }) else {
                throw GroupConversationError.invalidControlMessage
            }
            if group.ownerFingerprint == local.fingerprint {
                guard message.ownerFingerprint == local.fingerprint else { throw GroupConversationError.invalidControlMessage }
                pendingGroupLeaveRequests.append(PendingGroupLeaveRequest(message: message, peerName: peer))
                do { try persistGroupState() }
                catch {
                    pendingGroupLeaveRequests.removeAll { $0.id == message.eventID }
                    throw error
                }
            }
        case .leaveReply:
            guard message.ownerFingerprint == peerID, message.targetFingerprint == local.fingerprint else {
                throw GroupConversationError.invalidControlMessage
            }
            guard pendingLeaveRequestsByGroup[message.groupID]?.contains(message.eventID) == true else { return }
            guard let group = groups.first(where: { $0.id == message.groupID }),
                  group.ownerFingerprint == peerID else {
                throw GroupConversationError.invalidControlMessage
            }
            if let request = pendingGroupLeaveMessagesByGroup[message.groupID]?[message.eventID] {
                guard request.ownerFingerprint == message.ownerFingerprint,
                      request.targetFingerprint == message.targetFingerprint,
                      request.epoch == message.epoch else { throw GroupConversationError.invalidControlMessage }
            }
            let previousIDs = pendingLeaveRequestsByGroup[message.groupID]
            let previousMessage = pendingGroupLeaveMessagesByGroup[message.groupID]?.removeValue(forKey: message.eventID)
            pendingLeaveRequestsByGroup[message.groupID]?.remove(message.eventID)
            if pendingLeaveRequestsByGroup[message.groupID]?.isEmpty == true {
                pendingLeaveRequestsByGroup.removeValue(forKey: message.groupID)
            }
            if pendingGroupLeaveMessagesByGroup[message.groupID]?.isEmpty == true {
                pendingGroupLeaveMessagesByGroup.removeValue(forKey: message.groupID)
            }
            do { try persistGroupState() }
            catch {
                pendingLeaveRequestsByGroup[message.groupID] = previousIDs
                if let previousMessage {
                    pendingGroupLeaveMessagesByGroup[message.groupID, default: [:]][message.eventID] = previousMessage
                }
                throw error
            }
            refreshPendingGroupLeaveGroupIDs()
            let hasPendingLeaveRequest = pendingLeaveRequestsByGroup[message.groupID]?.isEmpty == false
            if !hasPendingGroupLeaveExchange(with: peerID) { clearPendingGroupLeaveReconnect(peerID: peerID) }
            if message.accepted != true { errorMessage = "群主暂未批准退出申请。" }
            if message.accepted == true, !hasPendingLeaveRequest,
               !group.members.contains(where: { $0.fingerprint == local.fingerprint }) {
                groupPeerIDs[message.groupID]?.remove(peerID)
                if groupPeerIDs[message.groupID]?.isEmpty == true {
                    groupPeerIDs.removeValue(forKey: message.groupID)
                }
                if !directPeerIDs.contains(peerID),
                   !groupPeerIDs.values.contains(where: { $0.contains(peerID) }) {
                    disconnect(peerID: peerID, preservePendingRequests: true)
                }
            }
        case .membershipPrepare:
            guard message.ownerFingerprint == peerID, message.targetFingerprint == local.fingerprint,
                  let roster = message.roster, let name = message.name else {
                throw GroupConversationError.invalidControlMessage
            }
            if let tombstone = groupAbortTombstones[message.eventID] {
                guard matchesAbortRequest(tombstone.abort, message) else { throw GroupConversationError.invalidControlMessage }
                let reply = try GroupControlMessage(kind: .membershipPrepareReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                    epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: false)
                try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                return
            }
            let hasAcceptedInvite: Bool
            if let accepted = acceptedGroupInvitations[message.eventID] {
                guard accepted.groupID == message.groupID, accepted.ownerFingerprint == message.ownerFingerprint,
                      accepted.roster == roster, accepted.epoch == message.epoch else {
                    throw GroupConversationError.invalidControlMessage
                }
                hasAcceptedInvite = true
            } else {
                hasAcceptedInvite = false
            }
            if let existing = groups.first(where: { $0.id == message.groupID }) {
                if existing.revision == message.epoch {
                    let committedOwner = message.newOwnerFingerprint ?? message.ownerFingerprint
                    let committedPreviousOwner = message.newOwnerFingerprint == nil ? nil : message.ownerFingerprint
                    guard existing.ownerFingerprint == committedOwner,
                          existing.previousOwnerFingerprint == committedPreviousOwner,
                          existing.name == name, existing.members == roster else {
                        throw GroupConversationError.invalidControlMessage
                    }
                    let reply = try GroupControlMessage(kind: .membershipPrepareReply, eventID: message.eventID,
                        groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                        epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: true)
                    try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                    return
                }
                guard existing.ownerFingerprint == peerID,
                      existing.members.contains(where: { $0.fingerprint == local.fingerprint }),
                      existing.revision == message.baseEpoch, existing.revision < UInt64.max,
                      message.epoch == existing.revision + 1, existing.name == name else {
                    throw GroupConversationError.invalidControlMessage
                }
            } else if !hasAcceptedInvite {
                throw GroupConversationError.invalidControlMessage
            }
            let previousPending = pendingGroupMembershipChanges[message.groupID]
            if let pending = previousPending {
                guard pending.eventID == message.eventID, pending.ownerFingerprint == message.ownerFingerprint,
                      pending.epoch == message.epoch, pending.baseEpoch == message.baseEpoch,
                      pending.roster == message.roster, pending.name == message.name,
                       pending.newOwnerFingerprint == message.newOwnerFingerprint else {
                    throw GroupConversationError.invalidControlMessage
                }
            } else {
                pendingGroupMembershipChanges[message.groupID] = message
            }
            do { try persistGroupState() }
            catch {
                if let previousPending { pendingGroupMembershipChanges[message.groupID] = previousPending }
                else { pendingGroupMembershipChanges.removeValue(forKey: message.groupID) }
                throw error
            }
            let meshReady = await waitForGroupMesh(groupID: message.groupID, roster: roster)
            let reply = try GroupControlMessage(kind: .membershipPrepareReply, eventID: message.eventID,
                groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: meshReady)
            try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
        case .membershipCommit:
            guard message.ownerFingerprint == peerID, message.targetFingerprint == local.fingerprint,
                  let roster = message.roster, let name = message.name else {
                throw GroupConversationError.invalidControlMessage
            }
            if let tombstone = groupAbortTombstones[message.eventID] {
                guard matchesAbortRequest(tombstone.abort, message) else { throw GroupConversationError.invalidControlMessage }
                let reply = try GroupControlMessage(kind: .membershipCommitReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                    epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: false)
                try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                return
            }
            guard await waitForGroupMesh(groupID: message.groupID, roster: roster) else {
                throw GroupConversationError.invalidControlMessage
            }
            if let current = groups.first(where: { $0.id == message.groupID }),
               current.revision == message.epoch,
               current.ownerFingerprint == (message.newOwnerFingerprint ?? peerID),
               current.previousOwnerFingerprint == (message.newOwnerFingerprint == nil ? nil : peerID),
               current.name == name, current.members == roster {
                let reply = try GroupControlMessage(kind: .membershipCommitReply, eventID: message.eventID,
                    groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                    epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: true)
                try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
                return
            }
            guard let pending = pendingGroupMembershipChanges[message.groupID],
                  pending.eventID == message.eventID, pending.baseEpoch == message.baseEpoch,
                  pending.epoch == message.epoch, pending.roster == roster, pending.name == name,
                  pending.newOwnerFingerprint == message.newOwnerFingerprint else {
                throw GroupConversationError.invalidControlMessage
            }
            if let current = groups.first(where: { $0.id == message.groupID }) {
                guard current.ownerFingerprint == peerID, current.revision == message.baseEpoch else {
                    throw GroupConversationError.revisionMismatch
                }
            } else {
                guard let accepted = acceptedGroupInvitations[message.eventID],
                      accepted.groupID == message.groupID, accepted.roster == roster,
                      accepted.epoch == message.epoch else { throw GroupConversationError.invalidControlMessage }
            }
            let committed = try GroupConversation(id: message.groupID, name: name,
                ownerFingerprint: message.newOwnerFingerprint ?? message.ownerFingerprint,
                revision: message.epoch, members: roster,
                previousOwnerFingerprint: message.newOwnerFingerprint == nil ? nil : message.ownerFingerprint)
            let previousGroup = groups.first(where: { $0.id == committed.id })
            let previousGroupPeers = groupPeerIDs[committed.id]
            let previousPendingChange = pendingGroupMembershipChanges[message.groupID]
            let previousAcceptedInvitation = acceptedGroupInvitations[message.eventID]
            if let index = groups.firstIndex(where: { $0.id == committed.id }) { groups[index] = committed }
            else { groups.append(committed) }
            if roster.contains(where: { $0.fingerprint == local.fingerprint }) {
                groupPeerIDs[committed.id] = Set(roster.map(\.fingerprint).filter { $0 != local.fingerprint })
            } else {
                groupPeerIDs.removeValue(forKey: committed.id)
            }
            pendingGroupMembershipChanges.removeValue(forKey: message.groupID)
            acceptedGroupInvitations.removeValue(forKey: message.eventID)
            do { try persistGroupState() }
            catch {
                if let previousGroup, let index = groups.firstIndex(where: { $0.id == committed.id }) {
                    groups[index] = previousGroup
                } else {
                    groups.removeAll { $0.id == committed.id }
                }
                if let previousGroupPeers { groupPeerIDs[committed.id] = previousGroupPeers }
                else { groupPeerIDs.removeValue(forKey: committed.id) }
                if let previousPendingChange { pendingGroupMembershipChanges[message.groupID] = previousPendingChange }
                if let previousAcceptedInvitation { acceptedGroupInvitations[message.eventID] = previousAcceptedInvitation }
                throw error
            }
            connectEligibleGroupPeers()
            let reply = try GroupControlMessage(kind: .membershipCommitReply, eventID: message.eventID,
                groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: true)
            try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
            if !roster.contains(where: { $0.fingerprint == local.fingerprint }),
               pendingLeaveRequestsByGroup[message.groupID]?.isEmpty != false {
                let staleGroupPeers = channelsByPeer.keys.filter { peer in
                    !directPeerIDs.contains(peer) && !groupPeerIDs.values.contains(where: { $0.contains(peer) })
                }
                for stalePeer in staleGroupPeers { disconnect(peerID: stalePeer) }
            }
        case .membershipAbort:
            try await receiveDurableMembershipAbort(message, peerID: peerID, local: local)
        }
    }

    private func receiveDurableMembershipAbort(_ message: GroupControlMessage, peerID: String,
                                               local: DeviceIdentity) async throws {
        guard (channelsByPeer[peerID]?.protocolVersion ?? 0) >= 5,
              message.ownerFingerprint == peerID, message.targetFingerprint == local.fingerprint else {
            throw GroupConversationError.invalidControlMessage
        }
        let oldTombstone = groupAbortTombstones[message.eventID]
        if let oldTombstone {
            guard sameAbort(oldTombstone.abort, message) else { throw GroupConversationError.invalidControlMessage }
            let reply = try GroupControlMessage(kind: .membershipAbortReply, eventID: message.eventID,
                groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
                epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: oldTombstone.accepted)
            try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
            return
        }

        let oldPending = pendingGroupMembershipChanges[message.groupID]
        let oldInvitation = acceptedGroupInvitations[message.eventID]
        let oldPendingInvitations = pendingGroupInvitations
        let committed = groups.first(where: { $0.id == message.groupID })
        let accepted = committed.map { $0.revision < message.epoch } ?? true
        if let oldPending {
            guard matchesAbortRequest(message, oldPending) else { throw GroupConversationError.invalidControlMessage }
            if accepted { pendingGroupMembershipChanges.removeValue(forKey: message.groupID) }
        }
        if let oldInvitation {
            guard oldInvitation.groupID == message.groupID, oldInvitation.ownerFingerprint == peerID,
                  oldInvitation.epoch == message.epoch else { throw GroupConversationError.invalidControlMessage }
            if accepted { acceptedGroupInvitations.removeValue(forKey: message.eventID) }
        }
        if let invite = pendingGroupInvitations.first(where: { $0.id == message.eventID && $0.peerFingerprint == peerID }) {
            guard invite.message.groupID == message.groupID, invite.message.ownerFingerprint == peerID,
                  invite.message.epoch == message.epoch else { throw GroupConversationError.invalidControlMessage }
            if accepted { pendingGroupInvitations.removeAll { $0.id == message.eventID && $0.peerFingerprint == peerID } }
        }
        groupAbortTombstones[message.eventID] = GroupAbortTombstone(abort: message, accepted: accepted)
        do { try persistGroupState() }
        catch {
            if let oldPending { pendingGroupMembershipChanges[message.groupID] = oldPending }
            if let oldInvitation { acceptedGroupInvitations[message.eventID] = oldInvitation }
            pendingGroupInvitations = oldPendingInvitations
            groupAbortTombstones.removeValue(forKey: message.eventID)
            throw error
        }
        let reply = try GroupControlMessage(kind: .membershipAbortReply, eventID: message.eventID,
            groupID: message.groupID, ownerFingerprint: message.ownerFingerprint,
            epoch: message.epoch, targetFingerprint: local.fingerprint, accepted: accepted)
        try await channelsByPeer[peerID]?.sendGroupFrame(.groupControl, payload: GroupWireJSON.encode(reply))
    }

    private func matchesAbortRequest(_ abort: GroupControlMessage, _ request: GroupControlMessage) -> Bool {
        abort.kind == .membershipAbort &&
        (request.kind == .membershipPrepare || request.kind == .membershipCommit) &&
        abort.eventID == request.eventID && abort.groupID == request.groupID &&
        abort.ownerFingerprint == request.ownerFingerprint && abort.epoch == request.epoch &&
        abort.baseEpoch == request.baseEpoch && abort.targetFingerprint == request.targetFingerprint
    }

    private func sameAbort(_ left: GroupControlMessage, _ right: GroupControlMessage) -> Bool {
        left.kind == .membershipAbort && right.kind == .membershipAbort &&
        left.eventID == right.eventID && left.groupID == right.groupID &&
        left.ownerFingerprint == right.ownerFingerprint && left.epoch == right.epoch &&
        left.baseEpoch == right.baseEpoch && left.targetFingerprint == right.targetFingerprint
    }

    private func updateGroupDelivery(_ key: GroupAckKey, status: GroupDeliveryStatus, reason: String?) {
        guard let index = groupDeliveries.firstIndex(where: { $0.groupID == key.groupID && $0.id == key.messageID }) else { return }
        do {
            try groupDeliveries[index].update(memberFingerprint: key.peerFingerprint, status: status,
                                               progress: status == .delivered ? 1 : 0, failureReason: reason)
            try persistGroupState()
        } catch { errorMessage = "更新群消息投递状态失败：\(error.localizedDescription)" }
    }

    private func persistGroupState() throws {
        guard !conversationArchiveLoadFailed else { throw ConversationManagementError.archiveUnavailable }
        let archive = ConversationArchive(messages: retainedMessages(messages), hiddenFingerprints: hiddenConversationIDs,
                                          groups: groups, groupMessages: groupMessages,
                                          groupDeliveries: groupDeliveries, groupFileLocations: groupFileLocations,
                                           pendingGroupCommits: pendingGroupCommits.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                            pendingGroupPreparations: pendingGroupPreparations.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                           pendingGroupMembershipChanges: pendingGroupMembershipChanges,
                                           acceptedGroupInvitations: acceptedGroupInvitations,
                                            pendingGroupAborts: pendingGroupAborts.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                            groupAbortTombstones: groupAbortTombstones.values.sorted { $0.abort.eventID.uuidString < $1.abort.eventID.uuidString },
                                             pendingGroupLeaveRequests: pendingGroupLeaveRequests,
                                             pendingGroupLeaveMessages: archivedPendingGroupLeaveMessages,
                                             completedGroupLeaveReplies: archivedCompletedGroupLeaveReplies)
        try persist(archive, to: messagesURL)
    }

    func sendText(_ text: String) -> Bool {
        sendText(text, to: connectedPeerID)
    }

    @discardableResult
    func sendText(_ text: String, to peerID: String) -> Bool {
        do { _ = try ChatText(text: text) }
        catch { errorMessage = error.localizedDescription; return false }
        // Earlier pending messages go first, so a new message joins the outbox behind them.
        guard let connection = channelsByPeer[peerID], !hasOutbox(peerID) else {
            return enqueueOutbox([ChatMessage(peerID: peerID, peerName: outboxPeerName(peerID), direction: .sent,
                                              kind: .text, text: text, status: .queued, outboxSince: Date())])
        }
        guard textTasksByPeer[peerID] == nil else { return false }
        let message = ChatMessage(peerID: peerID, peerName: connection.peerName, direction: .sent,
                                  kind: .text, text: text, status: .sending)
        messages.append(message)
        saveMessages()
        transmitText(message)
        return true
    }

    private func transmitText(_ message: ChatMessage) {
        guard let connection = channelsByPeer[message.peerID], let token = peerTokens[message.peerID],
              textTasksByPeer[message.peerID] == nil else { return }
        let peerID = message.peerID
        sendingTextPeers.insert(peerID)
        if connectedPeerID == peerID { isSendingText = true }
        updateMessage(message.id, status: .sending)
        let task = Task { [self] in
            defer {
                sendingTextPeers.remove(peerID)
                if connectedPeerID == peerID { isSendingText = false; textTask = nil }
                textTasksByPeer.removeValue(forKey: peerID)
                flushOutbox(peerID)
            }
            do {
                _ = try await connection.sendText(message.text ?? "", id: message.id)
                guard activeConnectionTokens.contains(token) else { return }
                updateMessage(message.id, status: .delivered)
                noteFirstSuccessfulTransfer(title: "消息已送达", body: "\(connection.peerName) 已收到你的消息。")
            } catch {
                guard activeConnectionTokens.contains(token) else { return }
                updateMessage(message.id, status: message.interruptedStatus, failureReason: error.localizedDescription)
                disconnect(peerID: peerID, failureReason: error.localizedDescription,
                           preservePendingRequests: true)
                schedulePendingGroupLeaveReconnect(peerID: peerID)
                if !(error is CancellationError) && !isConnectionLoss(error) {
                    errorMessage = error.localizedDescription
                    shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                }
            }
        }
        textTasksByPeer[peerID] = task
        if connectedPeerID == peerID { textTask = task }
    }

    private func receiveText(_ message: ChatText, peer: String, peerID: String, generation token: UUID) throws {
        guard activeConnectionTokens.contains(token) else { throw ConnectionError.closed }
        // A lost acknowledgement may cause an explicit retry. Do not duplicate the received bubble.
        guard !messages.contains(where: { $0.id == message.id && $0.peerID == peerID && $0.direction == .received && $0.kind == .text }) else { return }
        let value = ChatMessage(id: message.id, peerID: peerID, peerName: peer, direction: .received,
                                kind: .text, text: message.text, status: .received)
        let updated = retainedMessages(messages + [value])
        var hidden = hiddenConversationIDs
        hidden.remove(peerID)
        try persistConversationState(messages: updated, hidden: hidden)
        noteIncoming(conversation: peerID, title: peer, body: value.preview, notify: true)
    }

    func retryMessage(_ message: ChatMessage) {
        guard channelsByPeer[message.peerID] != nil, !hasOutbox(message.peerID) else {
            requeueOutbox(message)
            return
        }
        if message.kind == .text { transmitText(message) }
        else if let path = message.localPath { startSending([URL(fileURLWithPath: path)], to: message.peerID) }
    }

    private func updateMessage(_ id: UUID, status: ChatMessage.Status, failureReason: String? = nil) {
        guard let index = messages.firstIndex(where: { $0.id == id && $0.direction == .sent }) else { return }
        messages[index].status = status
        messages[index].failureReason = failureReason
        if status == .delivered { messages[index].progress = 1 }
        saveMessages()
    }

    private func saveMessages() {
        do {
            guard !conversationArchiveLoadFailed else { throw ConversationManagementError.archiveUnavailable }
            // Keep all active transfers even when a large queue exceeds the history retention count.
            let updated = retainedMessages(messages)
            let archive = ConversationArchive(messages: updated, hiddenFingerprints: hiddenConversationIDs,
                                              groups: groups, groupMessages: groupMessages,
                                              groupDeliveries: groupDeliveries, groupFileLocations: groupFileLocations,
                                           pendingGroupCommits: pendingGroupCommits.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                            pendingGroupPreparations: pendingGroupPreparations.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                           pendingGroupMembershipChanges: pendingGroupMembershipChanges,
                                           acceptedGroupInvitations: acceptedGroupInvitations,
                                            pendingGroupAborts: pendingGroupAborts.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                            groupAbortTombstones: groupAbortTombstones.values.sorted { $0.abort.eventID.uuidString < $1.abort.eventID.uuidString },
                                             pendingGroupLeaveRequests: pendingGroupLeaveRequests,
                                             pendingGroupLeaveMessages: archivedPendingGroupLeaveMessages,
                                             completedGroupLeaveReplies: archivedCompletedGroupLeaveReplies)
            try persist(archive, to: messagesURL)
            if messages != updated { messages = updated }
        } catch { errorMessage = "保存会话记录失败：\(error.localizedDescription)" }
    }

    private func retainedMessages(_ values: [ChatMessage]) -> [ChatMessage] {
        let pending = values.filter(\.isPending)
        let retained = Array(values.filter { !$0.isPending }.suffix(1000))
        return (pending + retained).sorted { $0.date < $1.date }
    }

    static func isImage(_ url: URL) -> Bool {
        UTType(filenameExtension: url.pathExtension)?.conforms(to: .image) == true
    }

    private func load() {
        if let data = try? Data(contentsOf: settingsURL), let value = try? JSONDecoder().decode(DeviceSettings.self, from: data) { settings = value }
        if FileManager.default.fileExists(atPath: messagesURL.path) {
            do {
                let archive = try ConversationArchive.decode(Data(contentsOf: messagesURL))
                hiddenConversationIDs = archive.hiddenFingerprints
                groups = archive.groups
                groupMessages = archive.groupMessages
                groupFileLocations = archive.groupFileLocations
                pendingGroupMembershipChanges = archive.pendingGroupMembershipChanges
                acceptedGroupInvitations = archive.acceptedGroupInvitations
                completedGroupLeaveReplies = Dictionary(uniqueKeysWithValues:
                    archive.completedGroupLeaveReplies.map { ($0.eventID, $0) })
                let completedLeaveIDs = Set(completedGroupLeaveReplies.keys)
                pendingGroupLeaveRequests = archive.pendingGroupLeaveRequests.filter {
                    !completedLeaveIDs.contains($0.id)
                }
                for message in archive.pendingGroupLeaveMessages {
                    pendingGroupLeaveMessagesByGroup[message.groupID, default: [:]][message.eventID] = message
                    pendingLeaveRequestsByGroup[message.groupID, default: []].insert(message.eventID)
                }
                refreshPendingGroupLeaveGroupIDs()
                pendingGroupAborts = Dictionary(uniqueKeysWithValues: archive.pendingGroupAborts.map { ($0.group.id, $0) })
                groupAbortTombstones = Dictionary(uniqueKeysWithValues: archive.groupAbortTombstones.map { ($0.abort.eventID, $0) })
                for abort in archive.pendingGroupAborts {
                    pendingGroupSetups[abort.group.id] = abort.group
                }
                for commit in archive.pendingGroupCommits {
                    pendingGroupCommits[commit.group.id] = commit
                    pendingGroupSetups[commit.group.id] = commit.group
                }
                for preparation in archive.pendingGroupPreparations
                    where pendingGroupCommits[preparation.group.id] == nil && pendingGroupAborts[preparation.group.id] == nil {
                    pendingGroupPreparations[preparation.group.id] = preparation
                    pendingGroupSetups[preparation.group.id] = preparation.group
                }
                groupDeliveries = archive.groupDeliveries.map { stored in
                    var delivery = stored
                    for recipient in stored.recipients where recipient.status == .pending || recipient.status == .transferring {
                        try? delivery.update(memberFingerprint: recipient.memberFingerprint, status: .unreachable,
                                             failureReason: "应用重启前未收到成员回执")
                    }
                    return delivery
                }
                messages = archive.messages.map { value in
                    var message = value
                    if message.isPending { message.status = message.interruptedStatus; message.progress = 0 }
                    return message
                }.sorted { $0.date < $1.date }
                expireOutbox()
            } catch {
                conversationArchiveLoadFailed = true
                errorMessage = "会话记录无法读取，原文件已保留：\(error.localizedDescription)"
            }
        }
        if let data = try? Data(contentsOf: historyURL), let value = try? JSONDecoder().decode([TransferRecord].self, from: data) { records = value.sorted { $0.date > $1.date } }
    }

    private func persist<T: Encodable>(_ value: T, to url: URL) throws {
        let data = try JSONEncoder().encode(value)
        try data.write(to: url, options: .atomic)
    }

    private func persistConversationState(messages: [ChatMessage], hidden: Set<String>) throws {
        guard !conversationArchiveLoadFailed else { throw ConversationManagementError.archiveUnavailable }
        let archive = ConversationArchive(messages: retainedMessages(messages), hiddenFingerprints: hidden,
                                          groups: groups, groupMessages: groupMessages,
                                          groupDeliveries: groupDeliveries, groupFileLocations: groupFileLocations,
                                           pendingGroupCommits: pendingGroupCommits.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                            pendingGroupPreparations: pendingGroupPreparations.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                           pendingGroupMembershipChanges: pendingGroupMembershipChanges,
                                           acceptedGroupInvitations: acceptedGroupInvitations,
                                            pendingGroupAborts: pendingGroupAborts.values.sorted { $0.group.id.uuidString < $1.group.id.uuidString },
                                            groupAbortTombstones: groupAbortTombstones.values.sorted { $0.abort.eventID.uuidString < $1.abort.eventID.uuidString },
                                             pendingGroupLeaveRequests: pendingGroupLeaveRequests,
                                             pendingGroupLeaveMessages: archivedPendingGroupLeaveMessages,
                                             completedGroupLeaveReplies: archivedCompletedGroupLeaveReplies)
        try persist(archive, to: messagesURL)
        self.messages = messages
        hiddenConversationIDs = hidden
    }

    private func isNetworkTroubleshootingFailure(_ error: Error) -> Bool {
        if let transportError = error as? NetworkTransportError,
           case .connectionFailed = transportError { return true }
        if let streamError = error as? AuthenticatedStreamError,
           case .timeout = streamError { return true }
        return false
    }

    /// The peer went away mid-session; the message row and conversation status already say so.
    private func isConnectionLoss(_ error: Error) -> Bool {
        if let connectionError = error as? ConnectionError, case .closed = connectionError { return true }
        if error is NetworkTransportError { return true }
        if let streamError = error as? AuthenticatedStreamError {
            switch streamError {
            case .unexpectedEOF, .timeout, .cancelled: return true
            default: return false
            }
        }
        return false
    }
}

private final class GroupResponseSlot<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<Value, Error>?
    private var continuation: CheckedContinuation<Value, Error>?

    func resolve(_ result: Result<Value, Error>) {
        lock.lock()
        guard self.result == nil else { lock.unlock(); return }
        self.result = result
        let waiter = continuation
        continuation = nil
        lock.unlock()
        waiter?.resume(with: result)
    }

    func wait(timeout: Duration) async throws -> Value {
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            let timer = Task {
                do { try await Task.sleep(for: timeout) } catch { return }
                self.resolve(.failure(AuthenticatedStreamError.timeout))
            }
            defer { timer.cancel() }
            return try await withCheckedThrowingContinuation { waiter in
                lock.lock()
                if let result {
                    lock.unlock()
                    waiter.resume(with: result)
                } else {
                    continuation = waiter
                    lock.unlock()
                }
            }
        } onCancel: {
            self.resolve(.failure(CancellationError()))
        }
    }
}

enum ConversationManagementError: LocalizedError {
    case activeConversation, archiveUnavailable
    var errorDescription: String? {
        switch self {
        case .activeConversation: "请先断开此设备并结束未完成或待发送的传输，再删除或合并会话记录。"
        case .archiveUnavailable: "现有会话记录无法读取，因此没有覆盖原文件。请先备份并修复记录文件。"
        }
    }
}


extension FileMetadata {
    /// A folder transfer is shown as one message named after its root folder.
    var displayName: String { folder?.rootName ?? fileName }
    var displaySize: UInt64 { folder?.totalBytes ?? size }
}
