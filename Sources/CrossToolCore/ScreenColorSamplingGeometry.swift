import Foundation

/// A pixel coordinate in a captured display frame. The origin is the top-left,
/// matching ScreenCaptureKit and CVPixelBuffer row order for BGRA frames.
public struct ScreenPixelCoordinate: Equatable, Sendable {
    public let x: Int
    public let y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }
}

/// The clipped pixel rectangle used by the cursor magnifier. `centerX` and
/// `centerY` are relative to the returned patch, so edge samples still mark the
/// correct selected pixel.
public struct ScreenPixelPatch: Equatable, Sendable {
    public let originX: Int
    public let originY: Int
    public let width: Int
    public let height: Int
    public let centerX: Int
    public let centerY: Int

    public init(
        originX: Int,
        originY: Int,
        width: Int,
        height: Int,
        centerX: Int,
        centerY: Int
    ) {
        self.originX = originX
        self.originY = originY
        self.width = width
        self.height = height
        self.centerX = centerX
        self.centerY = centerY
    }
}

/// Converts AppKit's global, bottom-left-origin mouse coordinates into the
/// top-left-origin pixels delivered by a native-resolution ScreenCaptureKit
/// display stream. Screen origins may be negative in a multi-display layout.
public struct ScreenColorSamplingGeometry: Equatable, Sendable {
    public let minimumX: Double
    public let minimumY: Double
    public let widthInPoints: Double
    public let heightInPoints: Double
    public let pointPixelScale: Double
    public let pixelWidth: Int
    public let pixelHeight: Int

    public init(
        minimumX: Double,
        minimumY: Double,
        widthInPoints: Double,
        heightInPoints: Double,
        pointPixelScale: Double,
        pixelWidth: Int,
        pixelHeight: Int
    ) {
        self.minimumX = minimumX
        self.minimumY = minimumY
        self.widthInPoints = widthInPoints
        self.heightInPoints = heightInPoints
        self.pointPixelScale = pointPixelScale
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
    }

    public func pixelCoordinate(globalX: Double, globalY: Double) -> ScreenPixelCoordinate? {
        guard minimumX.isFinite,
              minimumY.isFinite,
              widthInPoints.isFinite,
              heightInPoints.isFinite,
              pointPixelScale.isFinite,
              globalX.isFinite,
              globalY.isFinite,
              widthInPoints > 0,
              heightInPoints > 0,
              pointPixelScale > 0,
              pixelWidth > 0,
              pixelHeight > 0,
              globalX >= minimumX,
              globalX < minimumX + widthInPoints,
              globalY >= minimumY,
              globalY < minimumY + heightInPoints else {
            return nil
        }

        let localX = globalX - minimumX
        let localYFromTop = (minimumY + heightInPoints) - globalY
        let pixelX = min(
            max(Int((localX * pointPixelScale).rounded(.down)), 0),
            pixelWidth - 1
        )
        let pixelY = min(
            max(Int((localYFromTop * pointPixelScale).rounded(.down)), 0),
            pixelHeight - 1
        )
        return ScreenPixelCoordinate(x: pixelX, y: pixelY)
    }

    public func patch(
        centeredAt coordinate: ScreenPixelCoordinate,
        radius: Int
    ) -> ScreenPixelPatch? {
        guard radius >= 0,
              coordinate.x >= 0,
              coordinate.x < pixelWidth,
              coordinate.y >= 0,
              coordinate.y < pixelHeight else {
            return nil
        }

        let minimumPatchX = max(0, coordinate.x - radius)
        let minimumPatchY = max(0, coordinate.y - radius)
        let maximumPatchX = min(pixelWidth - 1, coordinate.x + radius)
        let maximumPatchY = min(pixelHeight - 1, coordinate.y + radius)
        return ScreenPixelPatch(
            originX: minimumPatchX,
            originY: minimumPatchY,
            width: maximumPatchX - minimumPatchX + 1,
            height: maximumPatchY - minimumPatchY + 1,
            centerX: coordinate.x - minimumPatchX,
            centerY: coordinate.y - minimumPatchY
        )
    }
}
