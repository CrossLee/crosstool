import CoreGraphics
import CrossToolCore
import Testing

@Suite("Long screenshot session overlay placement")
struct LongScreenshotSessionOverlayPlacementTests {
    private let screen = CGRect(x: 1_728, y: -323, width: 2_560, height: 1_407)
    private let barSize = CGSize(width: 440, height: 92)

    @Test("prefers below the selection")
    func prefersBelow() {
        let selection = CGRect(x: 2_100, y: 300, width: 1_000, height: 650)

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            visibleFrame: screen
        )

        #expect(placement.side == .below)
        #expect(placement.avoidsSelection)
        #expect(screen.contains(placement.frame))
        #expect(placement.frame.maxY < selection.minY)
    }

    @Test("uses above when the lower strip is too short")
    func usesAbove() {
        let selection = CGRect(x: 2_100, y: -300, width: 1_000, height: 700)

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            visibleFrame: screen
        )

        #expect(placement.side == .above)
        #expect(placement.avoidsSelection)
        #expect(screen.contains(placement.frame))
        #expect(placement.frame.minY > selection.maxY)
    }

    @Test("uses a side when neither vertical strip fits")
    func usesSide() {
        let selection = CGRect(x: 1_900, y: -260, width: 1_250, height: 1_260)

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            visibleFrame: screen
        )

        #expect(placement.side == .right)
        #expect(placement.avoidsSelection)
        #expect(screen.contains(placement.frame))
        #expect(placement.frame.minX > selection.maxX)
    }

    @Test("remeasures height without changing placement policy")
    func handlesDynamicHeight() {
        let selection = CGRect(x: 2_100, y: 360, width: 1_000, height: 600)
        let compact = LongScreenshotSessionOverlayPlacement.controlBar(
            size: CGSize(width: 440, height: 76),
            selectionRect: selection,
            visibleFrame: screen
        )
        let expanded = LongScreenshotSessionOverlayPlacement.controlBar(
            size: CGSize(width: 440, height: 132),
            selectionRect: selection,
            visibleFrame: screen
        )

        #expect(compact.side == .below)
        #expect(expanded.side == .below)
        #expect(compact.frame.maxY == expanded.frame.maxY)
        #expect(expanded.frame.minY < compact.frame.minY)
        #expect(!compact.frame.intersects(selection))
        #expect(!expanded.frame.intersects(selection))
    }

    @Test("stays on the selected display in the unavoidable fallback")
    func fallsBackOnScreen() {
        let selection = screen

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            visibleFrame: screen
        )

        #expect(placement.side == .fallback)
        #expect(!placement.avoidsSelection)
        #expect(screen.contains(placement.frame))
    }

    @Test("keeps unavoidable fallback near a reverse-drag anchor")
    func fallbackTracksReverseDrag() {
        let anchor = CGPoint(x: screen.minX + 40, y: screen.maxY - 40)
        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: screen,
            anchor: anchor,
            visibleFrame: screen
        )

        #expect(placement.side == .fallback)
        #expect(screen.contains(placement.frame))
        #expect(placement.frame.maxY >= anchor.y - 8)
    }

    @Test("keeps the bar next to the pointer release")
    func tracksPointerRelease() {
        let selection = CGRect(x: 2_100, y: 300, width: 1_000, height: 650)
        let anchor = CGPoint(x: selection.maxX, y: selection.minY)

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            anchor: anchor,
            visibleFrame: screen
        )

        #expect(placement.side == .below)
        #expect(placement.frame.maxX == anchor.x)
        #expect(anchor.y - placement.frame.maxY == 8)
        #expect(placement.avoidsSelection)
    }

    @Test("uses the nearest side for a reverse drag")
    func tracksReverseDragSide() {
        let selection = CGRect(x: 2_100, y: 100, width: 1_000, height: 650)
        let anchor = CGPoint(x: selection.minX, y: selection.maxY)

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            anchor: anchor,
            visibleFrame: screen
        )

        #expect(placement.side == .above || placement.side == .left)
        #expect(placement.avoidsSelection)
        #expect(screen.contains(placement.frame))
    }

    @Test("clamps a pointer anchor at a negative screen edge")
    func clampsNegativeScreenAnchor() {
        let selection = CGRect(x: screen.minX + 40, y: 100, width: 700, height: 500)
        let anchor = CGPoint(x: selection.minX, y: selection.maxY)

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: barSize,
            selectionRect: selection,
            anchor: anchor,
            visibleFrame: screen
        )

        #expect(screen.contains(placement.frame))
        #expect(placement.frame.minX >= screen.minX + 8)
    }
}
