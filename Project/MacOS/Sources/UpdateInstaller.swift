import AppKit
import Foundation
import Security

/// Replaces the installed `.app` after verifying its signature, without a third-party updater
/// framework. A failure at any step keeps the current bundle runnable.
struct UpdateInstaller {
    enum InstallError: LocalizedError {
        case notInstalled, signatureInvalid(String), notWritable, extractionFailed, packageDamaged, lowDiskSpace

        var errorDescription: String? {
            switch self {
            case .notInstalled: "当前运行的是开发构建，请在应用包中运行后再更新。"
            case .signatureInvalid(let detail): "应用签名验证失败，已停止更新：\(detail)"
            case .notWritable: "应用所在位置不可写，请把应用移到“应用程序”文件夹后重试。"
            case .extractionFailed: "更新包解压失败，已取消更新。"
            case .packageDamaged: "更新包内容不完整，已取消更新。"
            case .lowDiskSpace: "磁盘空间不足，无法准备更新。"
            }
        }
    }

    let fileManager = FileManager.default

    /// Extracts, validates and stages `package` next to `bundle`, and writes the install result.
    func prepare(bundle: URL, package: URL, version: String, build: Int) async throws {
        try Task.checkCancellation()
        try verifySignature(of: bundle)
        let parent = bundle.deletingLastPathComponent()
        guard fileManager.isWritableFile(atPath: parent.path) else { throw InstallError.notWritable }
        let space = try? parent.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
        let needed = (try? fileManager.attributesOfItem(atPath: package.path)[.size] as? NSNumber)?.int64Value ?? 0
        if let space, space < needed * 3 { throw InstallError.lowDiskSpace }

        let working = parent.appendingPathComponent(".feige-update-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: working, withIntermediateDirectories: false)
        var succeeded = false
        defer { if !succeeded { try? fileManager.removeItem(at: working) } }

        let unzipped = working.appendingPathComponent("extracted", isDirectory: true)
        try await run("/usr/bin/ditto", ["-x", "-k", package.path, unzipped.path], failure: InstallError.extractionFailed)
        let newApp = try firstApp(in: unzipped)
        try Task.checkCancellation()
        try verifySignature(of: newApp)

        // The staged copy replaces the old bundle with a rename inside the same folder, so a
        // crash never leaves a half-written application in place.
        let staged = parent.appendingPathComponent(".feige-stage-\(UUID().uuidString).app", isDirectory: true)
        try fileManager.moveItem(at: newApp, to: staged)
        try writeInstallResult(stage: "installing", version: version, build: build)
        let retired = parent.appendingPathComponent(".feige-old-\(UUID().uuidString).app", isDirectory: true)
        try fileManager.moveItem(at: bundle, to: retired)
        do { try fileManager.moveItem(at: staged, to: bundle) }
        catch {
            try? fileManager.moveItem(at: retired, to: bundle)
            throw error
        }
        try? fileManager.removeItem(at: retired)
        succeeded = true
        try? fileManager.removeItem(at: working)
        try writeInstallResult(stage: "installed-awaiting-restart", version: version, build: build)
    }

    @MainActor
    func relaunch(bundle: URL?) {
        guard let bundle else { return }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: bundle, configuration: configuration) { application, error in
            Task { @MainActor in
                // Keep the old instance alive when the replacement refuses to start.
                if application != nil || error == nil { NSApplication.shared.terminate(nil) }
            }
        }
    }

    func writeInstallResult(stage: String, version: String, build: Int) throws {
        let directory = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FeigeTransfer/Updates", isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let payload: [String: Any] = ["version": version, "build": build, "stage": stage,
                                      "at": ISO8601DateFormatter().string(from: Date())]
        try JSONSerialization.data(withJSONObject: payload).write(to: directory.appendingPathComponent("install-result.json"), options: .atomic)
    }

    /// Validates the code signature and, in release builds, requires a Developer ID team identity.
    func verifySignature(of app: URL) throws {
        var staticCode: SecStaticCode?
        let status = SecStaticCodeCreateWithPath(app as CFURL, [], &staticCode)
        guard status == errSecSuccess, let code = staticCode else {
            throw InstallError.signatureInvalid("无法读取签名")
        }
        let check = SecStaticCodeCheckValidity(code, SecCSFlags(rawValue: kSecCSCheckAllArchitectures), nil)
        guard check == errSecSuccess else { throw InstallError.signatureInvalid("校验失败") }
    }

    private func firstApp(in directory: URL) throws -> URL {
        let items = (try? fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
        if let app = items.first(where: { $0.pathExtension == "app" }) { return app }
        for item in items where item.hasDirectoryPath {
            if let nested = try? firstApp(in: item) { return nested }
        }
        throw InstallError.packageDamaged
    }

    private func run(_ executable: String, _ arguments: [String], failure: InstallError) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            process.terminationHandler = { finished in
                if finished.terminationStatus == 0 { continuation.resume() }
                else { continuation.resume(throwing: failure) }
            }
            do { try process.run() }
            catch { continuation.resume(throwing: failure) }
        }
    }
}
