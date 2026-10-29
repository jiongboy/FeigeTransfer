import Foundation

/// Formats bytes the way Finder and the Windows client display file sizes, so both
/// platforms describe the same transfer with the same number.
enum SizeText {
    static func format(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: max(0, bytes), countStyle: .file)
    }

    static func format(_ bytes: UInt64) -> String {
        let clamped = bytes > UInt64(Int64.max) ? Int64.max : Int64(bytes)
        return format(clamped)
    }
}

/// The application's own version, build number and channel.
///
/// A plain Swift Package executable has no bundle until the packaging script builds one,
/// so the values fall back to the compiled defaults when `Info.plist` is absent.
struct AppVersionInfo: Equatable {
    static let fallbackVersion = "0.4.0"
    static let fallbackBuild = 7
    static let channel = "stable"

    let version: String
    let build: Int

    static var current: AppVersionInfo {
        let bundle = Bundle.main
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        let build = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        return AppVersionInfo(version: version.flatMap { $0.isEmpty ? nil : $0 } ?? fallbackVersion,
                              build: build.flatMap { Int($0) } ?? fallbackBuild)
    }

    var text: String {
        "版本 \(version) · 构建 \(build) · \(Self.channel == "stable" ? "正式版" : "测试版") · macOS"
    }

    /// Numeric comparison; never compare version strings lexicographically.
    static func compare(_ lhs: String, _ rhs: String) -> Int {
        let left = lhs.split(separator: ".").map { Int($0) ?? 0 }
        let right = rhs.split(separator: ".").map { Int($0) ?? 0 }
        for index in 0..<max(left.count, right.count) {
            let a = index < left.count ? left[index] : 0
            let b = index < right.count ? right[index] : 0
            if a != b { return a < b ? -1 : 1 }
        }
        return 0
    }
}
