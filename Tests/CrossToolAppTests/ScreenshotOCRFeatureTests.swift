import Foundation
@testable import CrossToolApp
import Testing

@Suite("Screenshot OCR feature")
struct ScreenshotOCRFeatureTests {
    @Test("Very tall screenshots use overlapping bounded tiles")
    func longScreenshotTilePlan() throws {
        let plans = ScreenshotOCRTilePlanner.plans(imageHeight: 20_000)

        #expect(plans.count == 6)
        #expect(plans.first == ScreenshotOCRTilePlan(top: 0, height: 4_096))
        #expect(plans.last?.bottom == 20_000)
        for pair in zip(plans, plans.dropFirst()) {
            #expect(pair.0.bottom - pair.1.top == 256)
            #expect(pair.0.height <= 4_096)
            #expect(pair.1.height <= 4_096)
        }

        let ordinary = ScreenshotOCRTilePlanner.plans(imageHeight: 2_234)
        #expect(ordinary == [ScreenshotOCRTilePlan(top: 0, height: 2_234)])
    }

    @Test("Overlapping tile observations are merged without dropping repeated later text")
    func seamDeduplication() throws {
        let firstTile = fragment(
            text: "跨接缝文字",
            tileIndex: 0,
            top: 3_930,
            bottom: 3_970,
            minX: 100,
            maxX: 420,
            confidence: 0.91,
            edgeDistance: 146
        )
        let driftedSecondTile = fragment(
            text: "跨接缝文字",
            tileIndex: 1,
            top: 3_940,
            bottom: 3_982,
            minX: 104,
            maxX: 416,
            confidence: 0.96,
            edgeDistance: 121
        )
        let realRepeatedLine = fragment(
            text: "跨接缝文字",
            tileIndex: 1,
            top: 4_180,
            bottom: 4_220,
            minX: 100,
            maxX: 420,
            confidence: 0.90,
            edgeDistance: 360
        )

        let output = ScreenshotOCRTextLayout.deduplicated([
            driftedSecondTile,
            realRepeatedLine,
            firstTile
        ])

        #expect(output.count == 2)
        #expect(output.contains(firstTile))
        #expect(!output.contains(driftedSecondTile))
        #expect(output.contains(realRepeatedLine))
    }

    @Test("Fragments are returned top to bottom and left to right")
    func readingOrder() {
        let output = ScreenshotOCRTextLayout.text(from: [
            fragment(text: "Next", tileIndex: 0, top: 160, bottom: 180, minX: 80, maxX: 150),
            fragment(text: "世界", tileIndex: 0, top: 101, bottom: 122, minX: 250, maxX: 320),
            fragment(text: "Hello", tileIndex: 0, top: 100, bottom: 120, minX: 80, maxX: 180)
        ])

        #expect(output == "Hello 世界\nNext")
    }

    @Test("Cancellation reaches an installed request and is idempotent")
    func requestCancellationBox() {
        let installedRequest = RecordingOCRCancellationRequest()
        let installedBox = ScreenshotOCRRequestCancellationBox()
        installedBox.install(installedRequest)
        installedBox.cancel()
        installedBox.cancel()
        #expect(installedRequest.cancelCount == 1)

        let lateRequest = RecordingOCRCancellationRequest()
        let cancelledFirstBox = ScreenshotOCRRequestCancellationBox()
        cancelledFirstBox.cancel()
        cancelledFirstBox.install(lateRequest)
        #expect(lateRequest.cancelCount == 1)
    }

    private func fragment(
        text: String,
        tileIndex: Int,
        top: Double,
        bottom: Double,
        minX: Double,
        maxX: Double,
        confidence: Float = 0.9,
        edgeDistance: Double = 100
    ) -> ScreenshotOCRFragment {
        ScreenshotOCRFragment(
            text: text,
            tileIndex: tileIndex,
            top: top,
            bottom: bottom,
            minX: minX,
            maxX: maxX,
            confidence: confidence,
            edgeDistance: edgeDistance
        )
    }
}

private final class RecordingOCRCancellationRequest: ScreenshotOCRCancellationRequest,
    @unchecked Sendable {
    private let lock = NSLock()
    private var storedCancelCount = 0

    var cancelCount: Int {
        lock.withLock { storedCancelCount }
    }

    func cancel() {
        lock.withLock {
            storedCancelCount += 1
        }
    }
}
