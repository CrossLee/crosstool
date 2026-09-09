import AppKit

enum ApplicationLaunchPolicy {
    static func shouldPresentMainWindow(
        isDefaultLaunch: Bool?
    ) -> Bool {
        // AppKit guarantees this launch flag. If an unusual launch environment
        // omits or corrupts it, stay background-only instead of surprising the
        // user with a window. A later explicit reopen still shows the app.
        isDefaultLaunch == true
    }
}

@MainActor
final class MainWindowOpenRequestBroker {
    typealias Handler = () -> Void

    private var handler: Handler?
    private var hasPendingRequest = false

    func receive() {
        if let handler {
            handler()
        } else {
            // Showing the same main window is idempotent, so coalesce repeated
            // cold-launch requests while the SwiftUI model is being created.
            hasPendingRequest = true
        }
    }

    func install(_ handler: @escaping Handler) {
        self.handler = handler
        guard hasPendingRequest else { return }

        hasPendingRequest = false
        handler()
    }
}

@MainActor
final class ImageOpenRequestBroker {
    typealias Handler = ([URL]) -> Void

    private var handler: Handler?
    private var pendingURLs: [URL] = []

    func receive(_ urls: [URL]) {
        let fileURLs = urls.filter(\.isFileURL)
        guard !fileURLs.isEmpty else { return }

        if let handler {
            handler(fileURLs)
        } else {
            pendingURLs.append(contentsOf: fileURLs)
        }
    }

    func install(_ handler: @escaping Handler) {
        self.handler = handler
        guard !pendingURLs.isEmpty else { return }

        let urls = pendingURLs
        pendingURLs.removeAll()
        handler(urls)
    }
}

@MainActor
final class CrosioApplicationDelegate: NSObject, NSApplicationDelegate {
    static weak var recordingModel: ScreenRecordingFeatureModel?
    static let mainWindowOpenRequestBroker = MainWindowOpenRequestBroker()
    static let imageOpenRequestBroker = ImageOpenRequestBroker()

    private var pendingTerminationTask: Task<Void, Never>?
    private let copyPathServiceProvider = CopyPathServiceProvider()

    func applicationDidFinishLaunching(_ notification: Notification) {
        let isDefaultLaunch = (
            notification.userInfo?[NSApplication.launchIsDefaultUserInfoKey]
                as? NSNumber
        )?.boolValue
        let shouldPresentMainWindow = ApplicationLaunchPolicy.shouldPresentMainWindow(
            isDefaultLaunch: isDefaultLaunch
        )

        // SMAppService is the single source of truth for login launches. Disable
        // AppKit's separate restore-on-login path so the in-app switch cannot be
        // bypassed by macOS window restoration.
        NSApp.disableRelaunchOnLogin()
        if shouldPresentMainWindow {
            Self.mainWindowOpenRequestBroker.receive()
        }

        // Finder sends a dedicated Services pasteboard. Keep this separate from
        // document opening, which intentionally starts image compression. Set
        // the provider last because AppKit may deliver a pending request as soon
        // as the provider becomes available.
        NSApp.servicesProvider = copyPathServiceProvider
        NSUpdateDynamicServices()
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        Self.imageOpenRequestBroker.receive(urls)
    }

    func applicationShouldHandleReopen(
        _ sender: NSApplication,
        hasVisibleWindows flag: Bool
    ) -> Bool {
        // If a Finder Service or login item already started the menu-bar app in
        // the background, explicitly opening Crosio later must still show it.
        Self.mainWindowOpenRequestBroker.receive()
        return false
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let recordingModel = Self.recordingModel, recordingModel.isBusy else {
            return .terminateNow
        }
        guard pendingTerminationTask == nil else {
            return .terminateLater
        }

        pendingTerminationTask = Task { @MainActor [weak self, weak sender] in
            let mayTerminate = await recordingModel.prepareForApplicationTermination()
            sender?.reply(toApplicationShouldTerminate: mayTerminate)
            self?.pendingTerminationTask = nil
        }
        return .terminateLater
    }
}
