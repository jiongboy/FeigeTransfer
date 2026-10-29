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

    static var `default`: DeviceSettings {
        let name = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? FileManager.default.homeDirectoryForCurrentUser
        return DeviceSettings(deviceName: name, receiveDirectory: downloads.appendingPathComponent("Feige Received", isDirectory: true), firstRunComplete: false)
    }

    private enum CodingKeys: String, CodingKey {
        case deviceName, receiveDirectory, firstRunComplete, setupStep
        case autoCheckUpdates, notificationsEnabled, backgroundTipSeen, connectionTipSeen, legacyReviewPending
    }

    /// Memberwise initializer kept explicit because declaring `init(from:)` suppresses synthesis.
    init(deviceName: String, receiveDirectory: URL, firstRunComplete: Bool, setupStep: Int? = nil,
         autoCheckUpdates: Bool = true, notificationsEnabled: Bool = false, backgroundTipSeen: Bool = false,
         connectionTipSeen: Bool = false, legacyReviewPending: Bool = false) {
        self.deviceName = deviceName
        self.receiveDirectory = receiveDirectory
        self.firstRunComplete = firstRunComplete
        self.setupStep = setupStep
        self.autoCheckUpdates = autoCheckUpdates
        self.notificationsEnabled = notificationsEnabled
        self.backgroundTipSeen = backgroundTipSeen
        self.connectionTipSeen = connectionTipSeen
        self.legacyReviewPending = legacyReviewPending
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
    }
}

enum SettingsError: LocalizedError {
    case invalidDeviceName

    var errorDescription: String? { "设备名称为空、过长或包含换行等控制字符，请修改后重试。" }
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
    @Published private(set) var hiddenConversationIDs: Set<String> = []
    @Published var connectedPeerID = ""
    @Published var isSendingText = false
    @Published var isReceiving = false
    @Published var listenerPort: UInt16?
    @Published var nearbyDevices: [LANDevice] = []
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
    var isBusy: Bool { isConnecting || isSending || isReceivingFile }

    /// Transfers in flight across both directions; an idle connection never blocks an update.
    var hasActiveWork: Bool { isSending || isReceivingFile || isSendingText || pendingConnectionPeerName != nil }

    private let identityLoader: @Sendable () throws -> DeviceIdentity
    private let settingsURL: URL
    private let historyURL: URL
    private let messagesURL: URL
    private let draftsURL: URL
    private let supportDirectory: URL
    private let lan = LANService()
    private let notifier = TransferNotifier()
    private var networkMonitor: NWPathMonitor?
    private let networkQueue = DispatchQueue(label: "com.feige.network-monitor")
    private var networkRefreshTask: Task<Void, Never>?
    private var started = false
    private var preparedIdentity: DeviceIdentity?
    private var identityPreparationTask: Task<DeviceIdentity, Error>?
    private var identityPreparationID = UUID()
    private var generation = UUID()
    private var approvalID: UUID?
    private var approvalContinuation: CheckedContinuation<Bool, Error>?
    /// Pending file confirmations keyed by transfer id; only one file may be in flight per connection.
    private var fileDecisions: [UUID: CheckedContinuation<Bool, Never>] = [:]
    /// Time allowed to answer a file confirmation, matching the Windows client.
    static let fileApprovalTimeout: Duration = .seconds(120)
    private var transport: NetworkByteStream?
    private var channel: FileConnection?
    private var connectionTask: Task<Void, Never>?
    private var readerTask: Task<Void, Never>?
    private var sendTask: Task<Void, Never>?
    private var textTask: Task<Void, Never>?
    private var retryURLs: [URL] = []
    private var lastDevice: LANDevice?
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
        load()
        drafts = (try? Data(contentsOf: draftsURL)).flatMap { try? JSONDecoder().decode([String: String].self, from: $0) } ?? [:]
        if makeUpdates {
            let controller = UpdateController(supportDirectory: support, version: updateVersion, source: updateSource)
            updates = controller
            Task { await self.refreshNotifierStatus() }
        }
        notifierStatus = notifier.status
        notifierAvailable = notifier.isAvailable
        lan.onDevices = { [weak self] in
            self?.nearbyDevices = $0
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
            if settings.firstRunComplete { refreshDiscovery() }
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

    func saveSettings(deviceName: String, receiveDirectory: URL) throws {
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
        updated.firstRunComplete = true
        updated.setupStep = nil
        try persist(updated, to: settingsURL)
        settings = updated
        isFirstRun = false
        if started && isIdentityReady { refreshDiscovery() }
    }

    func saveSetupDraft(deviceName: String, receiveDirectory: URL, nextStep: Int) throws {
        let trimmed = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 48, trimmed.utf8.count <= 128,
              !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw SettingsError.invalidDeviceName
        }
        try validateReceiveDirectory(receiveDirectory)
        var updated = settings
        updated.deviceName = trimmed
        updated.receiveDirectory = receiveDirectory
        updated.firstRunComplete = false
        updated.setupStep = nextStep
        try persist(updated, to: settingsURL)
        settings = updated
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

    /// Rebuilds discovery after the network settles. Established sessions, the listener port and
    /// the consent stay untouched; later-mode and shutdown never restart the service.
    private func handleNetworkChange(available: Bool) {
        guard available, started, settings.firstRunComplete, isIdentityReady else { return }
        networkRefreshTask?.cancel()
        networkRefreshTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(750))
            guard let self, !Task.isCancelled else { return }
            self.localAddress = LANAddress.currentIPv4()
            self.refreshDiscovery()
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
    }

    func refreshDiscovery() {
        guard settings.firstRunComplete else { return }
        guard isIdentityReady else {
            if !isPreparingIdentity { Task { await start() } }
            return
        }
        do { try lan.start(deviceName: settings.deviceName, fingerprint: identityFingerprint) }
        catch { errorMessage = "无法启动局域网服务：\(error.localizedDescription)" }
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

    func chooseAndSendFiles(imagesOnly: Bool = false) {
        let panel = NSOpenPanel()
        panel.title = imagesOnly ? "选择要发送的图片" : "选择要发送的文件"
        if imagesOnly { panel.allowedContentTypes = [.image] }
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK { startSending(panel.urls) }
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

    func requestConnection(to device: LANDevice, pendingFiles: [URL] = []) {
        guard !isConnecting, !isConnected else { return }
        guard settings.firstRunComplete else { isFirstRun = true; return }
        guard isIdentityReady else { errorMessage = "请先完成启动时的系统钥匙串授权，再连接设备。"; return }
        lastDevice = device
        shouldSuggestNetworkHelp = false
        let stream = NetworkByteStream(endpoint: device.endpoint)
        beginConnection(stream, role: .initiator, pendingFiles: pendingFiles)
    }

    func clearError() {
        errorMessage = nil
        shouldSuggestNetworkHelp = false
    }

    func accept(_ connection: NWConnection) {
        guard !isConnected, !isConnecting, settings.firstRunComplete, isIdentityReady else { connection.cancel(); return }
        beginConnection(NetworkByteStream(connection: connection), role: .responder, pendingFiles: [])
    }

    private func beginConnection(_ stream: NetworkByteStream, role: HandshakeRole, pendingFiles: [URL]) {
        guard let identity = preparedIdentity else {
            stream.close()
            errorMessage = "设备身份尚未准备完成，请先完成系统钥匙串授权。"
            return
        }
        let token = UUID()
        generation = token
        transport = stream
        isConnecting = true
        connectionState = "正在建立局域网连接…"
        connectionTask = Task { [self] in
            do {
                try await stream.start(timeout: 10)
                let connection = try await ConnectionService.establish(transport: stream, identity: identity,
                                                                         role: role, deviceName: settings.deviceName,
                                                                         onStage: { [weak self] stage in
                    await self?.handshakeProgress(stage, role: role, generation: token)
                }) { [weak self] peer in
                    guard let self else { return false }
                    return try await self.askForConnection(peer, generation: token)
                }
                guard generation == token, !Task.isCancelled else { connection.close(); return }
                install(connection, generation: token)
                if !pendingFiles.isEmpty { startSending(pendingFiles) }
            } catch {
                guard generation == token else { return }
                let rejectedLocally = role == .responder && (error as? ConnectionError).map {
                    if case .rejected = $0 { return true }; return false
                } == true
                disconnect()
                connectionState = rejectedLocally ? "已拒绝连接" : "未连接"
                if !rejectedLocally && !(error is CancellationError) {
                    errorMessage = error.localizedDescription
                    shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                }
                if !pendingFiles.isEmpty { canRetry = true }
            }
        }
    }

    private func handshakeProgress(_ stage: HandshakeStage, role: HandshakeRole, generation token: UUID) {
        guard generation == token else { return }
        switch stage {
        case .exchangingOffers: connectionState = "正在交换设备信息…"
        case .authorizingIdentity: connectionState = "正在验证设备身份，如有钥匙串弹窗请完成授权…"
        case .waitingForPeer: connectionState = "等待对方完成身份验证或系统授权…"
        case .verified: connectionState = role == .initiator ? "等待对方同意连接…" : "请确认是否同意连接…"
        }
    }

    private func askForConnection(_ peer: PendingPeerApproval, generation token: UUID) async throws -> Bool {
        let id = UUID()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            guard generation == token else { throw CancellationError() }
            pendingConnectionPeerName = peer.peer.deviceName
            approvalID = id
            return try await withCheckedThrowingContinuation { approvalContinuation = $0 }
        } onCancel: {
            Task { @MainActor [weak self] in
                if self?.approvalID == id { self?.approveConnection(false) }
            }
        }
    }

    func approveConnection(_ accepted: Bool) {
        let continuation = approvalContinuation
        approvalContinuation = nil
        approvalID = nil
        pendingConnectionPeerName = nil
        continuation?.resume(returning: accepted)
    }

    private func install(_ connection: FileConnection, generation token: UUID) {
        channel = connection
        connectedPeerName = connection.peerName
        connectedPeerID = connection.peerFingerprint
        restoreConversation(connection.peerFingerprint)
        isConnected = true
        isConnecting = false
        connectionTask = nil
        connectionState = "已连接：\(connection.peerName)"
        markConnectionTipSeen()
        let directory = settings.receiveDirectory
        readerTask = Task { [self] in
            do {
                try await connection.run(receiveDirectory: directory, directoryProvider: { [weak self] in
                    await self?.settings.receiveDirectory ?? directory
                }, fileApproval: { [weak self] metadata in
                    guard let self else { return false }
                    return await self.requestFileApproval(metadata, peer: connection.peerName,
                                                          peerID: connection.peerFingerprint, generation: token)
                }, didReceive: { [weak self] metadata, url in
                    await self?.received(metadata, at: url, peer: connection.peerName, peerID: connection.peerFingerprint, generation: token)
                }, progress: { [weak self] metadata, count in
                    await self?.receiving(metadata, count: count, generation: token)
                }, didReceiveText: { [weak self] message in
                    guard let self else { throw ConnectionError.closed }
                    try await self.receiveText(message, peer: connection.peerName, peerID: connection.peerFingerprint, generation: token)
                })
            } catch {
                guard generation == token else { return }
                let interrupted = isSending || isReceivingFile
                if isSending { transferStatus = "发送中断"; canRetry = !retryURLs.isEmpty }
                if isReceivingFile { receiveStatus = "接收中断，未完成的文件已清理" }
                disconnect(failureReason: error.localizedDescription)
                connectionState = "连接已断开"
                if interrupted && !(error is CancellationError) {
                    errorMessage = error.localizedDescription
                    shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                }
            }
        }
    }

    /// Shows an incoming file as "等待你确认接收" and suspends until the user decides.
    ///
    /// A file is never written to disk before this returns `true`, so declining cannot leave a partial
    /// file. Asking fails closed: a disconnected connection or cancelled task declines.
    private func requestFileApproval(_ metadata: FileMetadata, peer: String, peerID: String,
                                     generation token: UUID) async -> Bool {
        guard generation == token, isConnected else { return false }
        if let index = messages.firstIndex(where: { $0.id == metadata.transferID && $0.peerID == peerID && $0.direction == .received }) {
            messages[index].status = .awaitingApproval
            messages[index].failureReason = nil
            saveMessages()
        } else {
            messages.append(ChatMessage(id: metadata.transferID, peerID: peerID, peerName: peer,
                                        direction: .received,
                                        kind: metadata.kind == .image ? .image : .file,
                                        fileName: metadata.fileName, size: Int64(metadata.size),
                                        status: .awaitingApproval))
            saveMessages()
        }
        // Files arrive one at a time per connection, so the buttons map to a single pending id.
        // Waiting expires like Windows: an unanswered request declines instead of blocking forever.
        let timeout = Task { [weak self] in
            try? await Task.sleep(for: Self.fileApprovalTimeout)
            guard !Task.isCancelled else { return }
            self?.resolveFileDecision(metadata.transferID, accepted: false, timedOut: true)
        }
        let accepted = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                guard generation == token else { continuation.resume(returning: false); return }
                fileDecisions[metadata.transferID] = continuation
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.resolveFileDecision(metadata.transferID, accepted: false, timedOut: false) }
        }
        timeout.cancel()
        if !accepted { markFileDeclined(metadata.transferID, peerID: peerID) }
        return accepted
    }

    /// Called by the message card. Accepting lets the transfer start; declining closes the connection.
    func decideIncomingFile(_ transferID: UUID, accepted: Bool) {
        resolveFileDecision(transferID, accepted: accepted, timedOut: false)
    }

    private func resolveFileDecision(_ transferID: UUID, accepted: Bool, timedOut: Bool) {
        guard let continuation = fileDecisions.removeValue(forKey: transferID) else { return }
        if timedOut { markFileDeclined(transferID, peerID: connectedPeerID, reason: "等待接收确认超时。") }
        continuation.resume(returning: accepted)
    }

    private func markFileDeclined(_ transferID: UUID, peerID: String, reason: String? = nil) {
        guard let index = messages.firstIndex(where: { $0.id == transferID && $0.peerID == peerID && $0.direction == .received }) else { return }
        messages[index].status = .declined
        messages[index].failureReason = reason ?? "已拒绝接收该文件。"
        saveMessages()
    }

    private func receiving(_ metadata: FileMetadata, count: UInt64, generation token: UUID) {
        guard generation == token else { return }
        isReceivingFile = true
        receiveStatus = "正在接收：\(metadata.fileName)"
        receiveProgress = metadata.size == 0 ? 0 : Double(count) / Double(metadata.size)
        if let index = messages.firstIndex(where: { $0.id == metadata.transferID && $0.peerID == connectedPeerID && $0.direction == .received && $0.kind != .text }) {
            if receiveProgress - messages[index].progress >= 0.01 { messages[index].progress = receiveProgress }
        } else {
            messages.append(ChatMessage(id: metadata.transferID, peerID: connectedPeerID, peerName: connectedPeerName,
                                        direction: .received, kind: metadata.kind == .image ? .image : .file,
                                        fileName: metadata.fileName, size: Int64(metadata.size), status: .sending))
        }
    }

    private func received(_ metadata: FileMetadata, at url: URL, peer: String, peerID: String, generation token: UUID) {
        guard generation == token else { return }
        addRecord(TransferRecord(fileName: url.lastPathComponent, size: Int64(metadata.size), direction: .received,
                                 peerName: peer, savedPath: url.path))
        restoreConversation(peerID)
        if let index = messages.firstIndex(where: { $0.id == metadata.transferID && $0.peerID == peerID && $0.direction == .received && $0.kind != .text }) {
            messages[index].status = .received
            messages[index].progress = 1
            messages[index].localPath = url.path
            messages[index].fileName = url.lastPathComponent
            saveMessages()
        }
        isReceivingFile = false
        receiveProgress = 1
        receiveStatus = "已接收：\(url.lastPathComponent)"
        noteFirstSuccessfulTransfer(title: "收到文件", body: "\(peer) 发送了 \(url.lastPathComponent)。")
    }

    func startSendingPastedImage(_ image: NSImage) {
        guard isConnected, !isSending else { return }
        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            errorMessage = "无法读取剪贴板中的图片。"
            return
        }
        do {
            let directory = settingsURL.deletingLastPathComponent().appendingPathComponent("PastedImages", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let url = directory.appendingPathComponent("粘贴图片-\(UUID().uuidString).png")
            try png.write(to: url, options: .atomic)
            startSending([url])
        } catch {
            errorMessage = "无法准备剪贴板图片：\(error.localizedDescription)"
        }
    }

    func startSending(_ urls: [URL]) {
        guard let connection = channel, isConnected else { errorMessage = "请先连接设备。"; return }
        guard !isSending, !urls.isEmpty else { return }
        let token = generation
        retryURLs = urls
        canRetry = false
        queueCount = urls.count
        queueCompleted = 0
        transferProgress = 0
        isSending = true
        let queued = urls.map { url in
            ChatMessage(peerID: connection.peerFingerprint, peerName: connection.peerName, direction: .sent,
                        kind: Self.isImage(url) ? .image : .file, fileName: url.lastPathComponent,
                        size: (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init),
                        localPath: url.path, status: .queued)
        }
        messages.append(contentsOf: queued)
        saveMessages()
        sendTask = Task { [self] in
            defer { if generation == token { isSending = false; sendTask = nil } }
            for (index, url) in urls.enumerated() {
                do {
                    try Task.checkCancellation()
                    transferStatus = "正在发送（\(index + 1)/\(urls.count)）：\(url.lastPathComponent)"
                    let messageID = queued[index].id
                    updateMessage(messageID, status: .sending)
                    let metadata = try await connection.sendFile(url, transferID: messageID,
                                                                kind: queued[index].kind == .image ? .image : .file) { [weak self] count, total in
                        await self?.sendingProgress(count: count, total: total, index: index, messageID: messageID, generation: token)
                    }
                    guard generation == token else { return }
                    addRecord(TransferRecord(fileName: metadata.fileName, size: Int64(metadata.size), direction: .sent,
                                             peerName: connection.peerName, savedPath: nil))
                    updateMessage(queued[index].id, status: .delivered)
                    queueCompleted = index + 1
                    transferProgress = Double(queueCompleted) / Double(queueCount)
                    retryURLs = Array(urls.dropFirst(index + 1))
                } catch {
                    guard generation == token else { return }
                    transferStatus = Task.isCancelled ? "发送已取消" : "发送未完成"
                    canRetry = true
                    disconnect(failureReason: error.localizedDescription)
                    if !(error is CancellationError) {
                        errorMessage = error.localizedDescription
                        shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                    }
                    return
                }
            }
            transferStatus = "已发送 \(queueCompleted) 个文件"
            if queueCompleted > 0 {
                let last = urls.last?.lastPathComponent ?? "文件"
                noteFirstSuccessfulTransfer(title: "发送完成", body: "对方已接收 \(last)。")
            }
        }
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

    private func sendingProgress(count: UInt64, total: UInt64, index: Int, messageID: UUID, generation token: UUID) {
        guard generation == token else { return }
        if let position = messages.firstIndex(where: { $0.id == messageID }) {
            let value = total == 0 ? 0 : Double(count) / Double(total)
            if value - messages[position].progress >= 0.01 { messages[position].progress = value }
        }
        transferProgress = (Double(index) + (total == 0 ? 0 : Double(count) / Double(total))) / Double(max(1, queueCount))
    }

    func retrySending() {
        guard !retryURLs.isEmpty else { return }
        if isConnected { startSending(retryURLs) }
        else if let device = lastDevice { requestConnection(to: device, pendingFiles: retryURLs) }
        else { errorMessage = "请先重新连接对方，再重试未完成的文件。" }
    }

    func cancelTransfer() {
        if isSending { transferStatus = "发送已取消"; canRetry = !retryURLs.isEmpty }
        if isReceivingFile { receiveStatus = "接收已取消，未完成的文件已清理" }
        disconnect()
    }

    func disconnect(failureReason: String? = nil) {
        generation = UUID()
        connectionTask?.cancel(); connectionTask = nil
        sendTask?.cancel(); sendTask = nil
        readerTask?.cancel(); readerTask = nil
        textTask?.cancel(); textTask = nil
        isSendingText = false
        for index in messages.indices where messages[index].isPending {
            messages[index].status = .failed
            if let failureReason { messages[index].failureReason = failureReason }
        }
        saveMessages()
        for (id, continuation) in fileDecisions {
            markFileDeclined(id, peerID: connectedPeerID, reason: "连接已断开，未开始接收。")
            continuation.resume(returning: false)
        }
        fileDecisions.removeAll()
        channel?.close(); channel = nil
        transport?.close(); transport = nil
        approveConnection(false)
        isConnected = false
        isConnecting = false
        isSending = false
        isReceivingFile = false
        connectedPeerName = ""
        connectedPeerID = ""
        connectionState = "未连接"
    }

    func shutdown() {
        started = false
        identityPreparationID = UUID()
        identityPreparationTask?.cancel()
        identityPreparationTask = nil
        isPreparingIdentity = false
        isIdentityReady = false
        preparedIdentity = nil
        disconnect()
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

    var chatPeers: [ChatPeer] {
        var peers: [String: ChatPeer] = [:]
        for message in messages {
            peers[message.peerID] = ChatPeer(id: message.peerID, name: message.peerName, preview: message.preview, date: message.date)
        }
        for (id, peer) in peers {
            if let device = nearbyDevices.first(where: { $0.serviceName == peer.serviceName }) {
                peers[id] = ChatPeer(id: id, name: device.displayName, preview: peer.preview, date: peer.date)
            }
        }
        if isConnected {
            let prior = peers[connectedPeerID]
            peers[connectedPeerID] = ChatPeer(id: connectedPeerID, name: connectedPeerName,
                                             preview: prior?.preview ?? "已连接，可以开始聊天", date: prior?.date ?? Date())
        }
        return peers.values.filter { !hiddenConversationIDs.contains($0.id) }.sorted { $0.date > $1.date }
    }

    func canDeleteConversation(_ id: String) -> Bool {
        let shortFingerprint = String(id.replacingOccurrences(of: " ", with: "").prefix(20))
        let isConnectionTarget = lastDevice?.serviceName == "Feige-\(shortFingerprint)"
        guard !((isConnected && id == connectedPeerID) || (isConnecting && isConnectionTarget)) else { return false }
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
    }

    var hiddenConversations: [ChatPeer] {
        var peers: [String: ChatPeer] = [:]
        for message in messages where hiddenConversationIDs.contains(message.peerID) {
            peers[message.peerID] = ChatPeer(id: message.peerID, name: message.peerName, preview: message.preview, date: message.date)
        }
        for (id, peer) in peers {
            if let device = nearbyDevices.first(where: { $0.serviceName == peer.serviceName }) {
                peers[id] = ChatPeer(id: id, name: device.displayName, preview: peer.preview, date: peer.date)
            }
        }
        return peers.values.sorted { $0.date > $1.date }
    }

    @discardableResult
    func sendText(_ text: String) -> Bool {
        guard isConnected, !isSendingText else { return false }
        do { _ = try ChatText(text: text) }
        catch { errorMessage = error.localizedDescription; return false }
        let message = ChatMessage(peerID: connectedPeerID, peerName: connectedPeerName, direction: .sent,
                                  kind: .text, text: text, status: .sending)
        messages.append(message)
        saveMessages()
        transmitText(message)
        return true
    }

    private func transmitText(_ message: ChatMessage) {
        guard let connection = channel, isConnected, message.peerID == connectedPeerID, !isSendingText else { return }
        let token = generation
        isSendingText = true
        updateMessage(message.id, status: .sending)
        textTask = Task { [self] in
            defer { if generation == token { isSendingText = false; textTask = nil } }
            do {
                _ = try await connection.sendText(message.text ?? "", id: message.id)
                guard generation == token else { return }
                updateMessage(message.id, status: .delivered)
                noteFirstSuccessfulTransfer(title: "消息已送达", body: "对方已收到你的消息。")
            } catch {
                guard generation == token else { return }
                updateMessage(message.id, status: .failed, failureReason: error.localizedDescription)
                disconnect(failureReason: error.localizedDescription)
                if !(error is CancellationError) {
                    errorMessage = error.localizedDescription
                    shouldSuggestNetworkHelp = isNetworkTroubleshootingFailure(error)
                }
            }
        }
    }

    private func receiveText(_ message: ChatText, peer: String, peerID: String, generation token: UUID) throws {
        guard generation == token else { throw ConnectionError.closed }
        // A lost acknowledgement may cause an explicit retry. Do not duplicate the received bubble.
        guard !messages.contains(where: { $0.id == message.id && $0.peerID == peerID && $0.direction == .received && $0.kind == .text }) else { return }
        let value = ChatMessage(id: message.id, peerID: peerID, peerName: peer, direction: .received,
                                kind: .text, text: message.text, status: .received)
        let updated = retainedMessages(messages + [value])
        var hidden = hiddenConversationIDs
        hidden.remove(peerID)
        try persistConversationState(messages: updated, hidden: hidden)
    }

    func retryMessage(_ message: ChatMessage) {
        guard isConnected, connectedPeerID == message.peerID else { errorMessage = "请先连接这台设备，再重试消息。"; return }
        if message.kind == .text { transmitText(message) }
        else if let path = message.localPath { startSending([URL(fileURLWithPath: path)]) }
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
            try persist(ConversationArchive(messages: updated, hiddenFingerprints: hiddenConversationIDs), to: messagesURL)
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
                messages = archive.messages.map { value in
                    var message = value
                    if message.isPending { message.status = .failed }
                    return message
                }.sorted { $0.date < $1.date }
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
        try persist(ConversationArchive(messages: retainedMessages(messages), hiddenFingerprints: hidden), to: messagesURL)
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
}

enum ConversationManagementError: LocalizedError {
    case activeConversation, archiveUnavailable
    var errorDescription: String? {
        switch self {
        case .activeConversation: "请先断开此设备并结束未完成的传输，再删除会话记录。"
        case .archiveUnavailable: "现有会话记录无法读取，因此没有覆盖原文件。请先备份并修复记录文件。"
        }
    }
}
