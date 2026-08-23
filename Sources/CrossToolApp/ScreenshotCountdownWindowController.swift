import AppKit
import SwiftUI

enum ScreenshotCountdownError: LocalizedError {
    case cancelled

    var errorDescription: String? { "已取消延时截图" }
}

@MainActor
final class ScreenshotCountdownWindowController: NSWindowController {
    private let state = CountdownState()
    private var cancelled = false

    init(screen: NSScreen?) {
        let targetScreen = screen ?? NSScreen.main ?? NSScreen.screens.first
        let size = NSSize(width: 210, height: 180)
        let origin: NSPoint
        if let visibleFrame = targetScreen?.visibleFrame {
            origin = NSPoint(
                x: visibleFrame.midX - size.width / 2,
                y: visibleFrame.midY - size.height / 2
            )
        } else {
            origin = .zero
        }

        let panel = NSPanel(
            contentRect: NSRect(origin: origin, size: size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.level = .screenSaver
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        super.init(window: panel)
        panel.contentView = NSHostingView(rootView: ScreenshotCountdownView(
            state: state,
            onCancel: { [weak self] in self?.cancelled = true }
        ))
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func run(seconds: Int) async throws {
        cancelled = false
        state.remaining = max(1, seconds)
        showWindow(nil)
        window?.orderFrontRegardless()

        defer { window?.orderOut(nil) }
        for remaining in stride(from: max(1, seconds), through: 1, by: -1) {
            state.remaining = remaining
            try Task.checkCancellation()
            if cancelled { throw ScreenshotCountdownError.cancelled }
            try await Task.sleep(for: .seconds(1))
        }
        if cancelled { throw ScreenshotCountdownError.cancelled }

        // Make sure the non-capturable overlay has left the compositor before
        // requesting the actual display image.
        window?.orderOut(nil)
        try await Task.sleep(for: .milliseconds(140))
    }
}

@MainActor
private final class CountdownState: ObservableObject {
    @Published var remaining = 5
}

private struct ScreenshotCountdownView: View {
    @ObservedObject var state: CountdownState
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            Text("\(state.remaining)")
                .font(.system(size: 72, weight: .bold, design: .rounded))
                .contentTransition(.numericText())
            Text("准备全屏截图")
                .font(.headline)
            Button("取消", action: onCancel)
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .foregroundStyle(.white)
        .background(.black.opacity(0.78), in: RoundedRectangle(cornerRadius: 24))
        .padding(8)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("延时截图倒计时，还剩 \(state.remaining) 秒")
    }
}
