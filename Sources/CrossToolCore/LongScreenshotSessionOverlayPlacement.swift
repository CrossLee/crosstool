import CoreGraphics

/// A side of the selected scrolling region used to place the session controls.
public enum LongScreenshotControlBarSide: Sendable, Equatable {
    case below
    case above
    case right
    case left
    case fallback
}

/// The result of placing the long-screenshot controls on the selected display.
public struct LongScreenshotControlBarPlacement: Sendable, Equatable {
    public let frame: CGRect
    public let side: LongScreenshotControlBarSide
    public let avoidsSelection: Bool

    public init(
        frame: CGRect,
        side: LongScreenshotControlBarSide,
        avoidsSelection: Bool
    ) {
        self.frame = frame
        self.side = side
        self.avoidsSelection = avoidsSelection
    }
}

/// Pure placement policy for the non-activating long-screenshot control bar.
///
/// The controls prefer the area immediately below the capture region, then
/// above it, then to its right or left. Every candidate is clamped to the
/// selected screen's visible frame. When no side can geometrically contain the
/// complete bar (for example, a full-screen selection), the fallback remains
/// on that display and reports that overlap was unavoidable.
public enum LongScreenshotSessionOverlayPlacement {
    public static func controlBar(
        size requestedSize: CGSize,
        selectionRect: CGRect,
        anchor: CGPoint? = nil,
        visibleFrame: CGRect,
        margin: CGFloat = 8,
        gap: CGFloat = 8
    ) -> LongScreenshotControlBarPlacement {
        guard requestedSize.width.isFinite,
              requestedSize.height.isFinite,
              requestedSize.width > 0,
              requestedSize.height > 0,
              visibleFrame.width.isFinite,
              visibleFrame.height.isFinite,
              visibleFrame.width > 0,
              visibleFrame.height > 0 else {
            return LongScreenshotControlBarPlacement(
                frame: CGRect(origin: visibleFrame.origin, size: .zero),
                side: .fallback,
                avoidsSelection: selectionRect.isEmpty
            )
        }

        let safeFrame = insetFrame(visibleFrame, by: max(0, margin))
        let size = CGSize(
            width: min(requestedSize.width, safeFrame.width),
            height: min(requestedSize.height, safeFrame.height)
        )
        let selection = selectionRect.standardized
        let anchor = anchor ?? CGPoint(x: selection.midX, y: selection.midY)
        let horizontalOrigin = clampedOrigin(
            anchor.x - size.width,
            minimum: safeFrame.minX,
            maximum: safeFrame.maxX - size.width
        )
        let verticalOrigin = clampedOrigin(
            anchor.y - size.height,
            minimum: safeFrame.minY,
            maximum: safeFrame.maxY - size.height
        )

        let below = CGRect(
            x: horizontalOrigin,
            y: selection.minY - gap - size.height,
            width: size.width,
            height: size.height
        )
        let above = CGRect(
            x: horizontalOrigin,
            y: selection.maxY + gap,
            width: size.width,
            height: size.height
        )
        let right = CGRect(
            x: selection.maxX + gap,
            y: verticalOrigin,
            width: size.width,
            height: size.height
        )
        let left = CGRect(
            x: selection.minX - gap - size.width,
            y: verticalOrigin,
            width: size.width,
            height: size.height
        )
        let candidates: [(CGRect, LongScreenshotControlBarSide, Int)] = [
            (below, .below, 0),
            (above, .above, 1),
            (right, .right, 2),
            (left, .left, 3)
        ].filter { safeFrame.contains($0.0) }
        if let nearest = candidates.min(by: { first, second in
            let firstDistance = squaredDistance(from: anchor, to: first.0)
            let secondDistance = squaredDistance(from: anchor, to: second.0)
            if firstDistance == secondDistance {
                return first.2 < second.2
            }
            return firstDistance < secondDistance
        }) {
            return placement(nearest.0, side: nearest.1, selection: selection)
        }

        let fallbackOrigin = CGPoint(
            x: clampedOrigin(
                anchor.x - size.width,
                minimum: safeFrame.minX,
                maximum: safeFrame.maxX - size.width
            ),
            y: verticalOrigin
        )
        let fallback = CGRect(origin: fallbackOrigin, size: size)
        return placement(fallback, side: .fallback, selection: selection)
    }

    private static func placement(
        _ frame: CGRect,
        side: LongScreenshotControlBarSide,
        selection: CGRect
    ) -> LongScreenshotControlBarPlacement {
        LongScreenshotControlBarPlacement(
            frame: frame,
            side: side,
            avoidsSelection: !frame.intersects(selection)
        )
    }

    private static func insetFrame(_ frame: CGRect, by inset: CGFloat) -> CGRect {
        guard frame.width > inset * 2, frame.height > inset * 2 else {
            return frame
        }
        return frame.insetBy(dx: inset, dy: inset)
    }

    private static func clampedOrigin(
        _ value: CGFloat,
        minimum: CGFloat,
        maximum: CGFloat
    ) -> CGFloat {
        min(max(value, minimum), max(minimum, maximum))
    }

    private static func squaredDistance(from point: CGPoint, to rect: CGRect) -> CGFloat {
        let nearestX = min(max(point.x, rect.minX), rect.maxX)
        let nearestY = min(max(point.y, rect.minY), rect.maxY)
        let deltaX = point.x - nearestX
        let deltaY = point.y - nearestY
        return deltaX * deltaX + deltaY * deltaY
    }
}
