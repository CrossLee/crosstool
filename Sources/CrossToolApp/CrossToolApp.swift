import AppKit
import SwiftUI

@main
struct CrossToolApp: App {
    @NSApplicationDelegateAdaptor(CrosioApplicationDelegate.self)
    private var appDelegate
    @StateObject private var model = AppModel()

    var body: some Scene {
        Window("Crosio", id: "main") {
            MainWindowView()
                .environmentObject(model)
                .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
                    model.refreshScreenCapturePermission()
                }
        }
        .defaultSize(width: 1_180, height: 780)
        .windowResizability(.contentMinSize)

        MenuBarExtra {
            MenuBarPanelView()
                .environmentObject(model)
        } label: {
            MenuBarStatusLabel(
                model: model
            )
        }
        .menuBarExtraStyle(.window)
    }
}

private struct MenuBarStatusLabel: View {
    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismissWindow) private var dismissWindow
    @ObservedObject var model: AppModel

    var body: some View {
        Label(
            model.screenRecording.isRecording ? "Crosio 正在录屏" : "Crosio",
            systemImage: model.screenRecording.isRecording
                ? "record.circle.fill"
                : (model.isServerRunning ? "square.and.arrow.up.fill" : "square.and.arrow.up")
        )
        .onChange(of: model.mainWindowOpenRequestID) { _, requestID in
            guard requestID > 0 else { return }
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        .onChange(of: model.mainWindowDismissRequestID) { _, requestID in
            guard requestID > 0 else { return }
            dismissWindow(id: "main")
        }
    }
}
