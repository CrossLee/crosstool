import AppKit

@MainActor
final class CrosioApplicationDelegate: NSObject, NSApplicationDelegate {
    static weak var recordingModel: ScreenRecordingFeatureModel?

    private var pendingTerminationTask: Task<Void, Never>?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // SMAppService is the single source of truth for login launches. Disable
        // AppKit's separate restore-on-login path so the in-app switch cannot be
        // bypassed by macOS window restoration.
        NSApp.disableRelaunchOnLogin()
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
