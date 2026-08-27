import Foundation
@testable import CrossToolApp
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

    @Test("Crosio is an alternate Open With handler for images")
    func sourceInfoPlistRegistersImageDocuments() throws {
        let info = try sourceInfoPlist()
        let documentTypes = try #require(info["CFBundleDocumentTypes"] as? [[String: Any]])
        #expect(documentTypes.count == 1)
        let imageType = try #require(documentTypes.first)

        #expect(imageType["CFBundleTypeName"] as? String == "图片")
        #expect(imageType["CFBundleTypeRole"] as? String == "Viewer")
        #expect(imageType["LSHandlerRank"] as? String == "Alternate")
        #expect(imageType["LSItemContentTypes"] as? [String] == ["public.image"])
        #expect(imageType["NSDocumentClass"] == nil)
    }

    @MainActor
    @Test("Image open requests wait for the app model and preserve their order")
    func imageOpenRequestBrokerQueuesColdLaunchRequests() throws {
        let broker = ImageOpenRequestBroker()
        let first = URL(fileURLWithPath: "/tmp/first.png")
        let second = URL(fileURLWithPath: "/tmp/second.jpg")
        let third = URL(fileURLWithPath: "/tmp/third.heic")
        let remote = URL(string: "https://example.com/not-a-file.png")!
        var deliveries: [[URL]] = []

        broker.receive([first, remote])
        broker.receive([second])
        #expect(deliveries.isEmpty)

        broker.install { deliveries.append($0) }
        broker.receive([third])
        broker.receive([remote])

        #expect(deliveries == [[first, second], [third]])
    }

    private func sourceInfoPlist() throws -> [String: Any] {
        let projectRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let infoPlistURL = projectRoot
            .appendingPathComponent("Resources", isDirectory: true)
            .appendingPathComponent("Info.plist", isDirectory: false)
        let data = try Data(contentsOf: infoPlistURL)
        return try #require(
            PropertyListSerialization.propertyList(from: data, format: nil)
                as? [String: Any]
        )
    }
}
