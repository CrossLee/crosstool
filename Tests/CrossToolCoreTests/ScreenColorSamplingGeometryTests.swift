import CrossToolCore
import Testing

@Suite("Screen color sampling geometry")
struct ScreenColorSamplingGeometryTests {
    @Test("maps AppKit bottom-left coordinates to native top-left pixels")
    func coordinateMapping() {
        let geometry = ScreenColorSamplingGeometry(
            minimumX: 0,
            minimumY: 0,
            widthInPoints: 100,
            heightInPoints: 50,
            pointPixelScale: 2,
            pixelWidth: 200,
            pixelHeight: 100
        )

        #expect(geometry.pixelCoordinate(globalX: 10.25, globalY: 40) == .init(x: 20, y: 20))
        #expect(geometry.pixelCoordinate(globalX: 0, globalY: 49.75) == .init(x: 0, y: 0))
        #expect(geometry.pixelCoordinate(globalX: 99.75, globalY: 0) == .init(x: 199, y: 99))
    }

    @Test("supports displays with negative global origins")
    func negativeDisplayOrigin() {
        let geometry = ScreenColorSamplingGeometry(
            minimumX: -1440,
            minimumY: -200,
            widthInPoints: 1440,
            heightInPoints: 900,
            pointPixelScale: 2,
            pixelWidth: 2880,
            pixelHeight: 1800
        )

        #expect(
            geometry.pixelCoordinate(globalX: -720, globalY: 250)
                == .init(x: 1440, y: 900)
        )
        #expect(geometry.pixelCoordinate(globalX: 1, globalY: 250) == nil)
        #expect(geometry.pixelCoordinate(globalX: -1441, globalY: 250) == nil)
    }

    @Test("clips magnifier patches at display edges and preserves the center")
    func clippedPatch() {
        let geometry = ScreenColorSamplingGeometry(
            minimumX: 0,
            minimumY: 0,
            widthInPoints: 10,
            heightInPoints: 10,
            pointPixelScale: 1,
            pixelWidth: 10,
            pixelHeight: 10
        )

        #expect(
            geometry.patch(centeredAt: .init(x: 5, y: 5), radius: 2)
                == .init(originX: 3, originY: 3, width: 5, height: 5, centerX: 2, centerY: 2)
        )
        #expect(
            geometry.patch(centeredAt: .init(x: 0, y: 0), radius: 2)
                == .init(originX: 0, originY: 0, width: 3, height: 3, centerX: 0, centerY: 0)
        )
        #expect(
            geometry.patch(centeredAt: .init(x: 9, y: 9), radius: 2)
                == .init(originX: 7, originY: 7, width: 3, height: 3, centerX: 2, centerY: 2)
        )
    }

    @Test("rejects invalid geometry and coordinates outside the display")
    func invalidInputs() {
        let geometry = ScreenColorSamplingGeometry(
            minimumX: 0,
            minimumY: 0,
            widthInPoints: 100,
            heightInPoints: 50,
            pointPixelScale: 2,
            pixelWidth: 200,
            pixelHeight: 100
        )

        #expect(geometry.pixelCoordinate(globalX: 100, globalY: 25) == nil)
        #expect(geometry.pixelCoordinate(globalX: 50, globalY: 50) == nil)
        #expect(geometry.pixelCoordinate(globalX: .nan, globalY: 25) == nil)
        #expect(geometry.patch(centeredAt: .init(x: -1, y: 0), radius: 2) == nil)
        #expect(geometry.patch(centeredAt: .init(x: 0, y: 0), radius: -1) == nil)
    }
}
