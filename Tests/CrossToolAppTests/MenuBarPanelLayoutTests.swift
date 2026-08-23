@testable import CrossToolApp
import Testing

@Suite("Menu bar panel layout")
struct MenuBarPanelLayoutTests {
    @Test("Sharing toggle immediately follows copy link and leaves the footer")
    func actionPlacementContract() {
        #expect(MenuBarPanelLayout.statusActions == [
            .copyShareLink,
            .toggleServer,
        ])
        #expect(MenuBarPanelLayout.footerActions == [
            .openMainWindow,
            .terminate,
        ])
        #expect(MenuBarPanelLayout.footerActions.contains(.toggleServer) == false)
    }
}
