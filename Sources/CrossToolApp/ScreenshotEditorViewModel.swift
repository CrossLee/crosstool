import AppKit
import CrossToolCore
import Foundation
import UniformTypeIdentifiers

@MainActor
final class ScreenshotEditorViewModel: ObservableObject {
    typealias ShareHandler = @MainActor (Data) throws -> Void
    typealias PinHandler = @MainActor (CGImage) throws -> Void

    @Published var selectedTool: ScreenshotEditTool = .pen
    @Published var selectedColor = ScreenshotRGBAColor(red: 0.96, green: 0.19, blue: 0.23)
    @Published var lineWidth: Double = 8
    @Published var mosaicBrushDiameter: Double = 48
    @Published var mosaicPixelSize: Double = 18

    @Published private(set) var previewImage: CGImage
    /// Generated lazily when the user first paints with the mosaic tool.
    /// Long screenshots can be tens of megapixels; eagerly allocating a
    /// second full-resolution bitmap would make merely opening them costly.
    @Published private(set) var mosaicPreviewImage: CGImage?
    @Published private(set) var draftOperation: ScreenshotEditOperation?
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false
    @Published private(set) var hasAnnotations = false
    @Published private(set) var hasDeliveredOutput = false
    private(set) var hasAnyDeliveredOutput = false
    @Published private(set) var statusMessage: String?
    @Published private(set) var textRecognitionState: ScreenshotTextRecognitionState = .idle
    @Published private(set) var recognizedText = ""
    @Published private(set) var textCopyMessage: String?

    let pixelWidth: Int
    let pixelHeight: Int

    var requestClose: (() -> Void)?
    weak var presentingWindow: NSWindow?

    private let sourceURL: URL
    private let renderer: ScreenshotEditorRenderer
    private let onSharePNG: ShareHandler
    private let onPinImage: PinHandler?
    private let pasteboardWriter: any ScreenshotPasteboardWriting
    private let textRecognizer: any ScreenshotTextRecognizing
    private let textPasteboardWriter: any ScreenshotTextPasteboardWriting
    private let textRecognitionImage: CGImage
    private var history = ScreenshotEditHistory()
    private var deliveredOperations: [ScreenshotEditOperation]?
    private var textRecognitionTask: Task<Void, Never>?
    private var textRecognitionRequestID: UUID?

    init(
        sourceURL: URL,
        originalWasAutomaticallyCopied: Bool = false,
        pasteboardWriter: (any ScreenshotPasteboardWriting)? = nil,
        textRecognizer: (any ScreenshotTextRecognizing)? = nil,
        textPasteboardWriter: (any ScreenshotTextPasteboardWriting)? = nil,
        onPinImage: PinHandler? = nil,
        onSharePNG: @escaping ShareHandler
    ) throws {
        let renderer = try ScreenshotEditorRenderer(imageURL: sourceURL)
        self.sourceURL = sourceURL
        self.renderer = renderer
        self.onSharePNG = onSharePNG
        self.onPinImage = onPinImage
        self.pasteboardWriter = pasteboardWriter ?? ScreenshotPasteboardWriter()
        self.textRecognizer = textRecognizer ?? VisionScreenshotTextRecognizer()
        self.textPasteboardWriter = textPasteboardWriter ?? ScreenshotTextPasteboardWriter()
        self.textRecognitionImage = renderer.originalImage
        self.previewImage = renderer.originalImage
        self.mosaicPreviewImage = nil
        self.pixelWidth = renderer.pixelWidth
        self.pixelHeight = renderer.pixelHeight
        if originalWasAutomaticallyCopied {
            deliveredOperations = []
            hasAnyDeliveredOutput = true
            hasDeliveredOutput = true
        }
    }

    var recognizedCharacterCount: Int {
        recognizedText.count
    }

    var canCopyRecognizedText: Bool {
        textRecognitionState == .recognized && !recognizedText.isEmpty
    }

    func startAutomaticTextRecognition() {
        guard textRecognitionState == .idle else { return }
        beginTextRecognition()
    }

    func retryTextRecognition() {
        beginTextRecognition()
    }

    func cancelTextRecognition() {
        textRecognitionRequestID = nil
        textRecognitionTask?.cancel()
        textRecognitionTask = nil
        if textRecognitionState == .recognizing {
            textRecognitionState = .idle
        }
    }

    func copyRecognizedText() {
        guard canCopyRecognizedText else { return }
        do {
            try textPasteboardWriter.writeText(recognizedText)
            textCopyMessage = "文字已复制到剪贴板"
        } catch {
            textCopyMessage = "复制文字失败：\(error.localizedDescription)"
        }
    }

    func beginInteraction(at point: ScreenshotEditPoint) {
        statusMessage = nil
        switch selectedTool {
        case .pen:
            draftOperation = .freehand(ScreenshotFreehandStroke(
                points: [point],
                color: selectedColor,
                lineWidth: lineWidth
            ))
        case .mosaic:
            prepareMosaicPreviewIfNeeded()
            draftOperation = .mosaic(ScreenshotMosaicStroke(
                points: [point],
                brushDiameter: mosaicBrushDiameter,
                pixelSize: mosaicPixelSize
            ))
        case .rectangle:
            draftOperation = .rectangle(ScreenshotRectangleAnnotation(
                start: point,
                end: point,
                color: selectedColor,
                lineWidth: lineWidth
            ))
        case .arrow:
            draftOperation = .arrow(ScreenshotArrowAnnotation(
                start: point,
                end: point,
                color: selectedColor,
                lineWidth: lineWidth
            ))
        }
        refreshDeliveryState()
    }

    func updateInteraction(to point: ScreenshotEditPoint) {
        updateInteraction(to: point, forceTerminalPoint: false)
    }

    private func updateInteraction(
        to point: ScreenshotEditPoint,
        forceTerminalPoint: Bool
    ) {
        guard let draftOperation else { return }
        switch draftOperation {
        case .freehand(let stroke):
            guard stroke.points.last != point,
                  forceTerminalPoint || shouldAppend(
                      point,
                      after: stroke.points.last,
                      minimumDistance: max(0.8, stroke.lineWidth / 5)
                  ) else {
                return
            }
            self.draftOperation = .freehand(ScreenshotFreehandStroke(
                id: stroke.id,
                points: stroke.points + [point],
                color: stroke.color,
                lineWidth: stroke.lineWidth
            ))
        case .mosaic(let stroke):
            guard stroke.points.last != point,
                  forceTerminalPoint || shouldAppend(
                      point,
                      after: stroke.points.last,
                      minimumDistance: max(1.5, stroke.brushDiameter / 8)
                  ) else {
                return
            }
            self.draftOperation = .mosaic(ScreenshotMosaicStroke(
                id: stroke.id,
                points: stroke.points + [point],
                brushDiameter: stroke.brushDiameter,
                pixelSize: stroke.pixelSize
            ))
        case .rectangle(let annotation):
            self.draftOperation = .rectangle(ScreenshotRectangleAnnotation(
                id: annotation.id,
                start: annotation.start,
                end: point,
                color: annotation.color,
                lineWidth: annotation.lineWidth
            ))
        case .arrow(let annotation):
            self.draftOperation = .arrow(ScreenshotArrowAnnotation(
                id: annotation.id,
                start: annotation.start,
                end: point,
                color: annotation.color,
                lineWidth: annotation.lineWidth
            ))
        }
    }

    func endInteraction(at point: ScreenshotEditPoint) {
        // Sampling thresholds keep long drags efficient, but the terminal
        // point must still land exactly under the pointer for pen and mosaic.
        updateInteraction(to: point, forceTerminalPoint: true)
        guard let operation = draftOperation else { return }
        draftOperation = nil

        guard isMeaningful(operation) else {
            refreshDeliveryState()
            return
        }
        history.commit(operation)
        rebuildPreview()
    }

    func cancelInteraction() {
        draftOperation = nil
        refreshDeliveryState()
    }

    func undo() {
        draftOperation = nil
        guard history.undo() != nil else { return }
        rebuildPreview()
    }

    func redo() {
        draftOperation = nil
        guard history.redo() != nil else { return }
        rebuildPreview()
    }

    func clearAnnotations() {
        guard !history.operations.isEmpty else { return }
        draftOperation = nil
        history.reset()
        rebuildPreview()
        statusMessage = "已清空全部标注"
    }

    func chooseMosaicPixelSize(_ value: Double) {
        mosaicPixelSize = value
        guard selectedTool == .mosaic || mosaicPreviewImage != nil else { return }
        do {
            mosaicPreviewImage = try renderer.mosaicImage(pixelSize: value)
        } catch {
            statusMessage = error.localizedDescription
        }
    }

    func copyToPasteboard() {
        do {
            let data = try ScreenshotEditorRenderer.pngData(from: previewImage)
            let image = NSImage(
                cgImage: previewImage,
                size: NSSize(width: pixelWidth, height: pixelHeight)
            )
            try pasteboardWriter.writePNG(data, tiffData: image.tiffRepresentation)
            markCurrentVersionDelivered()
            requestClose?()
        } catch {
            statusMessage = "复制失败：\(error.localizedDescription)"
        }
    }

    func pinCurrentImage() {
        cancelInteraction()
        guard let onPinImage else {
            statusMessage = "当前无法创建悬浮贴图"
            return
        }

        do {
            try onPinImage(previewImage)
            markCurrentVersionDelivered()
            requestClose?()
        } catch {
            statusMessage = "创建悬浮贴图失败：\(error.localizedDescription)"
        }
    }

    func saveAsPNG() {
        let panel = NSSavePanel()
        panel.title = "保存截图"
        panel.nameFieldLabel = "文件名："
        panel.prompt = "保存"
        panel.allowedContentTypes = [.png]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = suggestedFilename

        let completion: (NSApplication.ModalResponse) -> Void = { [weak self] response in
            guard response == .OK, let url = panel.url else { return }
            Task { @MainActor [weak self] in
                self?.writePNG(to: url, successMessage: "截图已保存")
            }
        }
        if let presentingWindow {
            panel.beginSheetModal(for: presentingWindow, completionHandler: completion)
        } else {
            panel.begin(completionHandler: completion)
        }
    }

    func addToShare() {
        do {
            let data = try ScreenshotEditorRenderer.pngData(from: previewImage)
            try onSharePNG(data)
            markCurrentVersionDelivered()
            requestClose?()
        } catch {
            statusMessage = "加入共享区失败：\(error.localizedDescription)"
        }
    }

    private func beginTextRecognition() {
        textRecognitionRequestID = nil
        textRecognitionTask?.cancel()

        let requestID = UUID()
        let recognizer = textRecognizer
        let image = textRecognitionImage
        textRecognitionRequestID = requestID
        recognizedText = ""
        textCopyMessage = nil
        textRecognitionState = .recognizing

        textRecognitionTask = Task { [weak self, recognizer, image] in
            let result: Result<String, Error>
            do {
                result = .success(try await recognizer.recognizeText(in: image))
            } catch {
                result = .failure(error)
            }

            guard !Task.isCancelled, let self else { return }
            self.applyTextRecognitionResult(result, requestID: requestID)
        }
    }

    private func applyTextRecognitionResult(
        _ result: Result<String, Error>,
        requestID: UUID
    ) {
        guard textRecognitionRequestID == requestID else { return }
        textRecognitionRequestID = nil
        textRecognitionTask = nil

        switch result {
        case .success(let text):
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            recognizedText = trimmed
            textRecognitionState = trimmed.isEmpty ? .empty : .recognized
        case .failure(let error):
            if error is CancellationError {
                textRecognitionState = .idle
            } else {
                recognizedText = ""
                textRecognitionState = .failed(error.localizedDescription)
            }
        }
    }

    private func writePNG(to url: URL, successMessage: String) {
        do {
            let data = try ScreenshotEditorRenderer.pngData(from: previewImage)
            try data.write(to: url, options: .atomic)
            markCurrentVersionDelivered()
            statusMessage = successMessage
            requestClose?()
        } catch {
            statusMessage = "保存失败：\(error.localizedDescription)"
        }
    }

    private func rebuildPreview() {
        do {
            previewImage = try renderer.render(operations: history.operations)
            publishHistoryState()
        } catch {
            statusMessage = "无法应用标注：\(error.localizedDescription)"
        }
    }

    private func prepareMosaicPreviewIfNeeded() {
        guard mosaicPreviewImage == nil else { return }
        do {
            mosaicPreviewImage = try renderer.mosaicImage(pixelSize: mosaicPixelSize)
        } catch {
            statusMessage = error.localizedDescription
        }
    }

    private func publishHistoryState() {
        canUndo = history.canUndo
        canRedo = history.canRedo
        hasAnnotations = !history.operations.isEmpty
        refreshDeliveryState()
    }

    private func markCurrentVersionDelivered() {
        deliveredOperations = history.operations
        hasAnyDeliveredOutput = true
        refreshDeliveryState()
    }

    private func refreshDeliveryState() {
        hasDeliveredOutput = draftOperation == nil
            && deliveredOperations == history.operations
    }

    private var suggestedFilename: String {
        let base = sourceURL.deletingPathExtension().lastPathComponent
        return "\(base)-已编辑.png"
    }

    private func shouldAppend(
        _ point: ScreenshotEditPoint,
        after previous: ScreenshotEditPoint?,
        minimumDistance: Double
    ) -> Bool {
        guard let previous else { return true }
        return hypot(point.x - previous.x, point.y - previous.y) >= minimumDistance
    }

    private func isMeaningful(_ operation: ScreenshotEditOperation) -> Bool {
        switch operation {
        case .freehand(let stroke):
            return !stroke.points.isEmpty
        case .mosaic(let stroke):
            return !stroke.points.isEmpty
        case .rectangle(let annotation):
            return abs(annotation.end.x - annotation.start.x) >= 1
                || abs(annotation.end.y - annotation.start.y) >= 1
        case .arrow(let annotation):
            return hypot(
                annotation.end.x - annotation.start.x,
                annotation.end.y - annotation.start.y
            ) >= 1
        }
    }
}
