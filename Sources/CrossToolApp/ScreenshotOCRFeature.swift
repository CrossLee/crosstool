import AppKit
import CoreGraphics
import Foundation
@preconcurrency import Vision

enum ScreenshotTextRecognitionState: Equatable, Sendable {
    case idle
    case recognizing
    case recognized
    case empty
    case failed(String)
}

protocol ScreenshotTextRecognizing: Sendable {
    func recognizeText(in image: CGImage) async throws -> String
}

enum ScreenshotTextRecognitionError: LocalizedError {
    case imageCropFailed
    case imageTooSmall

    var errorDescription: String? {
        switch self {
        case .imageCropFailed:
            return "无法读取截图中的文字区域"
        case .imageTooSmall:
            return "截图区域太小，无法识别文字"
        }
    }
}

struct ScreenshotOCRTilePlan: Equatable, Sendable {
    let top: Int
    let height: Int

    var bottom: Int { top + height }
}

enum ScreenshotOCRTilePlanner {
    static let maximumTileHeight = 4_096
    static let tileOverlap = 256
    static let tilingThreshold = 6_000

    static func plans(imageHeight: Int) -> [ScreenshotOCRTilePlan] {
        guard imageHeight > tilingThreshold else {
            return [ScreenshotOCRTilePlan(top: 0, height: imageHeight)]
        }

        var plans: [ScreenshotOCRTilePlan] = []
        var top = 0
        while top < imageHeight {
            let bottom = min(top + maximumTileHeight, imageHeight)
            plans.append(ScreenshotOCRTilePlan(top: top, height: bottom - top))
            guard bottom < imageHeight else { break }
            top = bottom - tileOverlap
        }
        return plans
    }
}

struct ScreenshotOCRFragment: Equatable, Sendable {
    let text: String
    let tileIndex: Int
    let top: Double
    let bottom: Double
    let minX: Double
    let maxX: Double
    let confidence: Float
    let edgeDistance: Double

    var midY: Double { (top + bottom) / 2 }
    var height: Double { max(1, bottom - top) }
    var width: Double { max(1, maxX - minX) }
}

enum ScreenshotOCRTextLayout {
    private struct Row {
        var fragments: [ScreenshotOCRFragment]
        var meanMidY: Double
        var maximumHeight: Double

        mutating func append(_ fragment: ScreenshotOCRFragment) {
            let count = Double(fragments.count)
            meanMidY = ((meanMidY * count) + fragment.midY) / (count + 1)
            maximumHeight = max(maximumHeight, fragment.height)
            fragments.append(fragment)
        }
    }

    static func deduplicated(
        _ fragments: [ScreenshotOCRFragment]
    ) -> [ScreenshotOCRFragment] {
        let preferredFirst = fragments.sorted { lhs, rhs in
            if lhs.edgeDistance != rhs.edgeDistance {
                return lhs.edgeDistance > rhs.edgeDistance
            }
            if lhs.confidence != rhs.confidence {
                return lhs.confidence > rhs.confidence
            }
            if lhs.text.count != rhs.text.count {
                return lhs.text.count > rhs.text.count
            }
            return lhs.tileIndex < rhs.tileIndex
        }

        var result: [ScreenshotOCRFragment] = []
        for fragment in preferredFirst {
            let isDuplicate = result.contains { retained in
                isSameObservation(fragment, retained)
            }
            if !isDuplicate {
                result.append(fragment)
            }
        }
        return result
    }

    static func text(from fragments: [ScreenshotOCRFragment]) -> String {
        let verticallySorted = deduplicated(fragments).sorted { lhs, rhs in
            if lhs.midY != rhs.midY { return lhs.midY < rhs.midY }
            return lhs.minX < rhs.minX
        }
        var rows: [Row] = []

        for fragment in verticallySorted {
            if var last = rows.last {
                let tolerance = max(last.maximumHeight, fragment.height) * 0.55
                if abs(last.meanMidY - fragment.midY) <= tolerance {
                    last.append(fragment)
                    rows[rows.count - 1] = last
                    continue
                }
            }
            rows.append(Row(
                fragments: [fragment],
                meanMidY: fragment.midY,
                maximumHeight: fragment.height
            ))
        }

        return rows.map { row in
            row.fragments
                .sorted { $0.minX < $1.minX }
                .map(\.text)
                .joined(separator: " ")
        }
        .joined(separator: "\n")
        .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func isSameObservation(
        _ lhs: ScreenshotOCRFragment,
        _ rhs: ScreenshotOCRFragment
    ) -> Bool {
        guard lhs.tileIndex != rhs.tileIndex,
              abs(lhs.tileIndex - rhs.tileIndex) == 1 else {
            return false
        }

        let verticalOverlap = max(0, min(lhs.bottom, rhs.bottom) - max(lhs.top, rhs.top))
        let horizontalOverlap = max(0, min(lhs.maxX, rhs.maxX) - max(lhs.minX, rhs.minX))
        let verticalRatio = verticalOverlap / min(lhs.height, rhs.height)
        let horizontalRatio = horizontalOverlap / min(lhs.width, rhs.width)
        if verticalRatio >= 0.55, horizontalRatio >= 0.55 {
            return true
        }

        let normalizedLeft = normalizedText(lhs.text)
        let normalizedRight = normalizedText(rhs.text)
        let leftEdgeTolerance = max(24, min(lhs.width, rhs.width) * 0.2)
        return normalizedLeft == normalizedRight
            && verticalRatio >= 0.35
            && abs(lhs.minX - rhs.minX) <= leftEdgeTolerance
    }

    private static func normalizedText(_ text: String) -> String {
        text
            .lowercased()
            .components(separatedBy: .whitespacesAndNewlines)
            .joined()
    }
}

protocol ScreenshotOCRCancellationRequest: AnyObject {
    func cancel()
}

extension VNRequest: ScreenshotOCRCancellationRequest {}

final class ScreenshotOCRRequestCancellationBox: @unchecked Sendable {
    private let lock = NSLock()
    private weak var request: (any ScreenshotOCRCancellationRequest)?
    private var isCancelled = false

    func install(_ request: any ScreenshotOCRCancellationRequest) {
        lock.lock()
        if isCancelled {
            lock.unlock()
            request.cancel()
        } else {
            self.request = request
            lock.unlock()
        }
    }

    func cancel() {
        lock.lock()
        guard !isCancelled else {
            lock.unlock()
            return
        }
        isCancelled = true
        let request = request
        lock.unlock()
        request?.cancel()
    }

    func clear() {
        lock.lock()
        request = nil
        lock.unlock()
    }
}

/// Runs Apple's Vision OCR locally. The actor keeps expensive Vision work off
/// the main actor and serializes requests so multiple large screenshots cannot
/// compete for memory.
actor VisionScreenshotTextRecognizer: ScreenshotTextRecognizing {
    private static let preferredLanguages = ["zh-Hans", "zh-Hant", "en-US"]

    func recognizeText(in image: CGImage) async throws -> String {
        try Task.checkCancellation()
        guard image.width > 2, image.height > 2 else {
            throw ScreenshotTextRecognitionError.imageTooSmall
        }
        let plans = ScreenshotOCRTilePlanner.plans(imageHeight: image.height)
        var fragments: [ScreenshotOCRFragment] = []

        for (tileIndex, plan) in plans.enumerated() {
            try Task.checkCancellation()
            let tileImage: CGImage
            if plans.count == 1 {
                tileImage = image
            } else {
                guard let cropped = image.cropping(to: CGRect(
                    x: 0,
                    y: plan.top,
                    width: image.width,
                    height: plan.height
                )) else {
                    throw ScreenshotTextRecognitionError.imageCropFailed
                }
                tileImage = cropped
            }
            fragments.append(contentsOf: try await recognizeFragments(
                in: tileImage,
                plan: plan,
                tileIndex: tileIndex,
                fullImageWidth: image.width
            ))
        }

        try Task.checkCancellation()
        return ScreenshotOCRTextLayout.text(from: fragments)
    }

    private func recognizeFragments(
        in image: CGImage,
        plan: ScreenshotOCRTilePlan,
        tileIndex: Int,
        fullImageWidth: Int
    ) async throws -> [ScreenshotOCRFragment] {
        let request = VNRecognizeTextRequest()
        // Crosio supports macOS 14. Vision revision 3 is the newest revision
        // available there and supports Chinese, rotation and handwriting.
        request.revision = 3
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.automaticallyDetectsLanguage = false
        request.preferBackgroundProcessing = true

        let supportedLanguages = try request.supportedRecognitionLanguages()
        let languages = Self.preferredLanguages.filter(supportedLanguages.contains)
        if !languages.isEmpty {
            request.recognitionLanguages = languages
        }

        let cancellationBox = ScreenshotOCRRequestCancellationBox()
        cancellationBox.install(request)
        defer { cancellationBox.clear() }

        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            let handler = VNImageRequestHandler(cgImage: image, options: [:])
            try handler.perform([request])
            try Task.checkCancellation()

            return (request.results ?? []).compactMap { observation in
                guard let candidate = observation.topCandidates(1).first else { return nil }
                let text = candidate.string.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { return nil }

                let box = observation.boundingBox
                let top = Double(plan.top) + ((1 - box.maxY) * Double(plan.height))
                let bottom = Double(plan.top) + ((1 - box.minY) * Double(plan.height))
                let midY = (top + bottom) / 2
                return ScreenshotOCRFragment(
                    text: text,
                    tileIndex: tileIndex,
                    top: top,
                    bottom: bottom,
                    minX: box.minX * Double(fullImageWidth),
                    maxX: box.maxX * Double(fullImageWidth),
                    confidence: candidate.confidence,
                    edgeDistance: min(
                        midY - Double(plan.top),
                        Double(plan.bottom) - midY
                    )
                )
            }
        } onCancel: {
            cancellationBox.cancel()
        }
    }
}

enum ScreenshotTextPasteboardError: LocalizedError {
    case emptyText
    case writeFailed

    var errorDescription: String? {
        switch self {
        case .emptyText:
            return "没有可复制的识别文字"
        case .writeFailed:
            return "系统剪贴板拒绝写入文字"
        }
    }
}

@MainActor
protocol ScreenshotTextPasteboardWriting {
    func writeText(_ text: String) throws
}

@MainActor
struct ScreenshotTextPasteboardWriter: ScreenshotTextPasteboardWriting {
    private let pasteboard: NSPasteboard

    init(pasteboard: NSPasteboard = .general) {
        self.pasteboard = pasteboard
    }

    func writeText(_ text: String) throws {
        guard !text.isEmpty else { throw ScreenshotTextPasteboardError.emptyText }
        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else {
            throw ScreenshotTextPasteboardError.writeFailed
        }
    }
}
