import AppKit
import CoreGraphics
import CrossToolCore
import SwiftUI

/// Mutable presentation state for a long-screenshot session overlay.
///
/// The coordinator can update these properties directly. Content changes
/// invalidate the SwiftUI fitting size so the control bar is measured and
/// placed again instead of drifting after an error adds another line.
@MainActor
final class LongScreenshotSessionOverlayModel: ObservableObject {
    @Published var frameCount: Int {
        didSet { invalidateLayout() }
    }

    @Published var outputHeight: Int {
        didSet { invalidateLayout() }
    }

    @Published var isCapturing: Bool {
        didSet { invalidateLayout() }
    }

    @Published var isWaitingForScroll: Bool {
        didSet { invalidateLayout() }
    }

    @Published var statusMessage: String {
        didSet { invalidateLayout() }
    }

    @Published var errorMessage: String? {
        didSet { invalidateLayout() }
    }

    @Published var canFinishCurrentOutput: Bool {
        didSet { invalidateLayout() }
    }

    var onCaptureNext: (() -> Void)?
    var onFinish: (() -> Void)?
    var onCancel: (() -> Void)?

    private var layoutInvalidation: (() -> Void)?

    init(
        frameCount: Int = 1,
        outputHeight: Int = 0,
        isCapturing: Bool = false,
        isWaitingForScroll: Bool = false,
        statusMessage: String = "首屏已采集。请在蓝色选区内平稳向下滚动。",
        errorMessage: String? = nil,
        canFinishCurrentOutput: Bool = false
    ) {
        self.frameCount = frameCount
        self.outputHeight = outputHeight
        self.isCapturing = isCapturing
        self.isWaitingForScroll = isWaitingForScroll
        self.statusMessage = statusMessage
        self.errorMessage = errorMessage
        self.canFinishCurrentOutput = canFinishCurrentOutput
    }

    fileprivate func setLayoutInvalidation(_ action: (() -> Void)?) {
        layoutInvalidation = action
    }

    private func invalidateLayout() {
        layoutInvalidation?()
    }
}

/// Keeps the original selected region visible while the target application
/// continues to receive every pointer and scroll event.
@MainActor
final class LongScreenshotSessionOverlayController {
    let model: LongScreenshotSessionOverlayModel

    private let selection: ScreenRegionSelection
    private var selectionWindow: NSWindow?
    private var controlPanel: NSPanel?
    private var hostingView: NSHostingView<LongScreenshotSessionControlBarView>?
    private var globalKeyMonitor: Any?
    private var localKeyMonitor: Any?
    private var relayoutTask: Task<Void, Never>?
    private var isPresented = false

    convenience init(selection: ScreenRegionSelection) {
        self.init(
            selection: selection,
            model: LongScreenshotSessionOverlayModel()
        )
    }

    init(
        selection: ScreenRegionSelection,
        model: LongScreenshotSessionOverlayModel
    ) {
        self.selection = selection
        self.model = model
        model.setLayoutInvalidation { [weak self] in
            self?.scheduleRelayout()
        }
    }

    func show() {
        guard !isPresented else {
            scheduleRelayout()
            return
        }
        isPresented = true
        installSelectionBorder()
        installControlPanel()
        installKeyMonitors()
    }

    /// Temporarily keeps the target content still while the coordinator locks
    /// the initial frame. Once unlocked, every pointer and scroll event passes
    /// through the border window to the underlying application.
    func setSelectionInteractionBlocked(_ blocked: Bool) {
        guard let selectionWindow else { return }
        selectionWindow.ignoresMouseEvents = !blocked
        if blocked, let screen = selectedScreen() {
            selectionWindow.setFrame(screen.frame, display: true)
            selectionWindow.contentView = LongScreenshotSelectionBorderView(
                frame: CGRect(origin: .zero, size: screen.frame.size),
                borderRect: selection.selectionRectInScreen.offsetBy(
                    dx: -screen.frame.minX,
                    dy: -screen.frame.minY
                )
            )
        } else {
            selectionWindow.setFrame(selection.selectionRectInScreen, display: true)
            selectionWindow.contentView = LongScreenshotSelectionBorderView(
                frame: CGRect(origin: .zero, size: selection.selectionRectInScreen.size),
                borderRect: CGRect(origin: .zero, size: selection.selectionRectInScreen.size)
            )
        }
    }

    func close() {
        guard isPresented || selectionWindow != nil || controlPanel != nil else {
            return
        }
        isPresented = false
        relayoutTask?.cancel()
        relayoutTask = nil
        removeKeyMonitors()

        selectionWindow?.orderOut(nil)
        selectionWindow?.contentView = nil
        selectionWindow?.close()
        selectionWindow = nil

        controlPanel?.orderOut(nil)
        controlPanel?.contentView = nil
        controlPanel?.close()
        controlPanel = nil
        hostingView = nil
    }

    private func installSelectionBorder() {
        let window = NSPanel(
            contentRect: selection.selectionRectInScreen,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        window.contentView = LongScreenshotSelectionBorderView(
            frame: CGRect(origin: .zero, size: selection.selectionRectInScreen.size),
            borderRect: CGRect(origin: .zero, size: selection.selectionRectInScreen.size)
        )
        configureOverlayWindow(window)
        window.hasShadow = false
        window.ignoresMouseEvents = true
        selectionWindow = window
        window.orderFrontRegardless()
    }

    private func installControlPanel() {
        let view = LongScreenshotSessionControlBarView(model: model)
        let hostingView = NSHostingView(rootView: view)
        let panel = LongScreenshotSessionControlPanel(
            contentRect: CGRect(x: 0, y: 0, width: 440, height: 92),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.contentView = hostingView
        configureOverlayWindow(panel)
        panel.hasShadow = true
        panel.ignoresMouseEvents = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isFloatingPanel = true
        panel.isMovable = false
        panel.isMovableByWindowBackground = false
        self.hostingView = hostingView
        controlPanel = panel
        relayoutControlPanel()
        panel.orderFrontRegardless()
    }

    private func configureOverlayWindow(_ window: NSWindow) {
        window.level = .screenSaver
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hidesOnDeactivate = false
        window.isReleasedWhenClosed = false
        window.tabbingMode = .disallowed
        window.collectionBehavior = [
            .canJoinAllSpaces,
            .fullScreenAuxiliary,
            .stationary,
            .ignoresCycle
        ]
        window.sharingType = .none
    }

    private func scheduleRelayout() {
        guard isPresented else { return }
        relayoutTask?.cancel()
        relayoutTask = Task { @MainActor [weak self] in
            await Task.yield()
            guard !Task.isCancelled else { return }
            self?.relayoutControlPanel()
        }
    }

    private func relayoutControlPanel() {
        guard let controlPanel, let hostingView else { return }
        hostingView.invalidateIntrinsicContentSize()
        hostingView.layoutSubtreeIfNeeded()
        let fittingSize = hostingView.fittingSize
        let requestedSize = CGSize(
            width: fittingSize.width.isFinite && fittingSize.width > 0 ? fittingSize.width : 440,
            height: fittingSize.height.isFinite && fittingSize.height > 0 ? fittingSize.height : 92
        )
        guard let screen = selectedScreen() else {
            controlPanel.setContentSize(requestedSize)
            controlPanel.center()
            return
        }

        let placement = LongScreenshotSessionOverlayPlacement.controlBar(
            size: requestedSize,
            selectionRect: selection.selectionRectInScreen,
            anchor: selection.panelAnchorInScreen,
            visibleFrame: screen.visibleFrame
        )
        controlPanel.setFrame(placement.frame, display: true)
    }

    private func selectedScreen() -> NSScreen? {
        if let matchingDisplay = NSScreen.screens.first(where: { screen in
            guard let number = screen.deviceDescription[
                NSDeviceDescriptionKey("NSScreenNumber")
            ] as? NSNumber else {
                return false
            }
            return CGDirectDisplayID(number.uint32Value) == selection.displayID
        }) {
            return matchingDisplay
        }
        return NSScreen.screens.max { first, second in
            intersectionArea(first.frame, selection.selectionRectInScreen)
                < intersectionArea(second.frame, selection.selectionRectInScreen)
        } ?? NSScreen.main
    }

    private func intersectionArea(_ first: CGRect, _ second: CGRect) -> CGFloat {
        let intersection = first.intersection(second)
        guard !intersection.isNull else { return 0 }
        return intersection.width * intersection.height
    }

    private func installKeyMonitors() {
        removeKeyMonitors()
        globalKeyMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) {
            [weak self] event in
            guard let action = Self.keyboardAction(for: event) else { return }
            Task { @MainActor [weak self] in
                self?.perform(action)
            }
        }
        localKeyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) {
            [weak self] event in
            guard let action = Self.keyboardAction(for: event) else { return event }
            Task { @MainActor [weak self] in
                self?.perform(action)
            }
            // The overlay only observes these shortcuts. It never consumes a
            // target application's event or interferes with ordinary input.
            return event
        }
    }

    private func removeKeyMonitors() {
        if let globalKeyMonitor {
            NSEvent.removeMonitor(globalKeyMonitor)
            self.globalKeyMonitor = nil
        }
        if let localKeyMonitor {
            NSEvent.removeMonitor(localKeyMonitor)
            self.localKeyMonitor = nil
        }
    }

    private enum KeyboardAction {
        case finish
        case cancel
    }

    private static func keyboardAction(for event: NSEvent) -> KeyboardAction? {
        guard !event.isARepeat else { return nil }
        if event.keyCode == 53 {
            return .cancel
        }
        let disallowedModifiers: NSEvent.ModifierFlags = [
            .command,
            .control,
            .option,
            .shift
        ]
        guard event.modifierFlags.intersection(disallowedModifiers).isEmpty,
              event.keyCode == 36 || event.keyCode == 76 else {
            return nil
        }
        return .finish
    }

    private func perform(_ action: KeyboardAction) {
        guard isPresented else { return }
        switch action {
        case .finish:
            model.onFinish?()
        case .cancel:
            model.onCancel?()
        }
    }
}

@MainActor
private final class LongScreenshotSessionControlPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
private final class LongScreenshotSelectionBorderView: NSView {
    private let borderRect: CGRect

    init(frame frameRect: NSRect, borderRect: CGRect) {
        self.borderRect = borderRect
        super.init(frame: frameRect)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        nil
    }

    override var isOpaque: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        NSColor.systemBlue.withAlphaComponent(0.96).setStroke()
        let path = NSBezierPath(rect: borderRect.insetBy(dx: 1.5, dy: 1.5))
        path.lineWidth = 3
        path.stroke()
    }
}

@MainActor
private struct LongScreenshotSessionControlBarView: View {
    @ObservedObject var model: LongScreenshotSessionOverlayModel

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "rectangle.stack.fill")
                .font(.system(size: 18, weight: .semibold))
                .foregroundStyle(Color.crossToolAccent)

            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 7) {
                    Text("长截图")
                        .font(.system(size: 13, weight: .semibold))
                    Text("\(model.frameCount) 屏 · \(model.outputHeight) px")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if model.isCapturing || model.isWaitingForScroll {
                        ProgressView()
                            .controlSize(.mini)
                    }
                }
                Text(model.errorMessage ?? model.statusMessage)
                    .font(.caption2)
                    .foregroundStyle(
                        model.errorMessage == nil ? Color.secondary : Color.orange
                    )
                    .lineLimit(model.errorMessage == nil ? 2 : 3)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 6) {
                Button("取消", systemImage: "xmark") {
                    model.onCancel?()
                }
                .labelStyle(.iconOnly)
                .help("取消长截图")

                Button("补采", systemImage: "camera.fill") {
                    model.onCaptureNext?()
                }
                .labelStyle(.iconOnly)
                .help("手动补采")
                .disabled(model.isCapturing)

                Button(model.canFinishCurrentOutput ? "保留当前并完成" : "完成") {
                    model.onFinish?()
                }
                .buttonStyle(.borderedProminent)
                .tint(.crossToolAccent)
                .help(model.canFinishCurrentOutput ? "忽略最后一帧错误并输出当前结果" : "补齐最后一帧并完成")
                .disabled(model.isCapturing)
            }
            .controlSize(.small)
        }
        .padding(.horizontal, 13)
        .padding(.vertical, 10)
        .frame(width: 440)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .overlay {
            RoundedRectangle(cornerRadius: 12)
                .stroke(Color.primary.opacity(0.12), lineWidth: 1)
        }
        .padding(2)
    }
}
