import AppKit
import CoreGraphics
import Darwin
import Foundation
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

enum ImageCompressionOutputFormat: String, CaseIterable, Identifiable, Sendable {
    case automatic
    case jpeg
    case heic
    case png

    var id: String { rawValue }

    var title: String {
        switch self {
        case .automatic: return "智能"
        case .jpeg: return "JPEG"
        case .heic: return "HEIC"
        case .png: return "PNG"
        }
    }

    var summary: String {
        switch self {
        case .automatic:
            return "无透明图片转为高质量 JPEG；透明图片保留 PNG"
        case .jpeg:
            return "兼容性最好，适合照片、截图和网页上传"
        case .heic:
            return "同等观感通常更小，适合 Apple 设备间使用"
        case .png:
            return "保留透明与无损像素，体积降幅通常有限"
        }
    }

    var isAvailable: Bool {
        switch self {
        case .automatic:
            return true
        case .jpeg:
            return ImageCompressionService.canWrite(.jpeg)
        case .heic:
            return ImageCompressionService.canWrite(.heic)
        case .png:
            return ImageCompressionService.canWrite(.png)
        }
    }
}

enum ImageCompressionTargetSize: Int, CaseIterable, Identifiable, Sendable {
    case kilobytes200 = 200_000
    case kilobytes500 = 500_000
    case megabyte1 = 1_000_000
    case megabytes2 = 2_000_000
    case megabytes5 = 5_000_000

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .kilobytes200: return "200 KB"
        case .kilobytes500: return "500 KB"
        case .megabyte1: return "1 MB"
        case .megabytes2: return "2 MB"
        case .megabytes5: return "5 MB"
        }
    }
}

enum ImageCompressionDimensionLimit: Int, CaseIterable, Identifiable, Sendable {
    case original = 0
    case pixels1242 = 1_242
    case pixels1920 = 1_920
    case pixels2560 = 2_560
    case pixels4096 = 4_096

    var id: Int { rawValue }

    var maximumPixels: Int? {
        self == .original ? nil : rawValue
    }

    var allowsTargetDrivenResize: Bool {
        self != .original
    }

    var title: String {
        switch self {
        case .original: return "保持原尺寸"
        case .pixels1242: return "最长边 1242 px"
        case .pixels1920: return "最长边 1920 px"
        case .pixels2560: return "最长边 2560 px"
        case .pixels4096: return "最长边 4096 px"
        }
    }
}

struct ImageCompressionSettings: Equatable, Sendable {
    var outputFormat: ImageCompressionOutputFormat
    var targetBytes: Int
    var dimensionLimit: ImageCompressionDimensionLimit
}

enum ImageCompressionResolvedFormat: String, Equatable, Sendable {
    case jpeg
    case heic
    case png

    var title: String {
        switch self {
        case .jpeg: return "JPEG"
        case .heic: return "HEIC"
        case .png: return "PNG"
        }
    }

    var fileExtension: String {
        switch self {
        case .jpeg: return "jpg"
        case .heic: return "heic"
        case .png: return "png"
        }
    }

    var typeIdentifier: String {
        switch self {
        case .jpeg: return UTType.jpeg.identifier
        case .heic: return UTType.heic.identifier
        case .png: return UTType.png.identifier
        }
    }

    var isLossy: Bool {
        self != .png
    }
}

struct ImageCompressionResult: Equatable, Sendable {
    let sourceURL: URL
    let outputURL: URL
    let originalBytes: Int64
    let compressedBytes: Int64
    let pixelWidth: Int
    let pixelHeight: Int
    let outputFormat: ImageCompressionResolvedFormat
    let metTargetSize: Bool

    var savedBytes: Int64 {
        max(0, originalBytes - compressedBytes)
    }

    var reductionPercentage: Int {
        guard originalBytes > 0 else { return 0 }
        let ratio = 1 - (Double(compressedBytes) / Double(originalBytes))
        return max(0, min(100, Int((ratio * 100).rounded())))
    }
}

enum ImageCompressionOutcome: Equatable, Sendable {
    case compressed(ImageCompressionResult)
    case alreadyOptimized(originalBytes: Int64)
}

enum ImageCompressionError: LocalizedError, Equatable {
    case unreadableFile
    case unsupportedImage
    case animatedImageUnsupported
    case invalidDimensions
    case imageTooLarge
    case decodingFailed
    case outputFormatUnavailable(String)
    case encodingFailed(String)
    case cannotWriteOutput

    var errorDescription: String? {
        switch self {
        case .unreadableFile:
            return "无法读取图片文件"
        case .unsupportedImage:
            return "不支持这种图片格式"
        case .animatedImageUnsupported:
            return "暂不压缩动图或多页图片，以免丢失帧"
        case .invalidDimensions:
            return "图片尺寸无效"
        case .imageTooLarge:
            return "图片像素过大，暂不处理超过 5000 万像素的图片"
        case .decodingFailed:
            return "图片解码失败，文件可能已损坏"
        case .outputFormatUnavailable(let format):
            return "当前 macOS 无法写入 \(format) 图片"
        case .encodingFailed(let format):
            return "\(format) 编码失败"
        case .cannotWriteOutput:
            return "无法在原图文件夹写入压缩结果"
        }
    }
}

struct ImageCompressionService: Sendable {
    static let maximumPixelDimension = 32_768
    static let maximumPixelCount: Int64 = 50_000_000
    static let minimumAutomaticPixelDimension = 960
    static let lossyQualityProbes: [Double] = [
        0.95, 0.90, 0.85, 0.80, 0.75, 0.70,
    ]
    static let minimumMeaningfulSavings = 16_384

    static func canWrite(_ format: ImageCompressionResolvedFormat) -> Bool {
        let identifiers = CGImageDestinationCopyTypeIdentifiers() as? [String] ?? []
        return identifiers.contains(format.typeIdentifier)
    }

    static func resolvedFormat(
        requested: ImageCompressionOutputFormat,
        hasAlpha: Bool
    ) -> ImageCompressionResolvedFormat {
        switch requested {
        case .automatic:
            return hasAlpha ? .png : .jpeg
        case .jpeg:
            return .jpeg
        case .heic:
            return .heic
        case .png:
            return .png
        }
    }

    static func nextPixelLimit(
        current: Int,
        minimum: Int,
        targetBytes: Int,
        currentBytes: Int
    ) -> Int? {
        guard current > minimum, targetBytes > 0, currentBytes > targetBytes else {
            return nil
        }

        let estimatedScale = sqrt(Double(targetBytes) / Double(currentBytes)) * 0.94
        let boundedScale = max(0.72, min(0.90, estimatedScale))
        let proposed = min(current - 1, Int((Double(current) * boundedScale).rounded(.down)))
        guard proposed >= minimum else {
            return current > minimum ? minimum : nil
        }
        return proposed
    }

    static func uniqueOutputURL(
        for sourceURL: URL,
        format: ImageCompressionResolvedFormat,
        fileManager: FileManager = .default
    ) -> URL {
        let directory = sourceURL.deletingLastPathComponent()
        let stem = sourceURL.deletingPathExtension().lastPathComponent
        var sequence = 1

        while true {
            let suffix = sequence == 1 ? "-crosio" : "-crosio-\(sequence)"
            let candidate = directory
                .appendingPathComponent(stem + suffix, isDirectory: false)
                .appendingPathExtension(format.fileExtension)
            if !fileManager.fileExists(atPath: candidate.path) {
                return candidate
            }
            sequence += 1
        }
    }

    static func writeOutputWithoutOverwriting(
        _ data: Data,
        for sourceURL: URL,
        format: ImageCompressionResolvedFormat,
        fileManager: FileManager = .default
    ) throws -> URL {
        let directory = sourceURL.deletingLastPathComponent()
        let stem = sourceURL.deletingPathExtension().lastPathComponent
        let temporaryURL = directory.appendingPathComponent(
            ".crosio-compression-\(UUID().uuidString).tmp",
            isDirectory: false
        )
        var sequence = 1

        do {
            try data.write(to: temporaryURL, options: .atomic)
        } catch {
            throw ImageCompressionError.cannotWriteOutput
        }
        defer { try? fileManager.removeItem(at: temporaryURL) }

        while sequence < 100_000 {
            let suffix = sequence == 1 ? "-crosio" : "-crosio-\(sequence)"
            let candidate = directory
                .appendingPathComponent(stem + suffix, isDirectory: false)
                .appendingPathExtension(format.fileExtension)
            switch try exclusiveMove(from: temporaryURL, to: candidate) {
            case .moved:
                return candidate
            case .destinationExists:
                sequence += 1
            case .unsupported:
                switch try fallbackWriteWithoutOverwriting(data, to: candidate) {
                case .written:
                    return candidate
                case .destinationExists:
                    sequence += 1
                }
            }
        }

        throw ImageCompressionError.cannotWriteOutput
    }

    enum FallbackWriteResult: Equatable {
        case written
        case destinationExists
    }

    static func fallbackWriteWithoutOverwriting(_ data: Data, to destinationURL: URL) throws
        -> FallbackWriteResult {
        do {
            try data.write(to: destinationURL, options: .withoutOverwriting)
            return .written
        } catch {
            if isDestinationExistsError(error) {
                return .destinationExists
            }
            throw ImageCompressionError.cannotWriteOutput
        }
    }

    static func isDestinationExistsError(_ error: Error) -> Bool {
        let cocoaError = error as NSError
        if cocoaError.domain == NSPOSIXErrorDomain,
           cocoaError.code == Int(EEXIST) {
            return true
        }
        if cocoaError.domain == NSCocoaErrorDomain,
           cocoaError.code == CocoaError.fileWriteFileExists.rawValue {
            return true
        }

        guard let underlyingError = cocoaError.userInfo[NSUnderlyingErrorKey] as? NSError else {
            return false
        }
        return underlyingError.domain == NSPOSIXErrorDomain && underlyingError.code == Int(EEXIST)
    }

    private enum ExclusiveMoveResult {
        case moved
        case destinationExists
        case unsupported
    }

    private static func exclusiveMove(from sourceURL: URL, to destinationURL: URL) throws
        -> ExclusiveMoveResult {
        var result: Int32?
        var failureCode: Int32?
        sourceURL.withUnsafeFileSystemRepresentation { sourcePath in
            destinationURL.withUnsafeFileSystemRepresentation { destinationPath in
                guard let sourcePath, let destinationPath else { return }
                result = renameatx_np(
                    AT_FDCWD,
                    sourcePath,
                    AT_FDCWD,
                    destinationPath,
                    UInt32(RENAME_EXCL)
                )
                if result != 0 {
                    failureCode = errno
                }
            }
        }

        guard let result else {
            throw ImageCompressionError.cannotWriteOutput
        }
        if result == 0 {
            return .moved
        }

        switch failureCode {
        case EEXIST:
            return .destinationExists
        case ENOTSUP, EINVAL:
            return .unsupported
        default:
            throw ImageCompressionError.cannotWriteOutput
        }
    }

    func compress(
        sourceURL: URL,
        settings: ImageCompressionSettings
    ) throws -> ImageCompressionOutcome {
        let isAccessing = sourceURL.startAccessingSecurityScopedResource()
        defer {
            if isAccessing {
                sourceURL.stopAccessingSecurityScopedResource()
            }
        }

        try Task.checkCancellation()

        guard let originalData = try? Data(contentsOf: sourceURL, options: .mappedIfSafe) else {
            throw ImageCompressionError.unreadableFile
        }
        guard let source = CGImageSourceCreateWithData(originalData as CFData, nil) else {
            throw ImageCompressionError.unsupportedImage
        }
        let frameCount = CGImageSourceGetCount(source)
        guard frameCount > 0 else {
            throw ImageCompressionError.unsupportedImage
        }
        guard frameCount == 1 else {
            throw ImageCompressionError.animatedImageUnsupported
        }

        let dimensions = try sourceDimensions(source)
        let sourceLongestEdge = max(dimensions.width, dimensions.height)
        let initialLimit = min(
            settings.dimensionLimit.maximumPixels ?? sourceLongestEdge,
            sourceLongestEdge
        )
        guard initialLimit > 0 else {
            throw ImageCompressionError.invalidDimensions
        }

        let requestedFormat = explicitlyRequestedFormat(settings.outputFormat)
        let sourceType = CGImageSourceGetType(source) as String?
        let requiresFormatChange = requestedFormat.map { $0.typeIdentifier != sourceType } ?? false
        let requiresDimensionChange = initialLimit < sourceLongestEdge
        if originalData.count <= settings.targetBytes,
           !requiresFormatChange,
           !requiresDimensionChange {
            return .alreadyOptimized(originalBytes: Int64(originalData.count))
        }

        let initialImage = try decodedImage(source: source, maximumPixelSize: initialLimit)
        let outputFormat = Self.resolvedFormat(
            requested: settings.outputFormat,
            hasAlpha: Self.hasAlphaChannel(initialImage)
        )
        guard Self.canWrite(outputFormat) else {
            throw ImageCompressionError.outputFormatUnavailable(outputFormat.title)
        }

        let encoded = if outputFormat.isLossy {
            try encodeLossy(
                source: source,
                initialImage: initialImage,
                initialLimit: initialLimit,
                format: outputFormat,
                targetBytes: settings.targetBytes,
                allowsResize: settings.dimensionLimit.allowsTargetDrivenResize
            )
        } else {
            try encodePNG(
                source: source,
                initialImage: initialImage,
                initialLimit: initialLimit,
                targetBytes: settings.targetBytes,
                allowsResize: settings.dimensionLimit.allowsTargetDrivenResize
            )
        }

        let metTargetSize = encoded.data.count <= settings.targetBytes
        let savedBytes = originalData.count - encoded.data.count
        let meaningfulSavingsThreshold = max(
            Self.minimumMeaningfulSavings,
            originalData.count / 20
        )
        let userRequestedTransformation = requiresFormatChange || requiresDimensionChange
        guard savedBytes > 0,
              metTargetSize || userRequestedTransformation || savedBytes >= meaningfulSavingsThreshold else {
            return .alreadyOptimized(originalBytes: Int64(originalData.count))
        }
        guard isValidEncodedImage(encoded.data, expectedFormat: outputFormat) else {
            throw ImageCompressionError.encodingFailed(outputFormat.title)
        }

        try Task.checkCancellation()
        let outputURL = try Self.writeOutputWithoutOverwriting(
            encoded.data,
            for: sourceURL,
            format: outputFormat
        )

        return .compressed(ImageCompressionResult(
            sourceURL: sourceURL,
            outputURL: outputURL,
            originalBytes: Int64(originalData.count),
            compressedBytes: Int64(encoded.data.count),
            pixelWidth: encoded.width,
            pixelHeight: encoded.height,
            outputFormat: outputFormat,
            metTargetSize: metTargetSize
        ))
    }

    private func explicitlyRequestedFormat(
        _ outputFormat: ImageCompressionOutputFormat
    ) -> ImageCompressionResolvedFormat? {
        switch outputFormat {
        case .automatic:
            return nil
        case .jpeg:
            return .jpeg
        case .heic:
            return .heic
        case .png:
            return .png
        }
    }

    private func sourceDimensions(_ source: CGImageSource) throws -> (width: Int, height: Int) {
        guard let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil)
            as? [CFString: Any],
              let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
              let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue,
              width > 0,
              height > 0 else {
            throw ImageCompressionError.invalidDimensions
        }

        guard width <= Self.maximumPixelDimension,
              height <= Self.maximumPixelDimension,
              Int64(width) * Int64(height) <= Self.maximumPixelCount else {
            throw ImageCompressionError.imageTooLarge
        }
        return (width, height)
    }

    private func decodedImage(
        source: CGImageSource,
        maximumPixelSize: Int
    ) throws -> CGImage {
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maximumPixelSize,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(
            source,
            0,
            options as CFDictionary
        ) else {
            throw ImageCompressionError.decodingFailed
        }
        return image
    }

    private func encodeLossy(
        source: CGImageSource,
        initialImage: CGImage,
        initialLimit: Int,
        format: ImageCompressionResolvedFormat,
        targetBytes: Int,
        allowsResize: Bool
    ) throws -> EncodedImage {
        var image = try flattenedOnWhiteIfNeeded(initialImage)
        var currentLimit = initialLimit
        var smallest: EncodedImage?
        let minimumLimit = min(Self.minimumAutomaticPixelDimension, initialLimit)
        let maximumResizePasses = allowsResize ? 3 : 0

        for resizePass in 0...maximumResizePasses {
            try Task.checkCancellation()
            let search = try searchLossyQuality(
                image: image,
                format: format,
                targetBytes: targetBytes
            )

            if let candidate = search.bestUnderTarget {
                return candidate
            }
            if smallest == nil || search.smallest.data.count < smallest!.data.count {
                smallest = search.smallest
            }

            guard resizePass < maximumResizePasses,
                  let nextLimit = Self.nextPixelLimit(
                    current: currentLimit,
                    minimum: minimumLimit,
                    targetBytes: targetBytes,
                    currentBytes: search.smallest.data.count
                  ) else {
                break
            }

            currentLimit = nextLimit
            image = try flattenedOnWhiteIfNeeded(
                decodedImage(source: source, maximumPixelSize: nextLimit)
            )
        }

        guard let smallest else {
            throw ImageCompressionError.encodingFailed(format.title)
        }
        return smallest
    }

    private func encodePNG(
        source: CGImageSource,
        initialImage: CGImage,
        initialLimit: Int,
        targetBytes: Int,
        allowsResize: Bool
    ) throws -> EncodedImage {
        var image = initialImage
        var currentLimit = initialLimit
        var smallest: EncodedImage?
        let minimumLimit = min(Self.minimumAutomaticPixelDimension, initialLimit)
        let maximumResizePasses = allowsResize ? 3 : 0

        for resizePass in 0...maximumResizePasses {
            try Task.checkCancellation()
            let data = try encode(image: image, format: .png, quality: nil)
            let candidate = EncodedImage(
                data: data,
                width: image.width,
                height: image.height,
                quality: nil
            )
            if candidate.data.count <= targetBytes {
                return candidate
            }
            if smallest == nil || candidate.data.count < smallest!.data.count {
                smallest = candidate
            }

            guard resizePass < maximumResizePasses,
                  let nextLimit = Self.nextPixelLimit(
                    current: currentLimit,
                    minimum: minimumLimit,
                    targetBytes: targetBytes,
                    currentBytes: candidate.data.count
                  ) else {
                break
            }

            currentLimit = nextLimit
            image = try decodedImage(source: source, maximumPixelSize: nextLimit)
        }

        guard let smallest else {
            throw ImageCompressionError.encodingFailed(ImageCompressionResolvedFormat.png.title)
        }
        return smallest
    }

    private func searchLossyQuality(
        image: CGImage,
        format: ImageCompressionResolvedFormat,
        targetBytes: Int
    ) throws -> (bestUnderTarget: EncodedImage?, smallest: EncodedImage) {
        var previousOverTargetQuality: Double?
        var smallest: EncodedImage?

        for quality in Self.lossyQualityProbes {
            try Task.checkCancellation()
            let data = try encode(image: image, format: format, quality: quality)
            let candidate = EncodedImage(
                data: data,
                width: image.width,
                height: image.height,
                quality: quality
            )
            if smallest == nil || candidate.data.count < smallest!.data.count {
                smallest = candidate
            }

            if data.count <= targetBytes {
                var best = candidate
                if let upperQuality = previousOverTargetQuality {
                    var lowerQuality = quality
                    var upperQuality = upperQuality
                    for _ in 0..<5 {
                        try Task.checkCancellation()
                        let middleQuality = (lowerQuality + upperQuality) / 2
                        let middleData = try encode(
                            image: image,
                            format: format,
                            quality: middleQuality
                        )
                        let middle = EncodedImage(
                            data: middleData,
                            width: image.width,
                            height: image.height,
                            quality: middleQuality
                        )
                        if smallest == nil || middle.data.count < smallest!.data.count {
                            smallest = middle
                        }
                        if middleData.count <= targetBytes {
                            best = middle
                            lowerQuality = middleQuality
                        } else {
                            upperQuality = middleQuality
                        }
                    }
                }
                return (best, smallest ?? best)
            }
            previousOverTargetQuality = quality
        }

        guard let smallest else {
            throw ImageCompressionError.encodingFailed(format.title)
        }
        return (nil, smallest)
    }

    private func encode(
        image: CGImage,
        format: ImageCompressionResolvedFormat,
        quality: Double?
    ) throws -> Data {
        let buffer = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            buffer,
            format.typeIdentifier as CFString,
            1,
            nil
        ) else {
            throw ImageCompressionError.encodingFailed(format.title)
        }

        var properties: [CFString: Any] = [
            kCGImageDestinationEmbedThumbnail: false,
            kCGImageDestinationOptimizeColorForSharing: true,
        ]
        if let quality {
            properties[kCGImageDestinationLossyCompressionQuality] = quality
        }

        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            throw ImageCompressionError.encodingFailed(format.title)
        }
        return buffer as Data
    }

    private func isValidEncodedImage(
        _ data: Data,
        expectedFormat: ImageCompressionResolvedFormat
    ) -> Bool {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) == 1,
              CGImageSourceGetType(source) as String? == expectedFormat.typeIdentifier,
              CGImageSourceCreateImageAtIndex(source, 0, nil) != nil else {
            return false
        }
        return true
    }

    private func flattenedOnWhiteIfNeeded(_ image: CGImage) throws -> CGImage {
        guard Self.hasAlphaChannel(image) else { return image }
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(
                data: nil,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: colorSpace,
                bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
              ) else {
            throw ImageCompressionError.decodingFailed
        }

        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let flattened = context.makeImage() else {
            throw ImageCompressionError.decodingFailed
        }
        return flattened
    }

    private static func hasAlphaChannel(_ image: CGImage) -> Bool {
        switch image.alphaInfo {
        case .first, .last, .premultipliedFirst, .premultipliedLast, .alphaOnly:
            return true
        case .none, .noneSkipFirst, .noneSkipLast:
            return false
        @unknown default:
            return true
        }
    }
}

private struct EncodedImage {
    let data: Data
    let width: Int
    let height: Int
    let quality: Double?
}

enum ImageCompressionItemState: Equatable, Sendable {
    case pending
    case compressing
    case completed(ImageCompressionResult)
    case alreadyOptimized(String)
    case failed(String)

    var isRunnable: Bool {
        switch self {
        case .pending, .failed:
            return true
        case .compressing, .completed, .alreadyOptimized:
            return false
        }
    }
}

struct ImageCompressionItem: Identifiable, Equatable, Sendable {
    let id: UUID
    let sourceURL: URL
    let originalBytes: Int64
    var state: ImageCompressionItemState
}

private enum ImageCompressionWorkerResult: Sendable {
    case success(ImageCompressionOutcome)
    case failure(String)
    case cancelled
}

@MainActor
final class ImageCompressionFeatureModel: ObservableObject {
    @Published var outputFormat: ImageCompressionOutputFormat {
        didSet {
            defaults.set(outputFormat.rawValue, forKey: Self.outputFormatKey)
            if outputFormat != oldValue {
                resetQueueAfterSettingsChange()
            }
        }
    }
    @Published var targetSize: ImageCompressionTargetSize {
        didSet {
            defaults.set(targetSize.rawValue, forKey: Self.targetSizeKey)
            if targetSize != oldValue {
                resetQueueAfterSettingsChange()
            }
        }
    }
    @Published var dimensionLimit: ImageCompressionDimensionLimit {
        didSet {
            defaults.set(dimensionLimit.rawValue, forKey: Self.dimensionLimitKey)
            if dimensionLimit != oldValue {
                resetQueueAfterSettingsChange()
            }
        }
    }
    @Published private(set) var items: [ImageCompressionItem] = []
    @Published private(set) var isCompressing = false
    @Published private(set) var statusMessage = "图片只在这台 Mac 上处理，不会上传"

    private static let outputFormatKey = "imageCompression.outputFormat.v1"
    private static let targetSizeKey = "imageCompression.targetSize.v1"
    private static let dimensionLimitKey = "imageCompression.dimensionLimit.v1"

    private let defaults: UserDefaults
    private let service: ImageCompressionService
    private var compressionTask: Task<Void, Never>?

    init(
        defaults: UserDefaults = .standard,
        service: ImageCompressionService = ImageCompressionService()
    ) {
        self.defaults = defaults
        self.service = service

        if let rawFormat = defaults.string(forKey: Self.outputFormatKey),
           let format = ImageCompressionOutputFormat(rawValue: rawFormat),
           format.isAvailable {
            outputFormat = format
        } else {
            outputFormat = .automatic
        }

        let rawTarget = defaults.integer(forKey: Self.targetSizeKey)
        targetSize = ImageCompressionTargetSize(rawValue: rawTarget) ?? .kilobytes500

        let rawDimension = defaults.integer(forKey: Self.dimensionLimitKey)
        dimensionLimit = ImageCompressionDimensionLimit(rawValue: rawDimension) ?? .original
    }

    var runnableItemCount: Int {
        items.count { $0.state.isRunnable }
    }

    var canCompress: Bool {
        runnableItemCount > 0 && !isCompressing
    }

    func addImages(_ urls: [URL]) {
        var added = 0
        var rejected = 0
        let existing = Set(items.map { $0.sourceURL.standardizedFileURL })
        var seen = existing

        for url in urls {
            let standardized = url.standardizedFileURL
            guard !seen.contains(standardized), Self.looksLikeImage(url) else {
                rejected += 1
                continue
            }

            let isAccessing = url.startAccessingSecurityScopedResource()
            let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
            if isAccessing {
                url.stopAccessingSecurityScopedResource()
            }
            guard let byteCount = (attributes?[.size] as? NSNumber)?.int64Value,
                  byteCount > 0 else {
                rejected += 1
                continue
            }

            seen.insert(standardized)
            items.append(ImageCompressionItem(
                id: UUID(),
                sourceURL: url,
                originalBytes: byteCount,
                state: .pending
            ))
            added += 1
        }

        if added > 0 {
            statusMessage = "已添加 \(added) 张图片，压缩结果会保存到原文件夹且不覆盖原图"
        } else if rejected > 0 {
            statusMessage = "没有添加新图片；请拖入 JPEG、PNG、HEIC、TIFF 或 WebP 等静态图片"
        }
    }

    func reportImporterError(_ error: Error) {
        statusMessage = "选择图片失败：\(error.localizedDescription)"
    }

    func compressAll() {
        guard canCompress else { return }
        let itemIDs = items.filter { $0.state.isRunnable }.map(\.id)
        let settings = ImageCompressionSettings(
            outputFormat: outputFormat,
            targetBytes: targetSize.rawValue,
            dimensionLimit: dimensionLimit
        )
        let service = service

        isCompressing = true
        statusMessage = "正在本地压缩 0/\(itemIDs.count)…"

        compressionTask = Task { [weak self] in
            guard let self else { return }
            var finished = 0

            for itemID in itemIDs {
                if Task.isCancelled { break }
                guard let index = items.firstIndex(where: { $0.id == itemID }) else { continue }
                let sourceURL = items[index].sourceURL
                items[index].state = .compressing

                let workerTask = Task.detached(priority: .userInitiated) {
                    do {
                        return ImageCompressionWorkerResult.success(
                            try service.compress(sourceURL: sourceURL, settings: settings)
                        )
                    } catch is CancellationError {
                        return ImageCompressionWorkerResult.cancelled
                    } catch {
                        return ImageCompressionWorkerResult.failure(error.localizedDescription)
                    }
                }
                let workerResult = await withTaskCancellationHandler {
                    await workerTask.value
                } onCancel: {
                    workerTask.cancel()
                }

                guard let refreshedIndex = items.firstIndex(where: { $0.id == itemID }) else {
                    continue
                }
                switch workerResult {
                case .success(.compressed(let result)):
                    items[refreshedIndex].state = .completed(result)
                case .success(.alreadyOptimized):
                    items[refreshedIndex].state = .alreadyOptimized("原图已经更小，未生成更大的副本")
                case .failure(let message):
                    items[refreshedIndex].state = .failed(message)
                case .cancelled:
                    items[refreshedIndex].state = .pending
                }

                finished += 1
                statusMessage = "正在本地压缩 \(finished)/\(itemIDs.count)…"
            }

            isCompressing = false
            compressionTask = nil
            statusMessage = Task.isCancelled
                ? "已停止压缩，尚未处理的图片仍在列表中"
                : "处理完成；原图未修改，结果已保存到各自原文件夹"
        }
    }

    func cancelCompression() {
        guard isCompressing else { return }
        compressionTask?.cancel()
        statusMessage = "正在停止…"
    }

    func remove(_ itemID: UUID) {
        guard !isCompressing else { return }
        items.removeAll { $0.id == itemID }
        if items.isEmpty {
            statusMessage = "图片只在这台 Mac 上处理，不会上传"
        }
    }

    func clear() {
        guard !isCompressing else { return }
        items = []
        statusMessage = "图片只在这台 Mac 上处理，不会上传"
    }

    func reveal(_ result: ImageCompressionResult) {
        NSWorkspace.shared.activateFileViewerSelecting([result.outputURL])
    }

    func open(_ result: ImageCompressionResult) {
        NSWorkspace.shared.open(result.outputURL)
    }

    private func resetQueueAfterSettingsChange() {
        guard !isCompressing else { return }
        var resetAnyItem = false

        for index in items.indices {
            switch items[index].state {
            case .completed, .alreadyOptimized, .failed:
                items[index].state = .pending
                resetAnyItem = true
            case .pending, .compressing:
                break
            }
        }

        if resetAnyItem {
            statusMessage = "设置已更改，可以按新设置重新压缩；已有结果文件不会被覆盖"
        }
    }

    private static func looksLikeImage(_ url: URL) -> Bool {
        if let values = try? url.resourceValues(forKeys: [.contentTypeKey]),
           let contentType = values.contentType,
           contentType.conforms(to: .image) {
            return true
        }
        guard let type = UTType(filenameExtension: url.pathExtension) else { return false }
        return type.conforms(to: .image)
    }
}

@MainActor
struct ImageCompressionPage: View {
    @ObservedObject private var model: ImageCompressionFeatureModel
    @State private var showingImporter = false
    @State private var isDropTargeted = false

    init(model: ImageCompressionFeatureModel) {
        self.model = model
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                PageHeader(
                    title: "图片压缩",
                    subtitle: "在 Mac 本地智能缩小图片体积，可按目标大小批量处理，原图不会被覆盖"
                )

                privacyBanner
                settingsCard
                dropZone
                queueCard
            }
            .padding(30)
        }
        .fileImporter(
            isPresented: $showingImporter,
            allowedContentTypes: [.image],
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case .success(let urls):
                model.addImages(urls)
            case .failure(let error):
                model.reportImporterError(error)
            }
        }
    }

    private var privacyBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "lock.shield")
                .font(.system(size: 21, weight: .medium))
                .foregroundStyle(Color.green)
            VStack(alignment: .leading, spacing: 3) {
                Text("完全本地压缩")
                    .font(.headline)
                Text("不会上传图片，也不会覆盖原文件；只有压缩结果确实更小时才会保存。")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(16)
        .background(Color.green.opacity(0.08))
        .clipShape(RoundedRectangle(cornerRadius: 13))
        .overlay {
            RoundedRectangle(cornerRadius: 13)
                .stroke(Color.green.opacity(0.22), lineWidth: 1)
        }
    }

    private var settingsCard: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text("压缩设置")
                    .font(.headline)
                Spacer()
                Text("默认兼顾清晰度与体积")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("输出格式")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                Picker("输出格式", selection: $model.outputFormat) {
                    ForEach(ImageCompressionOutputFormat.allCases) { format in
                        Text(format.title)
                            .tag(format)
                            .disabled(!format.isAvailable)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                Text(model.outputFormat.summary)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            HStack(spacing: 24) {
                Picker("每张目标大小", selection: $model.targetSize) {
                    ForEach(ImageCompressionTargetSize.allCases) { target in
                        Text(target.title).tag(target)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Picker("图片尺寸", selection: $model.dimensionLimit) {
                    ForEach(ImageCompressionDimensionLimit.allCases) { limit in
                        Text(limit.title).tag(limit)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            if model.outputFormat == .png {
                Label(
                    "PNG 会保留透明与无损像素，因此降幅可能不如智能、JPEG 或 HEIC。",
                    systemImage: "info.circle"
                )
                .font(.caption)
                .foregroundStyle(.orange)
            } else if model.outputFormat == .jpeg || model.outputFormat == .heic {
                Label(
                    "若原图含透明区域，将以白色背景合成后输出。",
                    systemImage: "info.circle"
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            Label(
                model.dimensionLimit == .original
                    ? "默认保持原始尺寸，并守住清晰度下限；若无法安全达到目标大小，会保留更清晰的结果。"
                    : "选择尺寸限制后会先缩到指定最长边，必要时最低缩至 960 px 以接近目标大小。",
                systemImage: "sparkles"
            )
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .padding(20)
        .background(.background)
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .stroke(Color.crossToolBorder, lineWidth: 1)
        }
        .disabled(model.isCompressing)
    }

    private var dropZone: some View {
        Button {
            showingImporter = true
        } label: {
            VStack(spacing: 10) {
                Image(systemName: "photo.badge.plus")
                    .font(.system(size: 30, weight: .light))
                    .foregroundStyle(Color.crossToolAccent)
                Text("拖入图片，或点击添加")
                    .font(.headline)
                Text("支持批量选择静态图片")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .frame(height: 128)
            .background(isDropTargeted ? Color.crossToolAccent.opacity(0.08) : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .stroke(
                    isDropTargeted ? Color.crossToolAccent : Color.secondary.opacity(0.42),
                    style: StrokeStyle(lineWidth: 1.3, dash: [6, 5])
                )
        }
        .dropDestination(for: URL.self) { urls, _ in
            model.addImages(urls)
            return !urls.isEmpty
        } isTargeted: { targeted in
            isDropTargeted = targeted
        }
        .disabled(model.isCompressing)
    }

    private var queueCard: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("待处理图片")
                        .font(.headline)
                    Text(model.statusMessage)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if !model.items.isEmpty {
                    Button("清空") { model.clear() }
                        .disabled(model.isCompressing)
                }
                Button("添加图片", systemImage: "plus") {
                    showingImporter = true
                }
                .disabled(model.isCompressing)
                if model.isCompressing {
                    Button("停止", systemImage: "stop.fill", role: .destructive) {
                        model.cancelCompression()
                    }
                } else {
                    Button("开始压缩", systemImage: "arrow.down.right.and.arrow.up.left") {
                        model.compressAll()
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(!model.canCompress)
                }
            }
            .padding(16)

            Divider()

            if model.items.isEmpty {
                EmptyContentRow(systemImage: "photo.on.rectangle.angled", text: "还没有图片")
            } else {
                LazyVStack(spacing: 0) {
                    ForEach(model.items) { item in
                        ImageCompressionItemRow(item: item, model: model)
                        if item.id != model.items.last?.id {
                            Divider()
                        }
                    }
                }
            }
        }
        .background(.background)
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .stroke(Color.crossToolBorder, lineWidth: 1)
        }
    }
}

@MainActor
private struct ImageCompressionItemRow: View {
    let item: ImageCompressionItem
    @ObservedObject var model: ImageCompressionFeatureModel

    var body: some View {
        HStack(spacing: 14) {
            thumbnail

            VStack(alignment: .leading, spacing: 5) {
                Text(item.sourceURL.lastPathComponent)
                    .lineLimit(1)
                HStack(spacing: 6) {
                    Text(fileSize(item.originalBytes))
                    Text("→")
                    stateSummary
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            Spacer(minLength: 14)

            if case .completed(let result) = item.state {
                Button("打开", systemImage: "eye") {
                    model.open(result)
                }
                Button("在 Finder 中显示", systemImage: "folder") {
                    model.reveal(result)
                }
                .labelStyle(.iconOnly)
                .help("在 Finder 中显示")
            }

            if case .compressing = item.state {
                ProgressView()
                    .controlSize(.small)
            } else {
                Button("移除", systemImage: "xmark") {
                    model.remove(item.id)
                }
                .labelStyle(.iconOnly)
                .help("从列表移除")
                .disabled(model.isCompressing)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private var thumbnail: some View {
        ImageCompressionThumbnail(sourceURL: item.sourceURL)
    }

    @ViewBuilder
    private var stateSummary: some View {
        switch item.state {
        case .pending:
            Text("等待压缩")
        case .compressing:
            Text("正在压缩…")
        case .completed(let result):
            HStack(spacing: 6) {
                Text(fileSize(result.compressedBytes))
                    .foregroundStyle(.primary)
                Text("缩小 \(result.reductionPercentage)%")
                    .foregroundStyle(.green)
                Text("\(result.pixelWidth)×\(result.pixelHeight) · \(result.outputFormat.title)")
                if !result.metTargetSize {
                    Text("为保持清晰，未达到目标大小")
                        .foregroundStyle(.orange)
                }
            }
        case .alreadyOptimized(let message):
            Text(message)
                .foregroundStyle(.secondary)
        case .failed(let message):
            Text(message)
                .foregroundStyle(.red)
    }
}

@MainActor
private struct ImageCompressionThumbnail: View {
    let sourceURL: URL
    @State private var image: CGImage?
    @State private var didFail = false

    var body: some View {
        Group {
            if let image {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .scaledToFill()
            } else if didFail {
                Image(systemName: "photo")
                    .font(.system(size: 22))
                    .foregroundStyle(.secondary)
            } else {
                ProgressView()
                    .controlSize(.small)
            }
        }
        .frame(width: 58, height: 48)
        .background(Color.secondary.opacity(0.07))
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .overlay {
            RoundedRectangle(cornerRadius: 8)
                .stroke(Color.crossToolBorder, lineWidth: 1)
        }
        .task(id: sourceURL) {
            image = nil
            didFail = false
            let loadedImage = await Task.detached(priority: .utility) {
                ImageCompressionThumbnailLoader.load(from: sourceURL)
            }.value
            guard !Task.isCancelled else { return }
            image = loadedImage
            didFail = loadedImage == nil
        }
    }
}

private enum ImageCompressionThumbnailLoader {
    nonisolated static func load(from sourceURL: URL) -> CGImage? {
        let isAccessing = sourceURL.startAccessingSecurityScopedResource()
        defer {
            if isAccessing {
                sourceURL.stopAccessingSecurityScopedResource()
            }
        }

        guard let source = CGImageSourceCreateWithURL(sourceURL as CFURL, nil) else {
            return nil
        }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 128,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }
}

private func fileSize(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
