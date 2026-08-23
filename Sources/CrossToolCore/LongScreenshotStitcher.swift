import CoreGraphics
import Foundation

/// Resource and matching limits used by ``LongScreenshotAccumulator``.
public struct LongScreenshotLimits: Equatable, Sendable {
    public let maximumPixelCount: Int
    public let maximumHeight: Int
    public let minimumOverlapPixels: Int
    public let minimumNewContentPixels: Int
    public let sampleWidth: Int
    public let maximumMeanDifference: Double
    public let duplicateMeanDifference: Double

    public init(
        maximumPixelCount: Int = 20_000_000,
        maximumHeight: Int = 20_000,
        minimumOverlapPixels: Int = 24,
        minimumNewContentPixels: Int = 8,
        sampleWidth: Int = 64,
        maximumMeanDifference: Double = 0.08,
        duplicateMeanDifference: Double = 0.012
    ) {
        self.maximumPixelCount = max(1, maximumPixelCount)
        self.maximumHeight = max(1, maximumHeight)
        self.minimumOverlapPixels = max(2, minimumOverlapPixels)
        self.minimumNewContentPixels = max(1, minimumNewContentPixels)
        self.sampleWidth = min(max(16, sampleWidth), 192)
        self.maximumMeanDifference = min(max(maximumMeanDifference, 0.01), 0.5)
        self.duplicateMeanDifference = min(
            max(duplicateMeanDifference, 0),
            self.maximumMeanDifference
        )
    }
}

public enum LongScreenshotStitchError: LocalizedError, Equatable {
    case invalidFrameDimensions
    case widthMismatch(expected: Int, actual: Int)
    case duplicateFrame
    case ambiguousOverlap
    case directionChanged
    case insufficientOverlap
    case lowConfidence(Double)
    case exceedsPixelLimit(maximum: Int)
    case exceedsHeightLimit(maximum: Int)
    case imageSamplingFailed
    case renderingFailed

    public var errorDescription: String? {
        switch self {
        case .invalidFrameDimensions:
            return "截图画面尺寸无效"
        case let .widthMismatch(expected, actual):
            return "新画面宽度为 \(actual) 像素，与首屏的 \(expected) 像素不一致"
        case .duplicateFrame:
            return "这一屏与上一屏几乎相同，请继续滚动后再采集"
        case .ambiguousOverlap:
            return "页面中存在重复内容，无法可靠确定接缝；请少滚动一些后重试"
        case .directionChanged:
            return "检测到滚动方向改变；请继续沿原方向滚动，避免内容重复"
        case .insufficientOverlap:
            return "没有找到足够的上下重叠区域，请少滚动一些后重试"
        case let .lowConfidence(confidence):
            return "画面重叠匹配不够可靠（置信度 \(Int((confidence * 100).rounded()))%），请回滚一些后重试"
        case let .exceedsPixelLimit(maximum):
            return "长截图超过 \(maximum / 1_000_000) 百万像素上限，请先完成当前截图"
        case let .exceedsHeightLimit(maximum):
            return "长截图高度超过 \(maximum) 像素上限，请先完成当前截图"
        case .imageSamplingFailed:
            return "无法分析这一屏的图像内容"
        case .renderingFailed:
            return "无法生成拼接后的长截图"
        }
    }
}

public enum LongScreenshotScrollDirection: String, Equatable, Sendable {
    case downward
    case upward
}

public struct LongScreenshotAppendResult: Equatable, Sendable {
    public let overlapPixels: Int
    public let appendedPixels: Int
    public let confidence: Double
    public let direction: LongScreenshotScrollDirection

    public init(
        overlapPixels: Int,
        appendedPixels: Int,
        confidence: Double,
        direction: LongScreenshotScrollDirection = .downward
    ) {
        self.overlapPixels = overlapPixels
        self.appendedPixels = appendedPixels
        self.confidence = confidence
        self.direction = direction
    }
}

/// Incrementally joins manually-scrolled captures into one vertical image.
///
/// Matching is only performed between the most recent accepted frame and the
/// new frame. Each possible vertical offset receives a small, fixed-width row
/// comparison and only a bounded number of candidates is verified in detail.
/// As a result, appending a frame is linear in its height instead of comparing
/// every pixel against an ever-growing output image.
public final class LongScreenshotAccumulator {
    private struct Segment {
        let image: CGImage
    }

    private struct FrameAnalysis {
        let width: Int
        let height: Int
        let sampleWidth: Int
        let luminance: [UInt8]

        func rowDifference(to other: FrameAnalysis, row: Int, otherRow: Int) -> Double {
            let firstOffset = row * sampleWidth
            let secondOffset = otherRow * sampleWidth
            var difference = 0
            for column in 0..<sampleWidth {
                difference += abs(
                    Int(luminance[firstOffset + column])
                        - Int(other.luminance[secondOffset + column])
                )
            }
            return Double(difference) / Double(sampleWidth * 255)
        }
    }

    private struct OverlapCandidate {
        let overlap: Int
        let anchorDifference: Double
    }

    private struct OverlapMatch {
        let overlapPixels: Int
        let confidence: Double
        let meanDifference: Double
    }

    private struct OverlapAttempt {
        let match: OverlapMatch?
        let error: LongScreenshotStitchError?
    }

    private let limits: LongScreenshotLimits
    private var segments: [Segment]
    private var lastAnalysis: FrameAnalysis

    public private(set) var outputHeight: Int
    public let outputWidth: Int
    public private(set) var direction: LongScreenshotScrollDirection?

    public var frameCount: Int { segments.count }
    public var outputPixelCount: Int { outputWidth * outputHeight }

    public init(
        firstFrame: CGImage,
        limits: LongScreenshotLimits = LongScreenshotLimits()
    ) throws {
        guard firstFrame.width > 0, firstFrame.height > 0 else {
            throw LongScreenshotStitchError.invalidFrameDimensions
        }
        self.limits = limits
        outputWidth = firstFrame.width
        outputHeight = firstFrame.height
        try Self.validateOutputSize(
            width: outputWidth,
            height: outputHeight,
            limits: limits
        )
        lastAnalysis = try Self.makeAnalysis(for: firstFrame, sampleWidth: limits.sampleWidth)
        segments = [Segment(image: firstFrame)]
    }

    @discardableResult
    public func append(
        _ frame: CGImage,
        preferredDirection: LongScreenshotScrollDirection? = nil
    ) throws -> LongScreenshotAppendResult {
        guard frame.width > 0, frame.height > 0 else {
            throw LongScreenshotStitchError.invalidFrameDimensions
        }
        guard frame.width == outputWidth else {
            throw LongScreenshotStitchError.widthMismatch(
                expected: outputWidth,
                actual: frame.width
            )
        }

        // Reject a frame that can never fit within the configured output
        // limits before allocating its luminance analysis buffer. Since the
        // sampled width never exceeds the source width, this also bounds the
        // analysis allocation by maximumPixelCount.
        try Self.validateOutputSize(
            width: frame.width,
            height: frame.height,
            limits: limits
        )

        let nextAnalysis = try Self.makeAnalysis(for: frame, sampleWidth: limits.sampleWidth)
        if Self.alignedDifference(lastAnalysis, nextAnalysis) <= limits.duplicateMeanDifference {
            throw LongScreenshotStitchError.duplicateFrame
        }

        let match = try Self.directionalMatch(
            previous: lastAnalysis,
            next: nextAnalysis,
            lockedDirection: direction,
            preferredDirection: preferredDirection,
            limits: limits
        )
        let appendedPixels = frame.height - match.overlapPixels
        guard appendedPixels >= limits.minimumNewContentPixels else {
            throw LongScreenshotStitchError.duplicateFrame
        }

        let (newHeight, overflow) = outputHeight.addingReportingOverflow(appendedPixels)
        guard !overflow else {
            throw LongScreenshotStitchError.exceedsHeightLimit(maximum: limits.maximumHeight)
        }
        try Self.validateOutputSize(width: outputWidth, height: newHeight, limits: limits)

        // Keep only an independent copy of the newly revealed strip. Retaining
        // every full source frame would allow tiny scroll steps to consume
        // gigabytes while the final image still remained below its pixel cap.
        let newContent: CGImage
        switch match.direction {
        case .downward:
            newContent = try Self.copyNewContent(
                from: frame,
                cropping: .top(match.overlapPixels)
            )
            segments.append(Segment(image: newContent))
        case .upward:
            newContent = try Self.copyNewContent(
                from: frame,
                cropping: .bottom(match.overlapPixels)
            )
            segments.insert(Segment(image: newContent), at: 0)
        }
        direction = match.direction
        lastAnalysis = nextAnalysis
        outputHeight = newHeight
        return LongScreenshotAppendResult(
            overlapPixels: match.overlapPixels,
            appendedPixels: appendedPixels,
            confidence: match.confidence,
            direction: match.direction
        )
    }

    public func render() throws -> CGImage {
        try Self.validateOutputSize(width: outputWidth, height: outputHeight, limits: limits)
        guard let context = CGContext(
            data: nil,
            width: outputWidth,
            height: outputHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                | CGBitmapInfo.byteOrder32Big.rawValue
        ) else {
            throw LongScreenshotStitchError.renderingFailed
        }

        context.interpolationQuality = .none

        var destinationY = 0
        for segment in segments {
            let visibleImage = segment.image
            let visibleHeight = visibleImage.height
            guard visibleHeight > 0 else {
                throw LongScreenshotStitchError.renderingFailed
            }
            context.draw(
                visibleImage,
                in: CGRect(
                    x: 0,
                    y: outputHeight - destinationY - visibleHeight,
                    width: visibleImage.width,
                    height: visibleImage.height
                )
            )
            destinationY += visibleHeight
        }

        guard destinationY == outputHeight, let result = context.makeImage() else {
            throw LongScreenshotStitchError.renderingFailed
        }
        return result
    }

    private enum CropSide {
        case top(Int)
        case bottom(Int)
    }

    private static func copyNewContent(
        from frame: CGImage,
        cropping: CropSide
    ) throws -> CGImage {
        let overlap: Int
        let sourceY: Int
        switch cropping {
        case let .top(pixels):
            overlap = pixels
            sourceY = pixels
        case let .bottom(pixels):
            overlap = pixels
            sourceY = 0
        }
        let visibleHeight = frame.height - overlap
        guard visibleHeight > 0,
              let cropped = frame.cropping(to: CGRect(
                  x: 0,
                  y: sourceY,
                  width: frame.width,
                  height: visibleHeight
              )),
              let context = CGContext(
                  data: nil,
                  width: frame.width,
                  height: visibleHeight,
                  bitsPerComponent: 8,
                  bytesPerRow: 0,
                  space: CGColorSpaceCreateDeviceRGB(),
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                      | CGBitmapInfo.byteOrder32Big.rawValue
              ) else {
            throw LongScreenshotStitchError.renderingFailed
        }
        context.interpolationQuality = .none
        context.draw(
            cropped,
            in: CGRect(x: 0, y: 0, width: frame.width, height: visibleHeight)
        )
        guard let copy = context.makeImage() else {
            throw LongScreenshotStitchError.renderingFailed
        }
        return copy
    }

    private static func directionalMatch(
        previous: FrameAnalysis,
        next: FrameAnalysis,
        lockedDirection: LongScreenshotScrollDirection?,
        preferredDirection: LongScreenshotScrollDirection?,
        limits: LongScreenshotLimits
    ) throws -> (
        overlapPixels: Int,
        confidence: Double,
        direction: LongScreenshotScrollDirection
    ) {
        let downwardAttempt = overlapAttempt(
            previous: previous,
            next: next,
            limits: limits
        )
        let upwardAttempt = overlapAttempt(
            previous: next,
            next: previous,
            limits: limits
        )
        let downward = downwardAttempt.match
        let upward = upwardAttempt.match

        if let lockedDirection {
            let expected = lockedDirection == .downward ? downward : upward
            if let expected {
                return (expected.overlapPixels, expected.confidence, lockedDirection)
            }

            let expectedError = lockedDirection == .downward
                ? downwardAttempt.error
                : upwardAttempt.error
            if expectedError == .ambiguousOverlap {
                throw LongScreenshotStitchError.ambiguousOverlap
            }
            if (lockedDirection == .downward ? upward : downward) != nil {
                throw LongScreenshotStitchError.directionChanged
            }

            // The direction is already known, so preserve its actionable
            // matching failure instead of flattening ambiguity or poor image
            // quality into a generic "insufficient overlap" message.
            throw expectedError ?? LongScreenshotStitchError.insufficientOverlap
        }

        if let preferredDirection {
            let preferredAttempt = preferredDirection == .downward
                ? downwardAttempt
                : upwardAttempt
            let oppositeAttempt = preferredDirection == .downward
                ? upwardAttempt
                : downwardAttempt

            if let preferred = preferredAttempt.match {
                return (
                    preferred.overlapPixels,
                    preferred.confidence,
                    preferredDirection
                )
            }

            // Repeated rows in the observed scroll direction are not made
            // safe by an incidental reverse-direction match. Accepting that
            // match would prepend instead of append (or vice versa) and
            // silently corrupt the result.
            if preferredAttempt.error == .ambiguousOverlap {
                throw LongScreenshotStitchError.ambiguousOverlap
            }

            if let opposite = oppositeAttempt.match {
                let oppositeDirection: LongScreenshotScrollDirection =
                    preferredDirection == .downward ? .upward : .downward
                return (
                    opposite.overlapPixels,
                    opposite.confidence,
                    oppositeDirection
                )
            }

            throw actionableError(
                preferred: preferredAttempt.error,
                alternate: oppositeAttempt.error
            )
        }

        switch (downward, upward) {
        case let (downward?, nil):
            return (downward.overlapPixels, downward.confidence, .downward)
        case let (nil, upward?):
            return (upward.overlapPixels, upward.confidence, .upward)
        case let (downward?, upward?):
            let separation = abs(downward.meanDifference - upward.meanDifference)
            guard separation >= 0.006 else {
                throw LongScreenshotStitchError.ambiguousOverlap
            }
            if downward.meanDifference < upward.meanDifference {
                return (downward.overlapPixels, downward.confidence, .downward)
            }
            return (upward.overlapPixels, upward.confidence, .upward)
        case (nil, nil):
            throw actionableError(
                preferred: downwardAttempt.error,
                alternate: upwardAttempt.error
            )
        }
    }

    private static func overlapAttempt(
        previous: FrameAnalysis,
        next: FrameAnalysis,
        limits: LongScreenshotLimits
    ) -> OverlapAttempt {
        do {
            return OverlapAttempt(
                match: try findOverlap(previous: previous, next: next, limits: limits),
                error: nil
            )
        } catch let error as LongScreenshotStitchError {
            return OverlapAttempt(match: nil, error: error)
        } catch {
            return OverlapAttempt(match: nil, error: .insufficientOverlap)
        }
    }

    private static func actionableError(
        preferred: LongScreenshotStitchError?,
        alternate: LongScreenshotStitchError?
    ) -> LongScreenshotStitchError {
        if preferred == .ambiguousOverlap {
            return .ambiguousOverlap
        }
        if let preferred, case .lowConfidence = preferred {
            return preferred
        }
        if alternate == .ambiguousOverlap {
            return .ambiguousOverlap
        }
        if let alternate, case .lowConfidence = alternate {
            return alternate
        }
        return preferred ?? alternate ?? .insufficientOverlap
    }

    private static func validateOutputSize(
        width: Int,
        height: Int,
        limits: LongScreenshotLimits
    ) throws {
        guard height <= limits.maximumHeight else {
            throw LongScreenshotStitchError.exceedsHeightLimit(maximum: limits.maximumHeight)
        }
        let (pixelCount, overflow) = width.multipliedReportingOverflow(by: height)
        guard !overflow, pixelCount <= limits.maximumPixelCount else {
            throw LongScreenshotStitchError.exceedsPixelLimit(maximum: limits.maximumPixelCount)
        }
    }

    private static func makeAnalysis(
        for image: CGImage,
        sampleWidth requestedSampleWidth: Int
    ) throws -> FrameAnalysis {
        let sampleWidth = min(requestedSampleWidth, image.width)
        let (sampleCount, overflow) = sampleWidth.multipliedReportingOverflow(
            by: image.height
        )
        guard !overflow, sampleCount > 0 else {
            throw LongScreenshotStitchError.imageSamplingFailed
        }
        var luminance = [UInt8](repeating: 255, count: sampleCount)
        let createdContext = luminance.withUnsafeMutableBytes { bytes -> Bool in
            guard let baseAddress = bytes.baseAddress,
                  let context = CGContext(
                      data: baseAddress,
                      width: sampleWidth,
                      height: image.height,
                      bitsPerComponent: 8,
                      bytesPerRow: sampleWidth,
                      space: CGColorSpaceCreateDeviceGray(),
                      bitmapInfo: CGImageAlphaInfo.none.rawValue
                  ) else {
                return false
            }
            context.interpolationQuality = .medium
            context.setFillColor(gray: 1, alpha: 1)
            context.fill(CGRect(x: 0, y: 0, width: sampleWidth, height: image.height))
            context.draw(
                image,
                in: CGRect(x: 0, y: 0, width: sampleWidth, height: image.height)
            )
            return true
        }
        guard createdContext else {
            throw LongScreenshotStitchError.imageSamplingFailed
        }
        return FrameAnalysis(
            width: image.width,
            height: image.height,
            sampleWidth: sampleWidth,
            luminance: luminance
        )
    }

    private static func alignedDifference(
        _ first: FrameAnalysis,
        _ second: FrameAnalysis
    ) -> Double {
        guard first.width == second.width,
              first.height == second.height,
              first.sampleWidth == second.sampleWidth else {
            return 1
        }
        let rowCount = min(64, first.height)
        guard rowCount > 0 else { return 1 }
        var total = 0.0
        for index in 0..<rowCount {
            let row = evenlySpacedIndex(index, count: rowCount, upperBound: first.height)
            total += first.rowDifference(to: second, row: row, otherRow: row)
        }
        return total / Double(rowCount)
    }

    private static func findOverlap(
        previous: FrameAnalysis,
        next: FrameAnalysis,
        limits: LongScreenshotLimits
    ) throws -> OverlapMatch {
        let commonHeight = min(previous.height, next.height)
        let adaptiveMinimum = max(limits.minimumOverlapPixels, commonHeight / 30)
        let maximumOverlap = commonHeight - limits.minimumNewContentPixels
        guard adaptiveMinimum <= maximumOverlap else {
            throw LongScreenshotStitchError.insufficientOverlap
        }

        let shallowAnchor = mostDistinctiveRow(
            in: next,
            rowCount: min(adaptiveMinimum, 96)
        )
        let broadAnchor = mostDistinctiveRow(
            in: next,
            rowCount: min(maximumOverlap, max(adaptiveMinimum, next.height / 3))
        )
        let anchors = Array(Set([shallowAnchor, broadAnchor])).sorted()
        var candidatesByOverlap: [Int: OverlapCandidate] = [:]
        candidatesByOverlap.reserveCapacity(24)

        for anchorRow in anchors {
            let firstOverlap = max(adaptiveMinimum, anchorRow + 1)
            guard firstOverlap <= maximumOverlap else { continue }
            var anchorCandidates: [OverlapCandidate] = []
            anchorCandidates.reserveCapacity(12)
            for overlap in firstOverlap...maximumOverlap {
                let previousRow = previous.height - overlap + anchorRow
                guard previousRow >= 0, previousRow < previous.height else { continue }
                let difference = previous.rowDifference(
                    to: next,
                    row: previousRow,
                    otherRow: anchorRow
                )
                insertCandidate(
                    OverlapCandidate(overlap: overlap, anchorDifference: difference),
                    into: &anchorCandidates,
                    maximumCount: 12
                )
            }
            for candidate in anchorCandidates {
                if let existing = candidatesByOverlap[candidate.overlap],
                   existing.anchorDifference <= candidate.anchorDifference {
                    continue
                }
                candidatesByOverlap[candidate.overlap] = candidate
            }
        }
        let candidates = Array(candidatesByOverlap.values)
        guard !candidates.isEmpty else {
            throw LongScreenshotStitchError.insufficientOverlap
        }

        let scored = candidates.map { candidate in
            let detailedDifference = overlapDifference(
                previous: previous,
                next: next,
                overlap: candidate.overlap
            )
            let combinedDifference = detailedDifference * 0.9
                + candidate.anchorDifference * 0.1
            return (candidate.overlap, combinedDifference)
        }
        // Frequent captures should reveal only a relatively small new strip.
        // Prefer the largest overlap when two offsets have the same score so
        // repeated table/list rows cannot make an arbitrary dictionary order
        // decide the seam.
        let ranked = scored.sorted { first, second in
            if abs(first.1 - second.1) < 0.000_001 {
                return first.0 > second.0
            }
            return first.1 < second.1
        }
        guard let best = ranked.first else {
            throw LongScreenshotStitchError.insufficientOverlap
        }

        var confidence = min(max(1 - best.1, 0), 1)
        guard best.1 <= limits.maximumMeanDifference else {
            throw LongScreenshotStitchError.lowConfidence(confidence)
        }
        if let runnerUp = ranked.dropFirst().first(where: { abs($0.0 - best.0) > 2 }),
           runnerUp.1 - best.1 < 0.006 {
            if best.1 <= 0.015 {
                throw LongScreenshotStitchError.ambiguousOverlap
            }
            confidence = min(confidence, 0.79)
            throw LongScreenshotStitchError.lowConfidence(confidence)
        }
        return OverlapMatch(
            overlapPixels: best.0,
            confidence: confidence,
            meanDifference: best.1
        )
    }

    private static func mostDistinctiveRow(
        in frame: FrameAnalysis,
        rowCount: Int
    ) -> Int {
        guard rowCount > 1 else { return 0 }
        var bestRow = 0
        var bestRange = -1
        for row in 0..<rowCount {
            let offset = row * frame.sampleWidth
            var minimum = 255
            var maximum = 0
            for column in 0..<frame.sampleWidth {
                let value = Int(frame.luminance[offset + column])
                minimum = min(minimum, value)
                maximum = max(maximum, value)
            }
            let range = maximum - minimum
            if range > bestRange {
                bestRange = range
                bestRow = row
            }
        }
        return bestRow
    }

    private static func insertCandidate(
        _ candidate: OverlapCandidate,
        into candidates: inout [OverlapCandidate],
        maximumCount: Int
    ) {
        if candidates.count < maximumCount {
            candidates.append(candidate)
            return
        }
        guard let worstIndex = candidates.indices.max(
            by: { candidates[$0].anchorDifference < candidates[$1].anchorDifference }
        ), candidate.anchorDifference < candidates[worstIndex].anchorDifference else {
            return
        }
        candidates[worstIndex] = candidate
    }

    private static func overlapDifference(
        previous: FrameAnalysis,
        next: FrameAnalysis,
        overlap: Int
    ) -> Double {
        let verificationRows = min(64, overlap)
        var rowDifferences: [Double] = []
        rowDifferences.reserveCapacity(verificationRows)
        for index in 0..<verificationRows {
            let nextRow = evenlySpacedIndex(index, count: verificationRows, upperBound: overlap)
            let previousRow = previous.height - overlap + nextRow
            rowDifferences.append(
                previous.rowDifference(to: next, row: previousRow, otherRow: nextRow)
            )
        }

        // A scrollbar thumb, caret, sticky control, or transient mouse pointer
        // can alter a few rows. Dropping the noisiest sixth keeps those local
        // changes from defeating an otherwise exact overlap while still
        // requiring most sampled rows to agree.
        rowDifferences.sort()
        let retainedCount = max(1, rowDifferences.count * 5 / 6)
        return rowDifferences.prefix(retainedCount).reduce(0, +) / Double(retainedCount)
    }

    private static func evenlySpacedIndex(
        _ index: Int,
        count: Int,
        upperBound: Int
    ) -> Int {
        guard count > 1, upperBound > 1 else { return 0 }
        return index * (upperBound - 1) / (count - 1)
    }
}
