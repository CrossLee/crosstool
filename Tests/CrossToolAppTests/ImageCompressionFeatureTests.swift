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
    @Test("Manual image imports ignore duplicates and non-images")
    func manualImageImportFiltering() throws {
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

    @MainActor
    @Test("Manual additions stay pending and never reveal Finder automatically")
    func manualAdditionDoesNotAutoCompress() async throws {
        let fixture = try makeStubImage(named: "manual.png")
        defer { try? FileManager.default.removeItem(at: fixture.deletingLastPathComponent()) }
        let compressor = RecordingImageCompressor()
        var revealBatches: [[URL]] = []
        let model = try makeModel(service: compressor) { revealBatches.append($0) }

        model.addImages([fixture])
        await Task.yield()

        #expect(compressor.calledURLs.isEmpty)
        #expect(revealBatches.isEmpty)
        #expect(model.items.count == 1)
        #expect(model.items.first?.state == .pending)
        #expect(!model.isCompressing)
    }

    @MainActor
    @Test("Finder Open With compresses only its batch and reveals the result once")
    func externalOpenAutoCompressesAndReveals() async throws {
        let directory = try makeStubDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let manual = try makeStubImage(named: "manual.png", in: directory)
        let external = try makeStubImage(named: "external.png", in: directory)
        let compressor = RecordingImageCompressor()
        var revealBatches: [[URL]] = []
        let model = try makeModel(service: compressor) { revealBatches.append($0) }

        model.addImages([manual])
        model.addImagesFromExternalOpen([external, external])
        await model.waitForCompressionToFinish()

        #expect(compressor.calledURLs == [external])
        #expect(model.items.count == 2)
        #expect(model.items.first(where: { $0.sourceURL == manual })?.state == .pending)
        let result = try completedResult(for: external, in: model)
        #expect(revealBatches == [[result.outputURL]])
    }

    @MainActor
    @Test("Finder requests received while compression is busy are queued")
    func externalOpenWhileBusyIsQueued() async throws {
        let directory = try makeStubDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let first = try makeStubImage(named: "first.png", in: directory)
        let second = try makeStubImage(named: "second.png", in: directory)
        let compressor = BlockingImageCompressor()
        var revealBatches: [[URL]] = []
        let model = try makeModel(service: compressor) { revealBatches.append($0) }

        model.addImagesFromExternalOpen([first])
        for _ in 0..<10_000 where compressor.calledURLs.isEmpty {
            await Task.yield()
        }
        #expect(compressor.calledURLs == [first])

        model.addImagesFromExternalOpen([second])
        compressor.releaseFirstCall()
        await model.waitForCompressionToFinish()

        #expect(compressor.calledURLs == [first, second])
        #expect(revealBatches.count == 2)
        #expect(revealBatches[0] == [try completedResult(for: first, in: model).outputURL])
        #expect(revealBatches[1] == [try completedResult(for: second, in: model).outputURL])
    }

    @MainActor
    @Test("Reopening a failed item while the batch is busy queues a retry")
    func failedItemCanBeRetriedBeforeItsBatchFinishes() async throws {
        let directory = try makeStubDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let failedThenRetried = try makeStubImage(named: "retry.png", in: directory)
        let blocking = try makeStubImage(named: "blocking.png", in: directory)
        let compressor = RetryAfterFailureCompressor(
            retryURL: failedThenRetried,
            blockingURL: blocking
        )
        var revealBatches: [[URL]] = []
        let model = try makeModel(service: compressor) { revealBatches.append($0) }

        model.addImagesFromExternalOpen([failedThenRetried, blocking])
        for _ in 0..<10_000 where compressor.calledURLs.count < 2 {
            await Task.yield()
        }
        #expect(compressor.calledURLs == [failedThenRetried, blocking])

        model.addImagesFromExternalOpen([failedThenRetried])
        compressor.releaseBlockingCall()
        await model.waitForCompressionToFinish()

        #expect(compressor.calledURLs == [failedThenRetried, blocking, failedThenRetried])
        #expect(revealBatches.count == 2)
        if revealBatches.count == 2 {
            #expect(revealBatches[0] == [try completedResult(for: blocking, in: model).outputURL])
            #expect(revealBatches[1] == [try completedResult(for: failedThenRetried, in: model).outputURL])
        }
    }

    @MainActor
    @Test("Opening the same path again recompresses the current source")
    func repeatedExternalOpenRecompressesCurrentSource() async throws {
        let fixture = try makeStubImage(named: "repeat.png")
        defer { try? FileManager.default.removeItem(at: fixture.deletingLastPathComponent()) }
        let compressor = RecordingImageCompressor()
        var revealBatches: [[URL]] = []
        let model = try makeModel(service: compressor) { revealBatches.append($0) }

        model.addImagesFromExternalOpen([fixture])
        await model.waitForCompressionToFinish()
        let firstResult = try completedResult(for: fixture, in: model)

        model.addImagesFromExternalOpen([fixture])
        await model.waitForCompressionToFinish()
        let secondResult = try completedResult(for: fixture, in: model)

        #expect(compressor.calledURLs == [fixture, fixture])
        #expect(secondResult.outputURL != firstResult.outputURL)
        #expect(revealBatches == [[firstResult.outputURL], [secondResult.outputURL]])
        #expect(model.items.count == 1)
    }

    @MainActor
    @Test("Failed or already optimized external images do not open Finder")
    func nonOutputExternalOutcomesDoNotReveal() async throws {
        let directory = try makeStubDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let failed = try makeStubImage(named: "failed.png", in: directory)
        let optimized = try makeStubImage(named: "optimized.png", in: directory)
        let compressor = RecordingImageCompressor(behaviors: [
            failed.lastPathComponent: .failure,
            optimized.lastPathComponent: .alreadyOptimized,
        ])
        var revealBatches: [[URL]] = []
        let model = try makeModel(service: compressor) { revealBatches.append($0) }

        model.addImagesFromExternalOpen([failed, optimized])
        await model.waitForCompressionToFinish()

        #expect(revealBatches.isEmpty)
        guard case .failed = model.items.first(where: { $0.sourceURL == failed })?.state else {
            Issue.record("The failed external image should remain visible as failed")
            return
        }
        guard case .alreadyOptimized = model.items.first(where: { $0.sourceURL == optimized })?.state else {
            Issue.record("The already optimized image should not produce a Finder result")
            return
        }
    }

    @Test("The feature is available from the main sidebar")
    func sidebarDestinationContract() {
        #expect(SidebarDestination.allCases.contains(.imageCompression))
        #expect(SidebarDestination.imageCompression.title == "图片压缩")
        #expect(SidebarDestination.imageCompression.systemImage == "photo.badge.arrow.down")
    }

    @MainActor
    private func makeModel(
        service: any ImageCompressing,
        revealFiles: @escaping ImageCompressionFeatureModel.FileRevealHandler
    ) throws -> ImageCompressionFeatureModel {
        let suiteName = "ImageCompressionFeatureTests.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
        return ImageCompressionFeatureModel(
            defaults: defaults,
            service: service,
            revealFiles: revealFiles
        )
    }

    private func makeStubDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-external-open-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func makeStubImage(named name: String, in directory: URL? = nil) throws -> URL {
        let directory = try directory ?? makeStubDirectory()
        let image = directory.appendingPathComponent(name, isDirectory: false)
        try Data("stub image \(name)".utf8).write(to: image)
        return image
    }

    @MainActor
    private func completedResult(
        for sourceURL: URL,
        in model: ImageCompressionFeatureModel
    ) throws -> ImageCompressionResult {
        let item = try #require(model.items.first(where: { $0.sourceURL == sourceURL }))
        guard case .completed(let result) = item.state else {
            Issue.record("Expected \(sourceURL.lastPathComponent) to be completed")
            throw ImageCompressionTestError.expectedCompletedResult
        }
        return result
    }
}

private enum ImageCompressionTestError: Error {
    case expectedCompletedResult
    case forcedFailure
}

private final class RecordingImageCompressor: ImageCompressing, @unchecked Sendable {
    enum Behavior: Sendable {
        case compressed
        case alreadyOptimized
        case failure
    }

    private let lock = NSLock()
    private let behaviors: [String: Behavior]
    private var calls: [URL] = []

    init(behaviors: [String: Behavior] = [:]) {
        self.behaviors = behaviors
    }

    var calledURLs: [URL] {
        lock.withLock { calls }
    }

    func compress(
        sourceURL: URL,
        settings: ImageCompressionSettings
    ) throws -> ImageCompressionOutcome {
        lock.withLock { calls.append(sourceURL) }
        switch behaviors[sourceURL.lastPathComponent] ?? .compressed {
        case .compressed:
            return try Self.makeCompressedOutcome(for: sourceURL)
        case .alreadyOptimized:
            return .alreadyOptimized(originalBytes: 1_024)
        case .failure:
            throw ImageCompressionTestError.forcedFailure
        }
    }

    fileprivate static func makeCompressedOutcome(
        for sourceURL: URL
    ) throws -> ImageCompressionOutcome {
        let outputURL = ImageCompressionService.uniqueOutputURL(for: sourceURL, format: .jpeg)
        try Data("compressed \(sourceURL.lastPathComponent)".utf8).write(to: outputURL)
        return .compressed(ImageCompressionResult(
            sourceURL: sourceURL,
            outputURL: outputURL,
            originalBytes: 1_024,
            compressedBytes: 256,
            pixelWidth: 100,
            pixelHeight: 100,
            outputFormat: .jpeg,
            metTargetSize: true
        ))
    }
}

private final class BlockingImageCompressor: ImageCompressing, @unchecked Sendable {
    private let condition = NSCondition()
    private var calls: [URL] = []
    private var firstCallReleased = false

    var calledURLs: [URL] {
        condition.withLock { calls }
    }

    func releaseFirstCall() {
        condition.withLock {
            firstCallReleased = true
            condition.broadcast()
        }
    }

    func compress(
        sourceURL: URL,
        settings: ImageCompressionSettings
    ) throws -> ImageCompressionOutcome {
        condition.lock()
        calls.append(sourceURL)
        let shouldWait = calls.count == 1
        while shouldWait, !firstCallReleased {
            condition.wait()
        }
        condition.unlock()
        return try RecordingImageCompressor.makeCompressedOutcome(for: sourceURL)
    }
}

private final class RetryAfterFailureCompressor: ImageCompressing, @unchecked Sendable {
    private let condition = NSCondition()
    private let retryURL: URL
    private let blockingURL: URL
    private var calls: [URL] = []
    private var blockingCallReleased = false
    private var retryAttemptCount = 0

    init(retryURL: URL, blockingURL: URL) {
        self.retryURL = retryURL
        self.blockingURL = blockingURL
    }

    var calledURLs: [URL] {
        condition.withLock { calls }
    }

    func releaseBlockingCall() {
        condition.withLock {
            blockingCallReleased = true
            condition.broadcast()
        }
    }

    func compress(
        sourceURL: URL,
        settings: ImageCompressionSettings
    ) throws -> ImageCompressionOutcome {
        condition.lock()
        calls.append(sourceURL)
        if sourceURL == retryURL {
            retryAttemptCount += 1
            let shouldFail = retryAttemptCount == 1
            condition.unlock()
            if shouldFail {
                throw ImageCompressionTestError.forcedFailure
            }
            return try RecordingImageCompressor.makeCompressedOutcome(for: sourceURL)
        }

        if sourceURL == blockingURL {
            while !blockingCallReleased {
                condition.wait()
            }
        }
        condition.unlock()
        return try RecordingImageCompressor.makeCompressedOutcome(for: sourceURL)
    }
}
