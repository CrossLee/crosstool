import CoreGraphics
import CrossToolCore
import Testing

@Test func longScreenshotCoordinateMapperHandlesTopLeftSourceCoordinates() {
    let source = ScreenRegionCoordinateMapper.screenCaptureKitSourceRect(
        appKitLocalRect: CGRect(x: 120, y: 80, width: 640, height: 360),
        screenSize: CGSize(width: 1_440, height: 900)
    )

    #expect(source == CGRect(x: 120, y: 460, width: 640, height: 360))
}

@Test func longScreenshotCoordinateMapperPreservesNegativeDisplayOrigins() {
    let global = ScreenRegionCoordinateMapper.appKitGlobalRect(
        appKitLocalRect: CGRect(x: 100, y: 150, width: 800, height: 500),
        screenFrame: CGRect(x: -2_560, y: -323, width: 2_560, height: 1_440)
    )

    #expect(global == CGRect(x: -2_460, y: -173, width: 800, height: 500))
}

@Test func longScreenshotCoordinateMapperAddsSecondaryDisplayOriginToAnchorOnce() {
    let global = ScreenRegionCoordinateMapper.appKitGlobalPoint(
        appKitLocalPoint: CGPoint(x: 881.25, y: 633.75),
        screenFrame: CGRect(x: 1_728, y: -323, width: 2_560, height: 1_440)
    )

    #expect(global == CGPoint(x: 2_609.25, y: 310.75))
}
