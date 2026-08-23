import CoreGraphics

/// Coordinate conversion shared by the long-screenshot region selector.
///
/// AppKit uses a bottom-left origin for a screen-local rectangle, while
/// ScreenCaptureKit expects a display-local rectangle with a top-left origin.
public enum ScreenRegionCoordinateMapper {
    public static func screenCaptureKitSourceRect(
        appKitLocalRect: CGRect,
        screenSize: CGSize
    ) -> CGRect {
        let screenBounds = CGRect(origin: .zero, size: screenSize)
        let rect = appKitLocalRect.standardized.intersection(screenBounds)
        guard !rect.isNull else { return .zero }
        return CGRect(
            x: rect.minX,
            y: screenSize.height - rect.maxY,
            width: rect.width,
            height: rect.height
        )
    }

    /// Converts a screen-local AppKit rectangle into the global AppKit screen
    /// coordinate space. Negative secondary-display origins are preserved.
    public static func appKitGlobalRect(
        appKitLocalRect: CGRect,
        screenFrame: CGRect
    ) -> CGRect {
        let localBounds = CGRect(origin: .zero, size: screenFrame.size)
        let rect = appKitLocalRect.standardized.intersection(localBounds)
        guard !rect.isNull else { return .zero }
        return CGRect(
            x: screenFrame.minX + rect.minX,
            y: screenFrame.minY + rect.minY,
            width: rect.width,
            height: rect.height
        )
    }

    /// Converts a point measured in the selection view into AppKit's global
    /// bottom-left screen coordinate space.
    public static func appKitGlobalPoint(
        appKitLocalPoint: CGPoint,
        screenFrame: CGRect
    ) -> CGPoint {
        CGPoint(
            x: screenFrame.minX + appKitLocalPoint.x,
            y: screenFrame.minY + appKitLocalPoint.y
        )
    }
}
