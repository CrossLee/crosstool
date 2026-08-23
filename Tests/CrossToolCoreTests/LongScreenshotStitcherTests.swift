import CoreGraphics
import Foundation
import Testing
@testable import CrossToolCore

@Test func longScreenshotStitchesMultipleOverlappingFramesIncrementally() throws {
    let source = try makePatternImage(width: 80, height: 420, seed: 3)
    let first = try crop(source, y: 0, height: 180)
    let second = try crop(source, y: 110, height: 180)
    let third = try crop(source, y: 240, height: 180)
    let limits = LongScreenshotLimits(minimumOverlapPixels: 12)
    let accumulator = try LongScreenshotAccumulator(firstFrame: first, limits: limits)

    let secondResult = try accumulator.append(second, preferredDirection: .downward)
    let thirdResult = try accumulator.append(third, preferredDirection: .downward)

    #expect(secondResult.overlapPixels == 70)
    #expect(secondResult.appendedPixels == 110)
    #expect(secondResult.confidence > 0.99)
    #expect(thirdResult.overlapPixels == 50)
    #expect(thirdResult.appendedPixels == 130)
    #expect(thirdResult.confidence > 0.99)
    #expect(accumulator.frameCount == 3)
    #expect(accumulator.outputWidth == 80)
    #expect(accumulator.outputHeight == 420)
    #expect(accumulator.outputPixelCount == 33_600)

    let rendered = try accumulator.render()
    #expect(rendered.width == 80)
    #expect(rendered.height == 420)
    let renderedBytes = try rgbaBytes(rendered)
    let sourceBytes = try rgbaBytes(source)
    #expect(renderedBytes.elementsEqual(sourceBytes))
}

@Test func longScreenshotRejectsRepeatedFrameWithoutGrowingOutput() throws {
    let frame = try makePatternImage(width: 72, height: 180, seed: 11)
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: frame,
        limits: LongScreenshotLimits(minimumOverlapPixels: 12)
    )

    #expect(throws: LongScreenshotStitchError.duplicateFrame) {
        try accumulator.append(frame)
    }
    #expect(accumulator.frameCount == 1)
    #expect(accumulator.outputHeight == 180)
}

@Test func longScreenshotCanGrowUpwardWhenTheScrollDirectionIsKnown() throws {
    let source = try makePatternImage(width: 80, height: 420, seed: 19)
    let first = try crop(source, y: 240, height: 180)
    let second = try crop(source, y: 110, height: 180)
    let third = try crop(source, y: 0, height: 180)
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(minimumOverlapPixels: 12)
    )

    let secondResult = try accumulator.append(second, preferredDirection: .upward)
    let thirdResult = try accumulator.append(third, preferredDirection: .upward)

    #expect(secondResult.direction == .upward)
    #expect(secondResult.overlapPixels == 50)
    #expect(thirdResult.direction == .upward)
    #expect(thirdResult.overlapPixels == 70)
    #expect(accumulator.direction == .upward)
    #expect(accumulator.outputHeight == 420)
    #expect(try rgbaBytes(accumulator.render()).elementsEqual(rgbaBytes(source)))
}

@Test func longScreenshotRejectsCrossDirectionAmbiguityWithoutAHint() throws {
    let firstBlock = Array(0..<100)
    let secondBlock = Array(100..<180)
    let first = try makeRowSequenceImage(
        width: 80,
        rows: firstBlock + secondBlock
    )
    let rotated = try makeRowSequenceImage(
        width: 80,
        rows: secondBlock + firstBlock
    )
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(minimumOverlapPixels: 12)
    )

    #expect(throws: LongScreenshotStitchError.ambiguousOverlap) {
        try accumulator.append(rotated)
    }
    #expect(accumulator.frameCount == 1)
    #expect(accumulator.outputHeight == 180)
    #expect(accumulator.direction == nil)
}

@Test func longScreenshotDoesNotUseReverseMatchWhenPreferredDirectionIsAmbiguous() throws {
    let uniqueRows = Array(0..<100)
    let repeatedRows = Array(repeating: Array(100..<120), count: 4).flatMap { $0 }
    let first = try makeRowSequenceImage(
        width: 80,
        rows: uniqueRows + repeatedRows
    )
    let rotated = try makeRowSequenceImage(
        width: 80,
        rows: repeatedRows + uniqueRows
    )
    let limits = LongScreenshotLimits(minimumOverlapPixels: 12)

    // The reverse direction has one exact 100-row match, while the preferred
    // downward direction has several equally exact periodic seams.
    let reverseAccumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: limits
    )
    let reverseResult = try reverseAccumulator.append(
        rotated,
        preferredDirection: .upward
    )
    #expect(reverseResult.direction == .upward)
    #expect(reverseResult.overlapPixels == 100)

    let preferredAccumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: limits
    )
    #expect(throws: LongScreenshotStitchError.ambiguousOverlap) {
        try preferredAccumulator.append(
            rotated,
            preferredDirection: .downward
        )
    }
    #expect(preferredAccumulator.frameCount == 1)
    #expect(preferredAccumulator.direction == nil)
}

@Test func longScreenshotPreservesAmbiguousErrorAfterDirectionLocks() throws {
    let leadingRows = Array(0..<100)
    let overlapRows = Array(100..<180)
    let repeatedRows = Array(repeating: Array(180..<200), count: 5).flatMap { $0 }
    let unrelatedTail = Array(0..<80)
    let first = try makeRowSequenceImage(
        width: 80,
        rows: leadingRows + overlapRows
    )
    let second = try makeRowSequenceImage(
        width: 80,
        rows: overlapRows + repeatedRows
    )
    let ambiguousThird = try makeRowSequenceImage(
        width: 80,
        rows: repeatedRows + unrelatedTail
    )
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(minimumOverlapPixels: 12)
    )

    let accepted = try accumulator.append(
        second,
        preferredDirection: .downward
    )
    #expect(accepted.direction == .downward)
    #expect(accumulator.direction == .downward)
    #expect(accumulator.outputHeight == 280)

    #expect(throws: LongScreenshotStitchError.ambiguousOverlap) {
        try accumulator.append(ambiguousThird)
    }
    #expect(accumulator.frameCount == 2)
    #expect(accumulator.outputHeight == 280)
    #expect(accumulator.direction == .downward)
}

@Test func longScreenshotPreservesLowConfidenceErrorAfterDirectionLocks() throws {
    let source = try makePatternImage(width: 80, height: 240, seed: 23)
    let first = try crop(source, y: 0, height: 180)
    let second = try crop(source, y: 60, height: 180)
    let unrelated = try makeSolidImage(width: 80, height: 180, value: 255)
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            minimumOverlapPixels: 12,
            maximumMeanDifference: 0.08
        )
    )

    _ = try accumulator.append(second, preferredDirection: .downward)
    do {
        _ = try accumulator.append(unrelated)
        Issue.record("Expected the locked-direction confidence error to survive")
    } catch let error as LongScreenshotStitchError {
        if case .lowConfidence = error {
            // Expected actionable error.
        } else {
            Issue.record("Expected lowConfidence, got \(error)")
        }
    }
    #expect(accumulator.frameCount == 2)
    #expect(accumulator.direction == .downward)
}

@Test func longScreenshotRejectsAnAmbiguousRepeatedListSeam() throws {
    let source = try makePeriodicRowsImage(width: 80, height: 300, period: 20)
    let first = try crop(source, y: 0, height: 180)
    let second = try crop(source, y: 50, height: 180)
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(minimumOverlapPixels: 12)
    )

    #expect(throws: LongScreenshotStitchError.ambiguousOverlap) {
        try accumulator.append(second, preferredDirection: .downward)
    }
    #expect(accumulator.outputHeight == 180)
}

@Test func longScreenshotRejectsDifferentWidth() throws {
    let first = try makePatternImage(width: 72, height: 180, seed: 5)
    let second = try makePatternImage(width: 70, height: 180, seed: 5)
    let accumulator = try LongScreenshotAccumulator(firstFrame: first)

    #expect(throws: LongScreenshotStitchError.widthMismatch(expected: 72, actual: 70)) {
        try accumulator.append(second)
    }
}

@Test func longScreenshotRejectsLowConfidenceOverlap() throws {
    let first = try makeSolidImage(width: 80, height: 180, value: 0)
    let unrelated = try makeSolidImage(width: 80, height: 180, value: 255)
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            minimumOverlapPixels: 12,
            maximumMeanDifference: 0.08
        )
    )

    do {
        _ = try accumulator.append(unrelated)
        Issue.record("Expected an unrelated frame to be rejected")
    } catch let error as LongScreenshotStitchError {
        if case let .lowConfidence(confidence) = error {
            #expect(confidence < 0.92)
        } else {
            Issue.record("Expected lowConfidence, got \(error)")
        }
    }
    #expect(accumulator.frameCount == 1)
}

private func makeSolidImage(width: Int, height: Int, value: UInt8) throws -> CGImage {
    var pixels = [UInt8](repeating: value, count: width * height * 4)
    for pixel in 0..<(width * height) {
        pixels[pixel * 4 + 3] = 255
    }
    return try makeImage(width: width, height: height, pixels: pixels)
}

@Test func longScreenshotEnforcesPixelAndHeightLimitsBeforeAppend() throws {
    let source = try makePatternImage(width: 80, height: 300, seed: 7)
    let first = try crop(source, y: 0, height: 160)
    let second = try crop(source, y: 100, height: 160)

    let pixelLimited = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            maximumPixelCount: 17_000,
            maximumHeight: 1_000,
            minimumOverlapPixels: 12
        )
    )
    #expect(throws: LongScreenshotStitchError.exceedsPixelLimit(maximum: 17_000)) {
        try pixelLimited.append(second)
    }
    #expect(pixelLimited.frameCount == 1)

    let heightLimited = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            maximumPixelCount: 100_000,
            maximumHeight: 200,
            minimumOverlapPixels: 12
        )
    )
    #expect(throws: LongScreenshotStitchError.exceedsHeightLimit(maximum: 200)) {
        try heightLimited.append(second)
    }
    #expect(heightLimited.frameCount == 1)
}

@Test func longScreenshotRejectsOversizedIncomingFramesBeforeMatching() throws {
    let first = try makePatternImage(width: 80, height: 160, seed: 29)
    let pixelOversized = try makePatternImage(width: 80, height: 300, seed: 31)
    let pixelLimited = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            maximumPixelCount: 17_000,
            maximumHeight: 1_000,
            minimumOverlapPixels: 12
        )
    )

    #expect(throws: LongScreenshotStitchError.exceedsPixelLimit(maximum: 17_000)) {
        try pixelLimited.append(pixelOversized)
    }
    #expect(pixelLimited.frameCount == 1)
    #expect(pixelLimited.direction == nil)

    let heightOversized = try makePatternImage(width: 80, height: 220, seed: 37)
    let heightLimited = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            maximumPixelCount: 100_000,
            maximumHeight: 200,
            minimumOverlapPixels: 12
        )
    )
    #expect(throws: LongScreenshotStitchError.exceedsHeightLimit(maximum: 200)) {
        try heightLimited.append(heightOversized)
    }
    #expect(heightLimited.frameCount == 1)
    #expect(heightLimited.direction == nil)
}

@Test func longScreenshotAcceptsExactlyTheMinimumNewContent() throws {
    let source = try makePatternImage(width: 80, height: 188, seed: 41)
    let first = try crop(source, y: 0, height: 180)
    let second = try crop(source, y: 8, height: 180)
    let accumulator = try LongScreenshotAccumulator(
        firstFrame: first,
        limits: LongScreenshotLimits(
            minimumOverlapPixels: 12,
            minimumNewContentPixels: 8
        )
    )

    let result = try accumulator.append(
        second,
        preferredDirection: .downward
    )
    #expect(result.overlapPixels == 172)
    #expect(result.appendedPixels == 8)
    #expect(accumulator.outputHeight == 188)
    #expect(try rgbaBytes(accumulator.render()).elementsEqual(rgbaBytes(source)))
}

private enum LongScreenshotTestImageError: Error {
    case imageCreationFailed
    case cropFailed
}

private func makePatternImage(width: Int, height: Int, seed: Int) throws -> CGImage {
    var pixels = [UInt8](repeating: 0, count: width * height * 4)
    for y in 0..<height {
        for x in 0..<width {
            let offset = (y * width + x) * 4
            pixels[offset] = UInt8((y * 29 + x * 17 + seed * 31) & 0xff)
            pixels[offset + 1] = UInt8((y * 47 + x * 7 + seed * 53) & 0xff)
            pixels[offset + 2] = UInt8((y * 13 + x * 41 + seed * 71) & 0xff)
            pixels[offset + 3] = 255
        }
    }

    return try makeImage(width: width, height: height, pixels: pixels)
}

private func makeRowSequenceImage(width: Int, rows: [Int]) throws -> CGImage {
    var pixels = [UInt8](repeating: 0, count: width * rows.count * 4)
    for (y, rowIdentifier) in rows.enumerated() {
        for x in 0..<width {
            let offset = (y * width + x) * 4
            pixels[offset] = UInt8((rowIdentifier * 31 + x * 17) & 0xff)
            pixels[offset + 1] = UInt8((rowIdentifier * 47 + x * 7) & 0xff)
            pixels[offset + 2] = UInt8((rowIdentifier * 13 + x * 41) & 0xff)
            pixels[offset + 3] = 255
        }
    }
    return try makeImage(width: width, height: rows.count, pixels: pixels)
}

private func makePeriodicRowsImage(
    width: Int,
    height: Int,
    period: Int
) throws -> CGImage {
    var pixels = [UInt8](repeating: 0, count: width * height * 4)
    for y in 0..<height {
        for x in 0..<width {
            let offset = (y * width + x) * 4
            let repeatedY = y % period
            pixels[offset] = UInt8((repeatedY * 31 + x * 13) & 0xff)
            pixels[offset + 1] = UInt8((repeatedY * 17 + x * 29) & 0xff)
            pixels[offset + 2] = UInt8((repeatedY * 47 + x * 7) & 0xff)
            pixels[offset + 3] = 255
        }
    }
    return try makeImage(width: width, height: height, pixels: pixels)
}

private func makeImage(width: Int, height: Int, pixels: [UInt8]) throws -> CGImage {
    let data = Data(pixels) as CFData
    guard let provider = CGDataProvider(data: data),
          let image = CGImage(
              width: width,
              height: height,
              bitsPerComponent: 8,
              bitsPerPixel: 32,
              bytesPerRow: width * 4,
              space: CGColorSpaceCreateDeviceRGB(),
              bitmapInfo: CGBitmapInfo(
                  rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
                      | CGBitmapInfo.byteOrder32Big.rawValue
              ),
              provider: provider,
              decode: nil,
              shouldInterpolate: false,
              intent: .defaultIntent
          ) else {
        throw LongScreenshotTestImageError.imageCreationFailed
    }
    return image
}

private func crop(_ image: CGImage, y: Int, height: Int) throws -> CGImage {
    guard let cropped = image.cropping(to: CGRect(
        x: 0,
        y: y,
        width: image.width,
        height: height
    )) else {
        throw LongScreenshotTestImageError.cropFailed
    }
    return cropped
}

private func rgbaBytes(_ image: CGImage) throws -> [UInt8] {
    var bytes = [UInt8](repeating: 0, count: image.width * image.height * 4)
    let rendered = bytes.withUnsafeMutableBytes { storage -> Bool in
        guard let baseAddress = storage.baseAddress,
              let context = CGContext(
                  data: baseAddress,
                  width: image.width,
                  height: image.height,
                  bitsPerComponent: 8,
                  bytesPerRow: image.width * 4,
                  space: CGColorSpaceCreateDeviceRGB(),
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                      | CGBitmapInfo.byteOrder32Big.rawValue
              ) else {
            return false
        }
        context.interpolationQuality = .none
        context.draw(
            image,
            in: CGRect(x: 0, y: 0, width: image.width, height: image.height)
        )
        return true
    }
    guard rendered else {
        throw LongScreenshotTestImageError.imageCreationFailed
    }
    return bytes
}
