// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "FeigeMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "FeigeIdentity", targets: ["FeigeIdentity"]),
        .executable(name: "FeigeMac", targets: ["FeigeMac"])
    ],
    targets: [
        .target(name: "FeigeIdentity", path: "Identity/Sources", linkerSettings: [.linkedFramework("Security")]),
        .executableTarget(
            name: "FeigeMac",
            dependencies: ["FeigeIdentity"],
            path: "Sources",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("Network"),
                .linkedFramework("Security"),
                .linkedFramework("PDFKit"),
                .linkedFramework("UserNotifications"),
                .linkedLibrary("z")
            ]
        ),
        .testTarget(name: "FeigeIdentityTests", dependencies: ["FeigeIdentity"], path: "Identity/Tests"),
        .testTarget(name: "FeigeMacTests", dependencies: ["FeigeMac"], path: "Tests")
    ]
)
