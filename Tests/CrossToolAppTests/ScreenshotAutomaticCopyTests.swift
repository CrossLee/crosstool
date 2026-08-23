import AppKit
import CrossToolCore
import Foundation
@testable import CrossToolApp
import Testing

@MainActor
@Suite("Screenshot automatic copy")
struct ScreenshotAutomaticCopyTests {
    @Test("Pasteboard writer stores independent PNG and TIFF image data")
    func writesImageBytesToPasteboard() throws {
        let pasteboard = NSPasteboard(name: .init("crosio-screenshot-test-\(UUID())"))
        defer { pasteboard.releaseGlobally() }
        let pngData = try makePNGData()
        let image = try #require(NSImage(data: pngData))

        try ScreenshotPasteboardWriter(pasteboard: pasteboard).writePNG(
            pngData,
            tiffData: image.tiffRepresentation
        )

        #expect(pasteboard.data(forType: .png) == pngData)
        #expect(pasteboard.data(forType: .tiff) != nil)
        #expect(NSImage(pasteboard: pasteboard) != nil)
    }

    @Test("Automatically copied original remains delivered until annotations change")
    func tracksTheDeliveredEditRevision() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let writer = RecordingScreenshotPasteboardWriter()
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            originalWasAutomaticallyCopied: true,
            pasteboardWriter: writer,
            onSharePNG: { _ in }
        )

        #expect(model.hasDeliveredOutput)
        #expect(model.hasAnyDeliveredOutput)

        model.selectedTool = .rectangle
        model.beginInteraction(at: .init(x: 0, y: 0))
        model.endInteraction(at: .init(x: 1, y: 1))
        #expect(model.hasAnnotations)
        #expect(!model.hasDeliveredOutput)

        model.undo()
        #expect(!model.hasAnnotations)
        #expect(model.hasDeliveredOutput)

        model.beginInteraction(at: .init(x: 0, y: 0))
        model.endInteraction(at: .init(x: 1, y: 1))
        var closeCount = 0
        model.requestClose = { closeCount += 1 }
        model.copyToPasteboard()

        #expect(writer.writeCount == 1)
        #expect(model.hasDeliveredOutput)
        #expect(closeCount == 1)
    }

    @Test("A cancelled draft interaction restores the delivered original state")
    func cancellingDraftRestoresDeliveryState() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            originalWasAutomaticallyCopied: true,
            pasteboardWriter: RecordingScreenshotPasteboardWriter(),
            onSharePNG: { _ in }
        )

        model.beginInteraction(at: .init(x: 0, y: 0))
        #expect(!model.hasDeliveredOutput)
        model.cancelInteraction()
        #expect(model.hasDeliveredOutput)
    }

    @Test("Failed manual copy keeps the editor open and version undelivered")
    func failedCopyDoesNotClose() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let writer = RecordingScreenshotPasteboardWriter(error: ScreenshotPasteboardError.writeFailed)
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            pasteboardWriter: writer,
            onSharePNG: { _ in }
        )
        var closeCount = 0
        model.requestClose = { closeCount += 1 }

        model.copyToPasteboard()

        #expect(writer.writeCount == 1)
        #expect(!model.hasDeliveredOutput)
        #expect(!model.hasAnyDeliveredOutput)
        #expect(closeCount == 0)
        #expect(model.statusMessage?.contains("复制失败") == true)
    }

    @Test("Pinning sends the annotated preview, delivers that revision, and closes")
    func pinningAnnotatedPreviewDeliversCurrentRevision() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let originalPNG = try Data(contentsOf: sourceURL)
        var pinnedPNG: Data?
        var pinCount = 0
        var closeCount = 0
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            originalWasAutomaticallyCopied: true,
            pasteboardWriter: RecordingScreenshotPasteboardWriter(),
            onPinImage: { image in
                pinCount += 1
                pinnedPNG = try ScreenshotEditorRenderer.pngData(from: image)
            },
            onSharePNG: { _ in }
        )
        model.requestClose = { closeCount += 1 }

        model.selectedTool = .rectangle
        model.lineWidth = 2
        model.beginInteraction(at: .init(x: 0, y: 0))
        model.endInteraction(at: .init(x: 1, y: 1))
        let annotatedPreviewPNG = try ScreenshotEditorRenderer.pngData(from: model.previewImage)

        #expect(model.hasAnnotations)
        #expect(!model.hasDeliveredOutput)
        #expect(annotatedPreviewPNG != originalPNG)

        model.pinCurrentImage()

        #expect(pinCount == 1)
        #expect(pinnedPNG == annotatedPreviewPNG)
        #expect(pinnedPNG != originalPNG)
        #expect(model.hasDeliveredOutput)
        #expect(model.hasAnyDeliveredOutput)
        #expect(closeCount == 1)
    }

    @Test("A failed pin keeps the annotated revision undelivered and the editor open")
    func failedPinDoesNotDeliverOrClose() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        var pinCount = 0
        var closeCount = 0
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            originalWasAutomaticallyCopied: true,
            pasteboardWriter: RecordingScreenshotPasteboardWriter(),
            onPinImage: { _ in
                pinCount += 1
                throw CocoaError(.fileWriteUnknown)
            },
            onSharePNG: { _ in }
        )
        model.requestClose = { closeCount += 1 }

        model.selectedTool = .rectangle
        model.beginInteraction(at: .init(x: 0, y: 0))
        model.endInteraction(at: .init(x: 1, y: 1))
        #expect(model.hasAnnotations)
        #expect(!model.hasDeliveredOutput)

        model.pinCurrentImage()

        #expect(pinCount == 1)
        #expect(!model.hasDeliveredOutput)
        #expect(model.hasAnyDeliveredOutput)
        #expect(closeCount == 0)
        #expect(model.statusMessage?.contains("创建悬浮贴图失败") == true)
    }

    private func makeTemporaryPNG() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-screenshot-\(UUID()).png")
        try makePNGData().write(to: url, options: .atomic)
        return url
    }

    private func makePNGData() throws -> Data {
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let context = try #require(CGContext(
            data: nil,
            width: 2,
            height: 2,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(NSColor.systemBlue.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        return try ScreenshotEditorRenderer.pngData(from: #require(context.makeImage()))
    }
}

@MainActor
private final class RecordingScreenshotPasteboardWriter: ScreenshotPasteboardWriting {
    private(set) var writeCount = 0
    private let error: Error?

    init(error: Error? = nil) {
        self.error = error
    }

    func writePNG(_ pngData: Data, tiffData: Data?) throws {
        writeCount += 1
        if let error { throw error }
    }
}
