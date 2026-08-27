import Foundation
import ImageIO
@testable import CrossToolApp
import Testing
import UniformTypeIdentifiers

@Suite("Image compression")
struct ImageCompressionFeatureTests {
    @Test("Automatic output keeps alpha-capable images lossless")
    func automaticFormatResolution() {
        #expect(ImageCompressionService.resolvedFormat(
            requested: .automatic,
            hasAlpha: false
        ) == .jpeg)
        #expect(ImageCompressionService.resolvedFormat(
            requested: .automatic,
            hasAlpha: true
        ) == .png)
        #expect(ImageCompressionService.resolvedFormat(
            requested: .heic,
            hasAlpha: true
        ) == .heic)
    }

    @Test("Target-driven resizing stays within the visual-quality floor")
    func nextPixelLimitIsBounded() throws {
        let next = try #require(ImageCompressionService.nextPixelLimit(
            current: 1_920,
            minimum: 960,
            targetBytes: 250_000,
            currentBytes: 1_000_000
        ))

        #expect(next < 1_920)
        #expect(next >= 960)
        #expect(ImageCompressionService.nextPixelLimit(
            current: 960,
            minimum: 960,
            targetBytes: 250_000,
            currentBytes: 1_000_000
        ) == nil)
        #expect(ImageCompressionService.nextPixelLimit(
            current: 1_920,
            minimum: 960,
            targetBytes: 1_000_000,
            currentBytes: 250_000
        ) == nil)
    }

    @Test("Output naming never overwrites an existing result")
    func uniqueOutputNaming() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-compression-naming-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let source = directory.appendingPathComponent("lesson.png")
        let firstOutput = directory.appendingPathComponent("lesson-crosio.jpg")
        try Data([1]).write(to: source)
        try Data([2]).write(to: firstOutput)

        let output = ImageCompressionService.uniqueOutputURL(for: source, format: .jpeg)
        #expect(output.lastPathComponent == "lesson-crosio-2.jpg")
        #expect(output != source)
    }

    @Test("Concurrent result writes choose distinct names and preserve existing files")
    func concurrentOutputWriting() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-compression-concurrent-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let source = directory.appendingPathComponent("lesson.png")
        let existingOutput = directory.appendingPathComponent("lesson-crosio.jpg")
        let existingData = Data("keep me".utf8)
        try Data([0]).write(to: source)
        try existingData.write(to: existingOutput)

        let outputs = try await withThrowingTaskGroup(of: URL.self) { group in
            for marker in 1...8 {
                group.addTask {
                    try ImageCompressionService.writeOutputWithoutOverwriting(
                        Data([UInt8(marker)]),
                        for: source,
                        format: .jpeg
                    )
                }
            }

            var urls: [URL] = []
            for try await url in group {
                urls.append(url)
            }
            return urls
        }

        #expect(Set(outputs).count == 8)
        #expect(try Data(contentsOf: existingOutput) == existingData)
        let remainingNames = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(!remainingNames.contains { $0.hasPrefix(".crosio-compression-") })
    }

    @Test("Fallback recognizes destination-exists errors without deleting the destination")
    func destinationExistsErrorRecognition() {
        let cocoaError = NSError(
            domain: NSCocoaErrorDomain,
            code: CocoaError.fileWriteFileExists.rawValue
        )
        let posixError = NSError(domain: NSPOSIXErrorDomain, code: Int(EEXIST))
        let wrappedPOSIXError = NSError(
            domain: NSCocoaErrorDomain,
            code: CocoaError.fileWriteUnknown.rawValue,
            userInfo: [NSUnderlyingErrorKey: posixError]
        )
        let unrelatedError = NSError(
            domain: NSCocoaErrorDomain,
            code: CocoaError.fileWriteNoPermission.rawValue
        )

        #expect(ImageCompressionService.isDestinationExistsError(cocoaError))
        #expect(ImageCompressionService.isDestinationExistsError(posixError))
        #expect(ImageCompressionService.isDestinationExistsError(wrappedPOSIXError))
        #expect(!ImageCompressionService.isDestinationExistsError(unrelatedError))
    }

    @Test("Fallback writer preserves an existing destination and writes a free one")
    func fallbackWriterNeverOverwrites() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-compression-fallback-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let existingURL = directory.appendingPathComponent("existing.jpg")
        let freeURL = directory.appendingPathComponent("free.jpg")
        let existingData = Data("original result".utf8)
        let newData = Data("new result".utf8)
        try existingData.write(to: existingURL)

        #expect(try ImageCompressionService.fallbackWriteWithoutOverwriting(
            newData,
            to: existingURL
        ) == .destinationExists)
        #expect(try Data(contentsOf: existingURL) == existingData)

        #expect(try ImageCompressionService.fallbackWriteWithoutOverwriting(
            newData,
            to: freeURL
        ) == .written)
        #expect(try Data(contentsOf: freeURL) == newData)
    }

    @Test("Images already under the target are not recompressed")
    func alreadyUnderTargetIsPreserved() throws {
        let projectRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let fixture = projectRoot
            .appendingPathComponent("Resources/Brand/CrosioIcon.png", isDirectory: false)
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-compression-preserve-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let source = directory.appendingPathComponent("small-enough.png")
        try FileManager.default.copyItem(at: fixture, to: source)
        let originalData = try Data(contentsOf: source)
        let outcome = try ImageCompressionService().compress(
            sourceURL: source,
            settings: ImageCompressionSettings(
                outputFormat: .automatic,
                targetBytes: 5_000_000,
                dimensionLimit: .original
            )
        )

        #expect(outcome == .alreadyOptimized(originalBytes: Int64(originalData.count)))
        #expect(try Data(contentsOf: source) == originalData)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path) == ["small-enough.png"])
    }

    @Test("Lossy quality never drops below the visual quality floor")
    func qualityFloor() throws {
        #expect(try #require(ImageCompressionService.lossyQualityProbes.min()) >= 0.70)
        #expect(ImageCompressionItemState.alreadyOptimized("done").isRunnable == false)
    }

    @Test("Native JPEG compression reduces a real app image without modifying it")
    func nativeJPEGCompression() throws {
        guard ImageCompressionService.canWrite(.jpeg) else {
            Issue.record("This macOS runtime must provide the ImageIO JPEG encoder")
            return
        }

        let projectRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let fixture = projectRoot
            .appendingPathComponent("Resources", isDirectory: true)
            .appendingPathComponent("Brand", isDirectory: true)
            .appendingPathComponent("CrosioIcon.png", isDirectory: false)
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-compression-integration-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let source = directory.appendingPathComponent("CrosioIcon.png")
        try FileManager.default.copyItem(at: fixture, to: source)
        let originalData = try Data(contentsOf: source)

        let outcome = try ImageCompressionService().compress(
            sourceURL: source,
            settings: ImageCompressionSettings(
                outputFormat: .jpeg,
                targetBytes: 200_000,
                dimensionLimit: .pixels1242
            )
        )
        let result: ImageCompressionResult
        switch outcome {
        case .compressed(let compressed):
            result = compressed
        case .alreadyOptimized:
            Issue.record("The 789 KB PNG fixture should compress to a smaller JPEG")
            return
        }

        #expect(result.outputURL.lastPathComponent == "CrosioIcon-crosio.jpg")
        #expect(result.compressedBytes < result.originalBytes)
        #expect(result.compressedBytes <= 200_000)
        #expect(result.metTargetSize)
        #expect(result.pixelWidth <= 1_242)
        #expect(result.pixelHeight <= 1_242)
        #expect(try Data(contentsOf: source) == originalData)

        let outputData = try Data(contentsOf: result.outputURL)
        let imageSource = try #require(CGImageSourceCreateWithData(outputData as CFData, nil))
        #expect(CGImageSourceGetType(imageSource) as String? == UTType.jpeg.identifier)
        #expect(CGImageSourceGetCount(imageSource) == 1)
    }

    @Test("Invalid files fail without leaving an output")
    func invalidInputDoesNotWriteOutput() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-compression-invalid-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let source = directory.appendingPathComponent("broken.jpg")
        try Data("not an image".utf8).write(to: source)

        #expect(throws: ImageCompressionError.unsupportedImage) {
            try ImageCompressionService().compress(
                sourceURL: source,
                settings: ImageCompressionSettings(
                    outputFormat: .jpeg,
                    targetBytes: 200_000,
                    dimensionLimit: .original
                )
            )
        }
        let remainingFiles = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(remainingFiles == ["broken.jpg"])
    }

    @MainActor
    @Test("Changing compression settings makes optimized items runnable again")
    func settingsChangeResetsQueue() async throws {
        let suiteName = "ImageCompressionFeatureTests.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let model = ImageCompressionFeatureModel(defaults: defaults)
        let projectRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let fixture = projectRoot
            .appendingPathComponent("Resources/Brand/CrosioIcon.png", isDirectory: false)

        model.targetSize = .megabytes5
        model.addImages([fixture])
        model.compressAll()
        for _ in 0..<10_000 where model.isCompressing {
            await Task.yield()
        }

        #expect(!model.isCompressing)
        #expect(model.runnableItemCount == 0)
        guard let firstState = model.items.first?.state else {
            Issue.record("The imported image should remain in the queue")
            return
        }
        guard case .alreadyOptimized = firstState else {
            Issue.record("The image should be recognized as already under the target")
            return
        }

        model.targetSize = .kilobytes200
        #expect(model.items.first?.state == .pending)
        #expect(model.runnableItemCount == 1)
    }

    @MainActor
    @Test("External image imports ignore duplicates and non-images")
    func externalImageImportFiltering() throws {
        let suiteName = "ImageCompressionFeatureTests.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let model = ImageCompressionFeatureModel(defaults: defaults)
        let projectRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let fixture = projectRoot
            .appendingPathComponent("Resources/Brand/CrosioIcon.png", isDirectory: false)
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-open-with-import-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let image = directory.appendingPathComponent("finder-image.png")
        let text = directory.appendingPathComponent("notes.txt")
        try FileManager.default.copyItem(at: fixture, to: image)
        try Data("not an image".utf8).write(to: text)

        model.addImages([image, image, text])

        #expect(model.items.count == 1)
        #expect(model.items.first?.sourceURL == image)
        #expect(model.items.first?.state == .pending)
        #expect(model.runnableItemCount == 1)
    }

    @Test("The feature is available from the main sidebar")
    func sidebarDestinationContract() {
        #expect(SidebarDestination.allCases.contains(.imageCompression))
        #expect(SidebarDestination.imageCompression.title == "图片压缩")
        #expect(SidebarDestination.imageCompression.systemImage == "photo.badge.arrow.down")
    }
}
