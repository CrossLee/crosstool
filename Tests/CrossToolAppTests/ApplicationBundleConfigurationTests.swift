import Foundation
import Testing

@Suite("Application bundle configuration")
struct ApplicationBundleConfigurationTests {
    @Test("Crosio is a UI-element app without becoming background-only")
    func sourceInfoPlistUsesUIElementPresentation() throws {
        let projectRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let infoPlistURL = projectRoot
            .appendingPathComponent("Resources", isDirectory: true)
            .appendingPathComponent("Info.plist", isDirectory: false)

        let data = try Data(contentsOf: infoPlistURL)
        let info = try #require(
            PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any]
        )

        #expect(info["LSUIElement"] as? Bool == true)
        if let backgroundOnlyValue = info["LSBackgroundOnly"] {
            let backgroundOnly = try #require(backgroundOnlyValue as? Bool)
            #expect(backgroundOnly == false)
        }
    }
}
