import AppKit
import Foundation

/// Application-owned update state for macOS.
///
/// Flow follows the shared update policy: silent background check → user-confirmed download →
/// size and SHA-256 verification → user-confirmed install → wait for idle transfers → atomic
/// bundle replacement → relaunch. Automatic checks never download, install or open a dialog.
@MainActor
final class UpdateController: ObservableObject {
    enum Phase: Equatable {
        case idle            // no candidate
        case available       // manifest offers a newer build
        case downloading
        case verified        // complete package on disk, not yet approved for install
        case waitingToInstall
        case readyToRelaunch // replacement staged, relaunch approved
    }

    @Published private(set) var status = "更新来源尚未配置"
    @Published private(set) var isConfigured = false
    @Published private(set) var isChecking = false
    @Published private(set) var isDownloading = false
    @Published private(set) var isWaitingToInstall = false
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var candidate: UpdateRelease?
    @Published private(set) var downloaded: Int64 = 0
    @Published private(set) var bannerVisible = false
    @Published private(set) var installNotice: String?
    @Published private(set) var installNoticeIsSuccess = true
    @Published var versionText = ""

    private let source: UpdateSource?
    private let current: AppVersionInfo
    private let store: UpdateStateStore
    private let updatesDirectory: URL
    private let downloads: URL
    private let session: URLSession
    private let logDirectory: URL
    private var state: UpdateState
    private var checkTask: Task<Void, Never>?
    private var downloadTask: Task<Void, Never>?
    private var packageURL: URL?
    private var servedOrigin: UpdateSource.Origin?
    private var waiting: Task<Bool, Never>?
    private var automaticTimer: Task<Void, Never>?

    var canCheck: Bool { isConfigured && !isDownloading && !isWaitingToInstall }
    var canDownload: Bool { isConfigured && candidate != nil && phase == .available && !isDownloading && !isWaitingToInstall }
    var canInstall: Bool { phase == .verified && !isWaitingToInstall }
    var isBusy: Bool { isDownloading || isWaitingToInstall }

    init(supportDirectory: URL, version: AppVersionInfo = .current, source: UpdateSource? = nil,
         session: URLSession = .shared) {
        self.current = version
        self.session = session
        let loaded = source ?? UpdateSource.load()
        self.source = loaded
        updatesDirectory = supportDirectory.appendingPathComponent("Updates", isDirectory: true)
        store = UpdateStateStore(directory: updatesDirectory)
        downloads = updatesDirectory.appendingPathComponent("Downloads", isDirectory: true)
        logDirectory = supportDirectory
        state = store.load()
        versionText = version.text
        // `self.source` is read explicitly: the parameter of the same name is still in scope here and
        // an `if let source` would bind to the (nil) parameter instead of the loaded configuration.
        if let configured = self.source {
            do {
                try configured.validate()
                isConfigured = true
                status = state.lastResult.isEmpty ? "尚未检查更新" : state.lastResult
                candidate = state.pendingRelease.flatMap { pending in
                    AppVersionInfo.compare(pending.version, version.version) > 0 || pending.build > version.build ? pending : nil
                }
                if self.candidate != nil { phase = .available }
                restoreVerifiedPackage()
            } catch {
                status = "更新配置无效，暂不联网检查：" + error.localizedDescription
                log("initialize-invalid-config")
            }
        } else {
            status = "更新来源尚未配置，暂不联网检查"
        }
        readInstallResult()
    }

    // MARK: lifecycle

    func start() {
        guard automaticTimer == nil else { return }
        automaticTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(60))
                self?.maybeCheckAutomatically()
            }
        }
    }

    func stop() {
        automaticTimer?.cancel(); automaticTimer = nil
        checkTask?.cancel(); downloadTask?.cancel()
        waiting?.cancel(); waiting = nil
    }

    /// Called whenever session or preference state changes; only automatic (silent) checks are rate limited.
    func preferencesChanged(autoCheckEnabled: Bool, setupEnded: Bool) {
        if !autoCheckEnabled { checkTask?.cancel() }
        if autoCheckEnabled && setupEnded { maybeCheckAutomatically() }
    }

    private func maybeCheckAutomatically() {
        guard canCheck, !isChecking, let source, source.isConfigured,
              state.isDue(now: Date()) else { return }
        check(manual: false)
    }

    // MARK: checking

    func check(manual: Bool = true) {
        guard canCheck else { return }
        if let checkTask { checkTask.cancel() }
        checkTask = Task { [weak self] in await self?.runCheck(manual: manual) }
    }

    private func runCheck(manual: Bool) async {
        guard let source, source.isConfigured, !isChecking else {
            status = "更新来源尚未配置，暂不联网检查"
            return
        }
        isChecking = true
        bannerVisible = false
        candidate = nil
        phase = .idle
        status = "正在检查更新…"
        state.lastAttemptAt = Date()
        persist()
        var lastFailure: Error?
        for origin in source.origins {
            do {
                let manifest = try await UpdateManifest.load(session: session, endpoint: source.manifestURL(for: origin),
                                                             keyId: source.keyId, publicKeyBase64: source.publicKey,
                                                             kind: source.algorithm)
                servedOrigin = origin
                let selection = UpdateManifest.selection(from: manifest, current: current)
                if let selection,
                   (!source.allows(selection.downloadUrl, origin: origin)
                    || !source.allows(selection.notesUrl, origin: origin)) {
                    throw UpdateManifestError.malformedRelease("下载或说明链接不属于当前可信更新来源")
                }
                state.highestSeenBuild = max(state.highestSeenBuild, manifest.releases.map(\.build).max() ?? 0)
                guard let selection else {
                    candidate = nil; phase = .idle; packageURL = nil
                    status = "已是最新版本"
                    state.lastResult = status
                    finishCheck()
                    return
                }
                candidate = selection
                phase = .available
                state.pendingRelease = selection
                status = "发现新版本 \(selection.version)"
                state.lastResult = status
                if !manual && state.shouldPrompt(build: selection.build, now: Date()) {
                    state.promptBuild = selection.build
                    state.promptAt = Date()
                    bannerVisible = true
                }
                finishCheck()
                return
            } catch {
                log("check failed: \(type(of: error))")
                lastFailure = error
                if error is UpdateManifestError { break } // a served-but-invalid manifest is fatal
            }
        }
        candidate = nil; phase = .idle
        status = describe(lastFailure) ?? "更新检查失败，请稍后重试"
        state.lastResult = status
        finishCheck()
    }

    private func finishCheck() {
        isChecking = false
        persist()
    }

    func later() { bannerVisible = false }

    func skip() {
        guard let candidate, !isDownloading, !isWaitingToInstall else { return }
        state.skippedBuild = candidate.build
        status = "已跳过 \(candidate.version)（构建 \(candidate.build)），手动检查仍可查看"
        bannerVisible = false
        persist()
    }

    // MARK: download

    func download() {
        guard canDownload, let release = candidate, let origin = servedOrigin, let source else { return }
        downloadTask?.cancel()
        downloadTask = Task { [weak self] in
            guard let self else { return }
            await self.runDownload(release, origin: origin, source: source)
        }
    }

    private func runDownload(_ release: UpdateRelease, origin: UpdateSource.Origin, source: UpdateSource) async {
        isDownloading = true
        phase = .downloading
        downloaded = 0
        bannerVisible = false
        status = "正在下载更新…"
        do {
            let directory = downloads
            let session = session
            let file = try await UpdateDownloader.download(release, origin: origin, source: source,
                                                           into: directory, session: session) { [weak self] progress in
                Task { @MainActor in
                    guard let self, self.isDownloading else { return }
                    switch progress {
                    case .downloading(let received, _): self.downloaded = received
                    case .verifying: self.status = "正在验证更新包大小、摘要和签名…"
                    }
                }
            }
            packageURL = file
            phase = .verified
            status = "更新包大小与摘要已验证，可以安装"
            state.lastResult = status
            persist()
        } catch is CancellationError {
            phase = .available
            status = "下载已取消，可重新下载"
        } catch {
            phase = .available
            status = describe(error) ?? "下载失败，可重试"
            log("download failed: \(type(of: error))")
        }
        isDownloading = false
    }

    func cancel() {
        downloadTask?.cancel(); downloadTask = nil
        waiting?.cancel(); waiting = nil
        if isDownloading { phase = .available }
    }

    /// Re-checks a package left from a previous run before calling it installable.
    private func restoreVerifiedPackage() {
        guard let release = candidate else { return }
        let file = downloads.appendingPathComponent("FeigeMac-\(release.version)-\(release.build).zip")
        guard FileManager.default.fileExists(atPath: file.path),
              (try? SHA256Text.hexDigest(of: file)) == release.sha256 else { return }
        packageURL = file
        if phase == .available { phase = .verified; status = "更新包已验证，可以安装" }
    }

    // MARK: install

    /// Waits until the caller reports that no transfer is active, then replaces the bundle.
    /// `busy` returning `true` means "work still in progress"; reconnection etc. keeps it waiting.
    func installWhenReady(window: NSWindow?, isBusy: @escaping @MainActor () -> Bool) async {
        await install(window: window, waitForIdle: isBusy)
    }

    func installNow(window: NSWindow?) async {
        await install(window: window, waitForIdle: { false })
    }

    private func install(window: NSWindow?, waitForIdle: @escaping @MainActor () -> Bool) async {
        guard canInstall, let release = candidate, let packageURL else { return }
        isWaitingToInstall = true
        phase = .waitingToInstall
        status = "正在准备安装…"
        defer { isWaitingToInstall = false; if phase == .waitingToInstall { phase = .verified } }
        guard let appURL = installedBundle() else {
            status = "无法确定当前应用位置，请从正式安装包手动更新。"
            return
        }
        let deadline = Date().addingTimeInterval(30 * 60)
        while waitForIdle() && Date() < deadline {
            status = "正在等待收发结束，可取消等待"
            do { try await Task.sleep(for: .milliseconds(500)) }
            catch { status = "已取消等待安装"; return }
        }
        guard Date() < deadline else { status = "等待收发超时，可稍后重试"; return }
        do {
            status = "正在验证签名并替换应用…"
            try await UpdateInstaller().prepare(bundle: appURL, package: packageURL,
                                                  version: release.version, build: release.build)
            phase = .readyToRelaunch
            status = "更新已准备好，重新启动后生效"
            state.lastResult = status
            persist()
            relaunch()
        } catch {
            phase = .verified
            status = "安装准备失败，当前版本已保留：" + (describe(error) ?? error.localizedDescription)
            log("install failed: \(type(of: error))")
        }
    }

    func relaunch() {
        UpdateInstaller().relaunch(bundle: installedBundle())
    }

    func dismissInstallNotice() { installNotice = nil }

    private func installedBundle() -> URL? {
        let bundle = Bundle.main.bundleURL
        guard bundle.pathExtension == "app" else { return nil }
        return bundle
    }

    private func readInstallResult() {
        let url = updatesDirectory.appendingPathComponent("install-result.json")
        guard let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let version = object["version"] as? String, let build = object["build"] as? Int,
              let stage = object["stage"] as? String else { return }
        if (stage == "installed-awaiting-restart" || stage == "installed-restart-failed"),
           version == current.version, build == current.build {
            status = "更新完成，当前运行版本已确认"
            installNotice = "已更新到 \(version)（构建 \(build)）。"
            installNoticeIsSuccess = true
            try? FileManager.default.removeItem(at: url)
        } else if stage != "cancelled" {
            status = "上次更新未完成版本确认，可重试或手动安装。"
            installNotice = "上次更新未能确认版本，可重试或手动安装。"
            installNoticeIsSuccess = false
        }
        state.lastResult = status
        persist()
    }

    // MARK: helpers

    private func persist() {
        do { try store.save(state) }
        catch { log("state save failed: \(type(of: error))") }
    }

    private func log(_ message: String) {
        AppLogFile.write("update: \(message)", in: logDirectory)
    }

    private func describe(_ error: Error?) -> String? {
        guard let error else { return nil }
        if let error = error as? LocalizedError, let description = error.errorDescription { return description }
        let urlError = error as? URLError
        switch urlError?.code {
        case .notConnectedToInternet, .networkConnectionLost, .cannotFindHost, .cannotConnectToHost, .timedOut:
            return "无法连接更新服务，请稍后重试；局域网收发不受影响"
        case .cancelled: return "更新检查已取消，可手动重试"
        default: return "检查或处理更新失败，请稍后重试"
        }
    }
}
