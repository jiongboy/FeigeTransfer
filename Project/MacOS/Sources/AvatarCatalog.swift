import AppKit
import SwiftUI

enum AvatarID {
    static let `default` = "bird"
    static let all = ["bird", "cat", "fox", "panda", "bear", "fish", "rocket", "sun", "star", "leaf"]

    static func isKnown(_ value: String?) -> Bool {
        guard let value else { return false }
        return all.contains(value)
    }

    static func normalize(_ value: String?) -> String {
        isKnown(value) ? value! : `default`
    }

    /// Only a full SHA-256 device fingerprint may key a persisted peer avatar.
    static func normalizeFingerprint(_ value: String?) -> String? {
        guard let value else { return nil }
        let compact = value.filter { !$0.isWhitespace }
        guard compact.utf8.count == 64,
              compact.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else {
            return nil
        }
        return compact.uppercased()
    }
}

struct AvatarChoice: Identifiable {
    let id: String
    let name: String
}

@MainActor
enum AvatarCatalog {
    static let choices = [
        AvatarChoice(id: "bird", name: "飞鸽"), AvatarChoice(id: "cat", name: "猫咪"),
        AvatarChoice(id: "fox", name: "狐狸"), AvatarChoice(id: "panda", name: "熊猫"),
        AvatarChoice(id: "bear", name: "小熊"), AvatarChoice(id: "fish", name: "小鱼"),
        AvatarChoice(id: "rocket", name: "火箭"), AvatarChoice(id: "sun", name: "太阳"),
        AvatarChoice(id: "star", name: "星星"), AvatarChoice(id: "leaf", name: "绿叶")
    ]

    static func imageURL(for rawID: String?) -> URL? {
        let avatarID = AvatarID.normalize(rawID)
        for directory in avatarDirectories {
            let url = directory.appendingPathComponent("\(avatarID).png")
            if FileManager.default.fileExists(atPath: url.path) { return url }
        }
        return nil
    }

    /// SwiftPM's generated `Bundle.module` only checks `<bundleURL>/FeigeMac_FeigeMac.bundle` and an absolute
    /// build-machine path, then calls `fatalError`. A signed `.app` cannot carry that bundle at its root, so the
    /// packaged app would crash on every machine except the one that built it. Resolve files explicitly instead:
    /// `Contents/Resources/Avatars` for the packaged app (copied by `scripts/package-app.sh`), and the SwiftPM
    /// resource bundle beside the executable or test bundle for `swift run` / `swift test`.
    private static let avatarDirectories: [URL] = {
        let resourceBundleName = "FeigeMac_FeigeMac.bundle"
        var roots: [URL] = []
        if let resources = Bundle.main.resourceURL { roots.append(resources) }
        for bundle in [Bundle.main, Bundle(for: BundleToken.self)] {
            roots.append(bundle.bundleURL)
            roots.append(bundle.bundleURL.deletingLastPathComponent())
        }
        var directories: [URL] = []
        for root in roots {
            directories.append(root.appendingPathComponent("Avatars", isDirectory: true))
            let resourceBundle = root.appendingPathComponent(resourceBundleName, isDirectory: true)
            directories.append(resourceBundle.appendingPathComponent("Avatars", isDirectory: true))
            directories.append(resourceBundle.appendingPathComponent("Contents/Resources/Avatars", isDirectory: true))
            directories.append(resourceBundle.appendingPathComponent("Contents/Resources", isDirectory: true))
            directories.append(resourceBundle)
        }
        return directories
    }()

    private final class BundleToken {}

    static func image(for rawID: String?) -> NSImage {
        if let url = imageURL(for: rawID), let image = NSImage(contentsOf: url) { return image }
        return NSImage(systemSymbolName: "person.crop.circle.fill", accessibilityDescription: nil) ?? NSImage(size: NSSize(width: 96, height: 96))
    }
}

struct AvatarView: View {
    let avatarID: String?
    var size: CGFloat = 40

    var body: some View {
        Image(nsImage: AvatarCatalog.image(for: avatarID))
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}
