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

    @Test("Text pasteboard writer stores OCR text only when explicitly called")
    func writesRecognizedTextToPasteboard() throws {
        let pasteboard = NSPasteboard(name: .init("crosio-ocr-test-\(UUID())"))
        defer { pasteboard.releaseGlobally() }

        try ScreenshotTextPasteboardWriter(pasteboard: pasteboard)
            .writeText("中文 and English")

        #expect(pasteboard.string(forType: .string) == "中文 and English")
        #expect(pasteboard.data(forType: .png) == nil)
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

    @Test("OCR recognizes the original without replacing the automatically copied image")
    func recognizesTextWithoutAutomaticTextCopy() async throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let imageWriter = RecordingScreenshotPasteboardWriter()
        let textWriter = RecordingScreenshotTextPasteboardWriter()
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            originalWasAutomaticallyCopied: true,
            pasteboardWriter: imageWriter,
            textRecognizer: ImmediateScreenshotTextRecognizer(text: "  第一行\nSecond line  "),
            textPasteboardWriter: textWriter,
            onSharePNG: { _ in }
        )

        model.startAutomaticTextRecognition()
        #expect(await waitUntil { model.textRecognitionState == .recognized })

        #expect(model.recognizedText == "第一行\nSecond line")
        #expect(model.recognizedCharacterCount == 15)
        #expect(model.canCopyRecognizedText)
        #expect(textWriter.writtenTexts.isEmpty)
        #expect(imageWriter.writeCount == 0)
        #expect(model.hasDeliveredOutput)
        #expect(model.hasAnyDeliveredOutput)

        var closeCount = 0
        model.requestClose = { closeCount += 1 }
        model.copyRecognizedText()

        #expect(textWriter.writtenTexts == ["第一行\nSecond line"])
        #expect(imageWriter.writeCount == 0)
        #expect(closeCount == 0)
        #expect(model.hasDeliveredOutput)
        #expect(model.textCopyMessage == "文字已复制到剪贴板")
    }

    @Test("OCR distinguishes empty output and can retry after a failure")
    func handlesEmptyFailureAndRetry() async throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }

        let emptyModel = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            textRecognizer: ImmediateScreenshotTextRecognizer(text: " \n\t "),
            textPasteboardWriter: RecordingScreenshotTextPasteboardWriter(),
            onSharePNG: { _ in }
        )
        emptyModel.startAutomaticTextRecognition()
        #expect(await waitUntil { emptyModel.textRecognitionState == .empty })
        #expect(emptyModel.recognizedText.isEmpty)
        #expect(!emptyModel.canCopyRecognizedText)

        let recognizer = SequenceScreenshotTextRecognizer(outcomes: [
            .failure("本机识别器暂时不可用"),
            .text("重试成功")
        ])
        let retryModel = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            textRecognizer: recognizer,
            textPasteboardWriter: RecordingScreenshotTextPasteboardWriter(),
            onSharePNG: { _ in }
        )
        retryModel.startAutomaticTextRecognition()
        #expect(await waitUntil {
            retryModel.textRecognitionState == .failed("本机识别器暂时不可用")
        })

        retryModel.retryTextRecognition()
        #expect(await waitUntil { retryModel.textRecognitionState == .recognized })
        #expect(retryModel.recognizedText == "重试成功")
        #expect(await recognizer.currentCallCount() == 2)
    }

    @Test("A cancelled late OCR result cannot overwrite a newer request")
    func ignoresCancelledLateRecognitionResult() async throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let recognizer = ControlledScreenshotTextRecognizer()
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            textRecognizer: recognizer,
            textPasteboardWriter: RecordingScreenshotTextPasteboardWriter(),
            onSharePNG: { _ in }
        )

        model.startAutomaticTextRecognition()
        model.startAutomaticTextRecognition()
        #expect(await waitUntil { await recognizer.currentCallCount() == 1 })

        model.retryTextRecognition()
        #expect(await waitUntil { await recognizer.currentCallCount() == 2 })
        await recognizer.complete(call: 0, with: "旧文字")
        await Task.yield()
        #expect(model.textRecognitionState == .recognizing)
        #expect(model.recognizedText.isEmpty)

        await recognizer.complete(call: 1, with: "新文字")
        #expect(await waitUntil { model.textRecognitionState == .recognized })
        #expect(model.recognizedText == "新文字")
    }

    @Test("Cancelling OCR returns to idle and ignores its eventual response")
    func cancelsRecognition() async throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }
        let recognizer = ControlledScreenshotTextRecognizer()
        let model = try ScreenshotEditorViewModel(
            sourceURL: sourceURL,
            textRecognizer: recognizer,
            textPasteboardWriter: RecordingScreenshotTextPasteboardWriter(),
            onSharePNG: { _ in }
        )

        model.startAutomaticTextRecognition()
        #expect(await waitUntil { await recognizer.currentCallCount() == 1 })
        model.cancelTextRecognition()
        #expect(model.textRecognitionState == .idle)

        await recognizer.complete(call: 0, with: "不应出现")
        await Task.yield()
        #expect(model.textRecognitionState == .idle)
        #expect(model.recognizedText.isEmpty)
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

    private func waitUntil(
        _ condition: @escaping @MainActor () async -> Bool
    ) async -> Bool {
        for _ in 0..<200 {
            if await condition() { return true }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        return false
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

private struct ImmediateScreenshotTextRecognizer: ScreenshotTextRecognizing {
    let text: String

    func recognizeText(in image: CGImage) async throws -> String {
        text
    }
}

private actor SequenceScreenshotTextRecognizer: ScreenshotTextRecognizing {
    enum Outcome: Sendable {
        case text(String)
        case failure(String)
    }

    private var outcomes: [Outcome]
    private var callCount = 0

    init(outcomes: [Outcome]) {
        self.outcomes = outcomes
    }

    func recognizeText(in image: CGImage) async throws -> String {
        let index = callCount
        callCount += 1
        let outcome = outcomes[min(index, outcomes.count - 1)]
        switch outcome {
        case .text(let text):
            return text
        case .failure(let message):
            throw ScreenshotTextRecognizerTestError(message: message)
        }
    }

    func currentCallCount() -> Int {
        callCount
    }
}

private actor ControlledScreenshotTextRecognizer: ScreenshotTextRecognizing {
    private var callCount = 0
    private var continuations: [Int: CheckedContinuation<String, Error>] = [:]

    func recognizeText(in image: CGImage) async throws -> String {
        let call = callCount
        callCount += 1
        return try await withCheckedThrowingContinuation { continuation in
            continuations[call] = continuation
        }
    }

    func currentCallCount() -> Int {
        callCount
    }

    func complete(call: Int, with text: String) {
        continuations.removeValue(forKey: call)?.resume(returning: text)
    }
}

private struct ScreenshotTextRecognizerTestError: LocalizedError {
    let message: String

    var errorDescription: String? { message }
}

@MainActor
private final class RecordingScreenshotTextPasteboardWriter: ScreenshotTextPasteboardWriting {
    private(set) var writtenTexts: [String] = []
    private let error: Error?

    init(error: Error? = nil) {
        self.error = error
    }

    func writeText(_ text: String) throws {
        if let error { throw error }
        writtenTexts.append(text)
    }
}
