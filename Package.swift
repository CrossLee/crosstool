// swift-tools-version: 6.1

import PackageDescription

let package = Package(
    name: "crosstool",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(
            name: "CrossToolCore",
            targets: ["CrossToolCore"]
        ),
        .executable(
            name: "CrossToolApp",
            targets: ["CrossToolApp"]
        )
    ],
    targets: [
        .target(
            name: "CrossToolCore"
        ),
        .executableTarget(
            name: "CrossToolApp",
            dependencies: ["CrossToolCore"],
            resources: [
                .copy("Resources/Web")
            ]
        ),
        .testTarget(
            name: "CrossToolCoreTests",
            dependencies: ["CrossToolCore"]
        ),
        .testTarget(
            name: "CrossToolAppTests",
            dependencies: ["CrossToolApp"]
        )
    ],
    swiftLanguageModes: [.v5]
)
