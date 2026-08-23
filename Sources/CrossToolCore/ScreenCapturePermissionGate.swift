public struct ScreenCapturePermissionGate: Sendable {
    public enum Action: Equatable, Sendable {
        case capture
        case requestPermission
        case showSettingsGuidance
    }

    private var didRequestThisLaunch = false

    public init() {}

    public mutating func nextAction(isAuthorized: Bool) -> Action {
        if isAuthorized {
            return .capture
        }
        guard !didRequestThisLaunch else {
            return .showSettingsGuidance
        }
        didRequestThisLaunch = true
        return .requestPermission
    }

    public mutating func markRequestAttempted() {
        didRequestThisLaunch = true
    }
}
