import CrossToolCore
import Testing

@Test
func authorizedScreenCaptureProceedsWithoutRequesting() {
    var gate = ScreenCapturePermissionGate()

    #expect(gate.nextAction(isAuthorized: true) == .capture)
    #expect(gate.nextAction(isAuthorized: true) == .capture)
}

@Test
func missingScreenCapturePermissionRequestsOnlyOncePerLaunch() {
    var gate = ScreenCapturePermissionGate()

    #expect(gate.nextAction(isAuthorized: false) == .requestPermission)
    #expect(gate.nextAction(isAuthorized: false) == .showSettingsGuidance)
    #expect(gate.nextAction(isAuthorized: false) == .showSettingsGuidance)
}

@Test
func authorizationAfterRequestAllowsCapture() {
    var gate = ScreenCapturePermissionGate()

    #expect(gate.nextAction(isAuthorized: false) == .requestPermission)
    #expect(gate.nextAction(isAuthorized: true) == .capture)
}

@Test
func explicitPermissionRequestAlsoSuppressesAutomaticRepeat() {
    var gate = ScreenCapturePermissionGate()
    gate.markRequestAttempted()

    #expect(gate.nextAction(isAuthorized: false) == .showSettingsGuidance)
}
