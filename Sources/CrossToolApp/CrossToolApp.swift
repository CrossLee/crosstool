import AppKit
import SwiftUI

@main
struct CrossToolApp: App {
    @NSApplicationDelegateAdaptor(OnePawApplicationDelegate.self)
    private var appDelegate
    @StateObject private var model = AppModel()
    @StateObject private var mainWindowPresenter = MainWindowPresenter()

    var body: some Scene {
        MenuBarExtra {
            MenuBarPanelView()
                .environmentObject(model)
        } label: {
            MenuBarStatusLabel(
                model: model,
                mainWindowPresenter: mainWindowPresenter
            )
        }
        .menuBarExtraStyle(.window)
    }
}

private struct MenuBarStatusLabel: View {
    @ObservedObject var model: AppModel
    let mainWindowPresenter: MainWindowPresenter

    var body: some View {
        Label(
            model.screenRecording.isRecording ? "\(ApplicationBrand.displayName)正在录屏" : ApplicationBrand.displayName,
            systemImage: model.screenRecording.isRecording
                ? "record.circle.fill"
                : (model.isServerRunning ? "square.and.arrow.up.fill" : "square.and.arrow.up")
        )
        .onAppear {
            openMainWindowIfNeeded(for: model.mainWindowOpenRequestID)
        }
        .onChange(of: model.mainWindowOpenRequestID) { _, requestID in
            openMainWindowIfNeeded(for: requestID)
        }
        .onChange(of: model.mainWindowDismissRequestID) { _, requestID in
            guard requestID > 0 else { return }
            mainWindowPresenter.dismiss()
        }
    }

    private func openMainWindowIfNeeded(for requestID: Int) {
        guard model.claimMainWindowOpenRequest(requestID) else { return }
        mainWindowPresenter.present(model: model)
    }
}

@MainActor
final class MainWindowPresenter: ObservableObject {
    private var windowController: NSWindowController?

    func present(model: AppModel) {
        let controller: NSWindowController
        if let windowController {
            controller = windowController
        } else {
            controller = makeWindowController(model: model)
            windowController = controller
        }

        NSApp.activate(ignoringOtherApps: true)
        controller.window?.deminiaturize(nil)
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
    }

    func dismiss() {
        windowController?.close()
    }

    private func makeWindowController(model: AppModel) -> NSWindowController {
        let rootView = MainWindowView()
            .environmentObject(model)
            .onReceive(
                NotificationCenter.default.publisher(
                    for: NSApplication.didBecomeActiveNotification
                )
            ) { _ in
                model.refreshScreenCapturePermission()
            }
        let hostingController = NSHostingController(rootView: rootView)
        let window = NSWindow(contentViewController: hostingController)
        window.title = ApplicationBrand.displayName
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.setContentSize(NSSize(width: 1_180, height: 780))
        window.contentMinSize = NSSize(width: 1_000, height: 620)
        window.isReleasedWhenClosed = false
        window.isRestorable = false
        window.tabbingMode = .disallowed
        window.center()
        return NSWindowController(window: window)
    }
}
