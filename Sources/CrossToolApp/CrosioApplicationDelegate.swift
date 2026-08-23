import AppKit

@MainActor
final class CrosioApplicationDelegate: NSObject, NSApplicationDelegate {
    static weak var recordingModel: ScreenRecordingFeatureModel?

    private var pendingTerminationTask: Task<Void, Never>?

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
