import AppKit
import CoreGraphics
import CrossToolCore

struct ScreenRegionSelection: Sendable, Equatable {
    let displayID: CGDirectDisplayID

    /// The selected rectangle in the display's logical point coordinate space.
    /// Its origin is at the display's top-left, matching
    /// `SCStreamConfiguration.sourceRect` for a display filter.
    let sourceRect: CGRect

    /// The selection in AppKit's global bottom-left screen coordinate space.
    /// This is used only for interaction and panel placement, never for SCK.
    let selectionRectInScreen: CGRect

    /// The pointer position at mouse-up, also in global AppKit coordinates.
    let panelAnchorInScreen: CGPoint
}

enum ScreenRegionSelectionPurpose: Sendable, Equatable {
    case longScreenshot
    case screenRecording

    fileprivate var instruction: String {
        switch self {
        case .longScreenshot:
            return "只框选可滚动内容（不要包含标题栏或固定区域） · Esc 或右键取消"
        case .screenRecording:
            return "拖拽选择录屏区域  ·  Esc 或右键取消"
        }
    }
}

enum ScreenRegionSelectionError: LocalizedError, Equatable {
    case alreadyPresented
    case cancelled
    case displayUnavailable

    var errorDescription: String? {
        switch self {
        case .alreadyPresented:
            return "已有一个区域选择器正在等待操作"
        case .cancelled:
            return "已取消选择区域"
        case .displayUnavailable:
            return "找不到可用的显示器"
        }
    }
}

@MainActor
final class ScreenRegionSelectionCoordinator: NSObject, NSWindowDelegate {
    static let shared = ScreenRegionSelectionCoordinator()

    private var continuation: CheckedContinuation<ScreenRegionSelection, Error>?
    private var selectionWindows = [ScreenRegionSelectionWindow]()

    private override init() {
        super.init()
    }

    /// Shows a selection overlay on every display. The drag is clamped to the
    /// display where it starts; Escape and right-click cancel the operation.
    func selectRegion(
        purpose: ScreenRegionSelectionPurpose = .longScreenshot
    ) async throws -> ScreenRegionSelection {
        guard continuation == nil else {
            throw ScreenRegionSelectionError.alreadyPresented
        }
        try Task.checkCancellation()

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                beginSelection(purpose: purpose, continuation: continuation)
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.finish(with: .failure(CancellationError()))
            }
        }
    }

    private func beginSelection(
        purpose: ScreenRegionSelectionPurpose,
        continuation: CheckedContinuation<ScreenRegionSelection, Error>
    ) {
        let screens = NSScreen.screens
        guard !screens.isEmpty else {
            continuation.resume(throwing: ScreenRegionSelectionError.displayUnavailable)
            return
        }

        self.continuation = continuation
        NSApp.activate(ignoringOtherApps: true)

        let mouseScreen = Self.screenUnderMouse()
        for screen in screens {
            let overlayView = ScreenRegionSelectionView(frame: NSRect(
                origin: .zero,
                size: screen.frame.size
            ))
            overlayView.instruction = purpose.instruction
            overlayView.onSelection = { [weak self] selectionRect, anchorInView in
                self?.completeSelection(
                    selectionRect,
                    anchorInView: anchorInView,
                    on: screen
                )
            }
            overlayView.onCancel = { [weak self] in
                self?.finish(with: .failure(ScreenRegionSelectionError.cancelled))
            }

            let window = ScreenRegionSelectionWindow(
                contentRect: screen.frame,
                styleMask: [.borderless],
                backing: .buffered,
                defer: false,
                screen: screen
            )
            window.backgroundColor = .clear
            window.isOpaque = false
            window.hasShadow = false
            window.level = .screenSaver
            window.collectionBehavior = [
                .canJoinAllSpaces,
                .fullScreenAuxiliary,
                .stationary,
                .ignoresCycle
            ]
            window.isReleasedWhenClosed = false
            window.tabbingMode = .disallowed
            // The overlay is closed before any frame is captured, and the SCK
            // filter already excludes Crosio's own windows. Keeping it
            // shareable also lets macOS accessibility/automation tools see the
            // selector instead of treating the screen as windowless.
            window.sharingType = .readOnly
            window.delegate = self
            window.contentView = overlayView
            window.onCancel = { [weak self] in
                self?.finish(with: .failure(ScreenRegionSelectionError.cancelled))
            }
            selectionWindows.append(window)

            if screen === mouseScreen {
                window.makeKeyAndOrderFront(nil)
                window.makeFirstResponder(overlayView)
            } else {
                window.orderFrontRegardless()
            }
        }
    }

    private func completeSelection(
        _ appKitRect: CGRect,
        anchorInView: CGPoint,
        on screen: NSScreen
    ) {
        guard let displayID = Self.displayID(for: screen) else {
            finish(with: .failure(ScreenRegionSelectionError.displayUnavailable))
            return
        }

        let sourceRect = ScreenRegionCoordinateMapper.screenCaptureKitSourceRect(
            appKitLocalRect: appKitRect,
            screenSize: screen.frame.size
        )
        let selectionRectInScreen = ScreenRegionCoordinateMapper.appKitGlobalRect(
            appKitLocalRect: appKitRect,
            screenFrame: screen.frame
        )
        let anchorInScreen = ScreenRegionCoordinateMapper.appKitGlobalPoint(
            appKitLocalPoint: anchorInView,
            screenFrame: screen.frame
        )
        finish(with: .success(ScreenRegionSelection(
            displayID: displayID,
            sourceRect: sourceRect,
            selectionRectInScreen: selectionRectInScreen,
            panelAnchorInScreen: anchorInScreen
        )))
    }

    private func finish(with result: sending Result<ScreenRegionSelection, Error>) {
        guard let continuation else { return }
        self.continuation = nil

        for window in selectionWindows {
            window.delegate = nil
            window.onCancel = nil
            window.orderOut(nil)
            window.contentView = nil
            window.close()
        }
        selectionWindows.removeAll()

        continuation.resume(with: result)
    }

    func windowWillClose(_ notification: Notification) {
        finish(with: .failure(ScreenRegionSelectionError.cancelled))
    }

    private static func screenUnderMouse() -> NSScreen? {
        let mouseLocation = NSEvent.mouseLocation
        return NSScreen.screens.first(where: { screen in
            NSMouseInRect(mouseLocation, screen.frame, false)
        }) ?? NSScreen.main
    }

    private static func displayID(for screen: NSScreen) -> CGDirectDisplayID? {
        guard let screenNumber = screen.deviceDescription[
            NSDeviceDescriptionKey("NSScreenNumber")
        ] as? NSNumber else {
            return nil
        }
        return CGDirectDisplayID(screenNumber.uint32Value)
    }

}

@MainActor
private final class ScreenRegionSelectionWindow: NSWindow {
    var onCancel: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }
}

@MainActor
private final class ScreenRegionSelectionView: NSView {
    var onSelection: ((CGRect, CGPoint) -> Void)?
    var onCancel: (() -> Void)?
    var instruction = ScreenRegionSelectionPurpose.longScreenshot.instruction

    private var dragAnchor: CGPoint?
    private var selectionRect: CGRect?
    private let minimumSelectionLength: CGFloat = 4

    override var acceptsFirstResponder: Bool { true }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }

    override func mouseDown(with event: NSEvent) {
        let point = clampedPoint(convert(event.locationInWindow, from: nil))
        dragAnchor = point
        selectionRect = CGRect(origin: point, size: .zero)
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragAnchor else { return }
        let point = clampedPoint(convert(event.locationInWindow, from: nil))
        selectionRect = standardizedRect(from: dragAnchor, to: point)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard let dragAnchor else { return }
        let point = clampedPoint(convert(event.locationInWindow, from: nil))
        let finalRect = standardizedRect(from: dragAnchor, to: point)
        self.dragAnchor = nil

        guard finalRect.width >= minimumSelectionLength,
              finalRect.height >= minimumSelectionLength else {
            selectionRect = nil
            needsDisplay = true
            return
        }

        selectionRect = finalRect
        // `event.locationInWindow` is not reliably window-local for these
        // full-screen borderless overlays on a secondary display. Reuse the
        // already-normalized view-local point and let the coordinator add the
        // selected screen's global origin exactly once.
        onSelection?(finalRect, point)
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 || event.charactersIgnoringModifiers == "\u{1B}" {
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        NSColor.black.withAlphaComponent(0.42).setFill()
        bounds.fill()

        if let selectionRect {
            guard let context = NSGraphicsContext.current?.cgContext else { return }
            context.saveGState()
            context.setBlendMode(.clear)
            context.fill(selectionRect)
            context.restoreGState()

            NSColor.white.withAlphaComponent(0.98).setStroke()
            let border = NSBezierPath(rect: selectionRect.insetBy(dx: 0.75, dy: 0.75))
            border.lineWidth = 1.5
            border.stroke()

            drawSizeLabel(for: selectionRect)
        } else {
            drawInstruction()
        }
    }

    private func drawInstruction() {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 15, weight: .medium),
            .foregroundColor: NSColor.white,
            .backgroundColor: NSColor.black.withAlphaComponent(0.58)
        ]
        let attributedText = NSAttributedString(
            string: instruction,
            attributes: attributes
        )
        let textSize = attributedText.size()
        let origin = CGPoint(
            x: bounds.midX - textSize.width / 2,
            y: bounds.maxY - textSize.height - 32
        )
        attributedText.draw(at: origin)
    }

    private func drawSizeLabel(for rect: CGRect) {
        let text = "\(Int(rect.width.rounded())) × \(Int(rect.height.rounded())) pt"
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium),
            .foregroundColor: NSColor.white,
            .backgroundColor: NSColor.black.withAlphaComponent(0.68)
        ]
        let attributedText = NSAttributedString(string: text, attributes: attributes)
        let textSize = attributedText.size()
        let preferredY = rect.minY - textSize.height - 8
        let origin = CGPoint(
            x: min(max(rect.minX, 8), bounds.maxX - textSize.width - 8),
            y: preferredY >= 8 ? preferredY : min(rect.maxY + 8, bounds.maxY - textSize.height - 8)
        )
        attributedText.draw(at: origin)
    }

    private func clampedPoint(_ point: CGPoint) -> CGPoint {
        CGPoint(
            x: min(max(point.x, bounds.minX), bounds.maxX),
            y: min(max(point.y, bounds.minY), bounds.maxY)
        )
    }

    private func standardizedRect(from first: CGPoint, to second: CGPoint) -> CGRect {
        CGRect(
            x: min(first.x, second.x),
            y: min(first.y, second.y),
            width: abs(second.x - first.x),
            height: abs(second.y - first.y)
        ).intersection(bounds)
    }
}
