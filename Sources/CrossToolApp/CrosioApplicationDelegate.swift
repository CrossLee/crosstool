import AppKit

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
    static let imageOpenRequestBroker = ImageOpenRequestBroker()

    private var pendingTerminationTask: Task<Void, Never>?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // SMAppService is the single source of truth for login launches. Disable
        // AppKit's separate restore-on-login path so the in-app switch cannot be
        // bypassed by macOS window restoration.
        NSApp.disableRelaunchOnLogin()
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        Self.imageOpenRequestBroker.receive(urls)
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
