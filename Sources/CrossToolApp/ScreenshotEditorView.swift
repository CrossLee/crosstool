import AppKit
import CrossToolCore
import SwiftUI

struct ScreenshotEditorView: View {
    @ObservedObject var model: ScreenshotEditorViewModel

    var body: some View {
        VStack(spacing: 0) {
            editorToolbar
            Divider()
            HStack(spacing: 0) {
                ScreenshotEditorCanvas(model: model)
                Divider()
                textRecognitionInspector
                    .frame(width: 300)
            }
            Divider()
            footer
        }
        .frame(minWidth: 880, minHeight: 580)
        .background(Color(nsColor: .windowBackgroundColor))
        .task {
            model.startAutomaticTextRecognition()
        }
    }

    private var textRecognitionInspector: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 8) {
                Image(systemName: "text.viewfinder")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(Color.crossToolAccent)
                Text("自动 OCR")
                    .font(.headline)
                Spacer()
                Text("本机")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(Color.crossToolAccent)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 3)
                    .background(Color.crossToolAccent.opacity(0.12), in: Capsule())
            }

            Text("由 Mac 在本机识别原图，不上传截图")
                .font(.caption)
                .foregroundStyle(.secondary)

            Group {
                switch model.textRecognitionState {
                case .idle, .recognizing:
                    VStack(spacing: 12) {
                        ProgressView()
                            .controlSize(.small)
                        Text("正在本机识别中…")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)

                case .recognized:
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            Text("识别出 \(model.recognizedCharacterCount) 个字符")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Spacer()
                            Button("重新识别") {
                                model.retryTextRecognition()
                            }
                            .buttonStyle(.link)
                            .font(.caption)
                        }

                        ScrollView {
                            Text(model.recognizedText)
                                .font(.system(size: 13))
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .topLeading)
                                .padding(10)
                        }
                        .background(
                            Color(nsColor: .textBackgroundColor),
                            in: RoundedRectangle(cornerRadius: 8)
                        )
                        .overlay {
                            RoundedRectangle(cornerRadius: 8)
                                .stroke(Color.primary.opacity(0.1), lineWidth: 1)
                        }

                        Button("复制文字", systemImage: "doc.on.doc") {
                            model.copyRecognizedText()
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(.crossToolAccent)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                    }

                case .empty:
                    recognitionPlaceholder(
                        systemImage: "text.magnifyingglass",
                        title: "没有识别到文字",
                        detail: "可以确认截图里有清晰文字后再试一次"
                    )

                case .failed(let message):
                    recognitionPlaceholder(
                        systemImage: "exclamationmark.triangle",
                        title: "文字识别失败",
                        detail: message
                    )
                }
            }

            if let message = model.textCopyMessage {
                Text(message)
                    .font(.caption)
                    .foregroundStyle(
                        message.hasPrefix("复制文字失败") ? Color.red : Color.crossToolAccent
                    )
            }
        }
        .padding(16)
        .background(Color(nsColor: .controlBackgroundColor))
    }

    private func recognitionPlaceholder(
        systemImage: String,
        title: String,
        detail: String
    ) -> some View {
        VStack(spacing: 10) {
            Image(systemName: systemImage)
                .font(.system(size: 26))
                .foregroundStyle(.secondary)
            Text(title)
                .font(.callout.weight(.medium))
            Text(detail)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button("重新识别") {
                model.retryTextRecognition()
            }
            .buttonStyle(.bordered)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var editorToolbar: some View {
        HStack(spacing: 14) {
            HStack(spacing: 5) {
                ForEach(ScreenshotEditTool.allCases, id: \.self) { tool in
                    toolButton(tool)
                }
            }

            Divider()
                .frame(height: 28)

            if model.selectedTool != .mosaic {
                HStack(spacing: 6) {
                    ForEach(Self.palette, id: \.self) { color in
                        paletteButton(color)
                    }
                    ColorPicker("自定义颜色", selection: selectedColorBinding, supportsOpacity: false)
                        .labelsHidden()
                        .frame(width: 28)
                        .help("自定义颜色")
                }

                Divider()
                    .frame(height: 28)
            }

            sizeControls

            Spacer(minLength: 10)

            HStack(spacing: 4) {
                Button {
                    model.undo()
                } label: {
                    Image(systemName: "arrow.uturn.backward")
                }
                .keyboardShortcut("z", modifiers: .command)
                .disabled(!model.canUndo)
                .help("撤销（⌘Z）")

                Button {
                    model.redo()
                } label: {
                    Image(systemName: "arrow.uturn.forward")
                }
                .keyboardShortcut("z", modifiers: [.command, .shift])
                .disabled(!model.canRedo)
                .help("重做（⇧⌘Z）")

                Button(role: .destructive) {
                    model.clearAnnotations()
                } label: {
                    Image(systemName: "trash")
                }
                .disabled(!model.hasAnnotations)
                .help("清空全部标注")
            }
            .buttonStyle(.bordered)
        }
        .padding(.horizontal, 16)
        .frame(height: 64)
    }

    @ViewBuilder
    private var sizeControls: some View {
        if model.selectedTool == .mosaic {
            HStack(spacing: 8) {
                Text("笔刷")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Slider(value: $model.mosaicBrushDiameter, in: 16...120, step: 2)
                    .frame(width: 86)
                Text("\(Int(model.mosaicBrushDiameter))")
                    .font(.system(.caption, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .frame(width: 28, alignment: .trailing)

                Text("颗粒")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Slider(value: mosaicPixelSizeBinding, in: 6...42, step: 2)
                    .frame(width: 70)
            }
        } else {
            HStack(spacing: 8) {
                Text("粗细")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Slider(value: $model.lineWidth, in: 2...32, step: 1)
                    .frame(width: 94)
                Text("\(Int(model.lineWidth)) px")
                    .font(.system(.caption, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .frame(width: 42, alignment: .trailing)
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                if let message = model.statusMessage {
                    Text(message)
                        .font(.callout)
                        .foregroundStyle(Color.crossToolAccent)
                } else {
                    Text("拖动鼠标进行标注；按 S 可贴到屏幕")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Text("原图尺寸 \(model.pixelWidth) × \(model.pixelHeight)，导出时保持原始分辨率")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }

            Spacer()

            Button("关闭") {
                model.requestClose?()
            }
            .keyboardShortcut(.cancelAction)

            Button("复制图片", systemImage: "doc.on.doc") {
                model.copyToPasteboard()
            }
            .help("复制图片（⌘C；选中 OCR 文字时复制所选文字）")

            Button("另存为…", systemImage: "square.and.arrow.down") {
                model.saveAsPNG()
            }
            .keyboardShortcut("s", modifiers: .command)

            Button("加入课堂共享", systemImage: "person.2.fill") {
                model.addToShare()
            }
            .buttonStyle(.borderedProminent)
            .tint(.crossToolAccent)
        }
        .padding(.horizontal, 16)
        .frame(height: 72)
    }

    private func toolButton(_ tool: ScreenshotEditTool) -> some View {
        Button {
            model.cancelInteraction()
            model.selectedTool = tool
        } label: {
            VStack(spacing: 4) {
                Image(systemName: tool.systemImage)
                    .font(.system(size: 16, weight: .medium))
                Text(tool.title)
                    .font(.caption2)
            }
            .foregroundStyle(model.selectedTool == tool ? Color.white : Color.primary)
            .frame(width: 52, height: 44)
            .background(
                model.selectedTool == tool
                    ? Color.crossToolAccent
                    : Color.primary.opacity(0.055),
                in: RoundedRectangle(cornerRadius: 8)
            )
            .contentShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .help(tool.title)
    }

    private func paletteButton(_ color: ScreenshotRGBAColor) -> some View {
        Button {
            model.selectedColor = color
        } label: {
            Circle()
                .fill(color.swiftUIColor)
                .frame(width: 19, height: 19)
                .overlay {
                    Circle()
                        .stroke(Color.primary.opacity(0.18), lineWidth: 1)
                }
                .padding(3)
                .overlay {
                    if model.selectedColor == color {
                        Circle()
                            .stroke(Color.crossToolAccent, lineWidth: 2)
                    }
                }
        }
        .buttonStyle(.plain)
        .help("选择颜色")
    }

    private var selectedColorBinding: Binding<Color> {
        Binding(
            get: { model.selectedColor.swiftUIColor },
            set: { color in
                guard let converted = NSColor(color).usingColorSpace(.sRGB) else { return }
                model.selectedColor = ScreenshotRGBAColor(
                    red: converted.redComponent,
                    green: converted.greenComponent,
                    blue: converted.blueComponent,
                    alpha: 1
                )
            }
        )
    }

    private var mosaicPixelSizeBinding: Binding<Double> {
        Binding(
            get: { model.mosaicPixelSize },
            set: { model.chooseMosaicPixelSize($0) }
        )
    }

    private static let palette: [ScreenshotRGBAColor] = [
        .red, .orange, .yellow, .green, .blue, .purple, .black, .white
    ]
}

private struct ScreenshotEditorCanvas: View {
    @ObservedObject var model: ScreenshotEditorViewModel

    private static let canvasCoordinateSpaceName = "ScreenshotEditorCanvas"

    var body: some View {
        GeometryReader { proxy in
            let imageRect = fittedImageRect(in: proxy.size)

            ZStack {
                Color(nsColor: .underPageBackgroundColor)

                Image(decorative: model.previewImage, scale: 1)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: imageRect.width, height: imageRect.height)
                    .position(x: imageRect.midX, y: imageRect.midY)
                    .shadow(color: .black.opacity(0.18), radius: 14, y: 4)

                ScreenshotDraftOverlay(
                    operation: model.draftOperation,
                    mosaicImage: model.mosaicPreviewImage,
                    imageRect: imageRect,
                    pixelWidth: model.pixelWidth,
                    pixelHeight: model.pixelHeight
                )

                Rectangle()
                    .fill(Color.clear)
                    .contentShape(Rectangle())
                    .frame(width: imageRect.width, height: imageRect.height)
                    .position(x: imageRect.midX, y: imageRect.midY)
                    .gesture(drawingGesture(imageRect: imageRect))

                Rectangle()
                    .stroke(Color.primary.opacity(0.16), lineWidth: 1)
                    .frame(width: imageRect.width, height: imageRect.height)
                    .position(x: imageRect.midX, y: imageRect.midY)
                    .allowsHitTesting(false)
            }
            .coordinateSpace(name: Self.canvasCoordinateSpaceName)
            .clipped()
        }
    }

    private func fittedImageRect(in size: CGSize) -> CGRect {
        let availableWidth = max(1, size.width - 48)
        let availableHeight = max(1, size.height - 48)
        let scale = min(
            availableWidth / CGFloat(model.pixelWidth),
            availableHeight / CGFloat(model.pixelHeight)
        )
        let width = CGFloat(model.pixelWidth) * scale
        let height = CGFloat(model.pixelHeight) * scale
        return CGRect(
            x: (size.width - width) / 2,
            y: (size.height - height) / 2,
            width: width,
            height: height
        )
    }

    private func drawingGesture(imageRect: CGRect) -> some Gesture {
        DragGesture(
            minimumDistance: 0,
            coordinateSpace: .named(Self.canvasCoordinateSpaceName)
        )
            .onChanged { value in
                let current = imagePoint(from: value.location, imageRect: imageRect)
                if model.draftOperation == nil {
                    let start = imagePoint(from: value.startLocation, imageRect: imageRect)
                    model.beginInteraction(at: start)
                }
                model.updateInteraction(to: current)
            }
            .onEnded { value in
                model.endInteraction(at: imagePoint(from: value.location, imageRect: imageRect))
            }
    }

    private func imagePoint(from location: CGPoint, imageRect: CGRect) -> ScreenshotEditPoint {
        coordinateMapper(for: imageRect).imagePoint(
            from: ScreenshotEditDisplayPoint(x: Double(location.x), y: Double(location.y))
        )
    }

    private func coordinateMapper(for imageRect: CGRect) -> ScreenshotEditCoordinateMapper {
        ScreenshotEditCoordinateMapper(
            displayOriginX: Double(imageRect.minX),
            displayOriginY: Double(imageRect.minY),
            displayWidth: Double(imageRect.width),
            displayHeight: Double(imageRect.height),
            imagePixelWidth: model.pixelWidth,
            imagePixelHeight: model.pixelHeight
        )
    }
}

private struct ScreenshotDraftOverlay: View {
    let operation: ScreenshotEditOperation?
    let mosaicImage: CGImage?
    let imageRect: CGRect
    let pixelWidth: Int
    let pixelHeight: Int

    var body: some View {
        Canvas { context, _ in
            guard let operation else { return }
            switch operation {
            case .freehand(let stroke):
                drawFreehand(stroke, context: &context)
            case .mosaic(let stroke):
                drawMosaic(stroke, context: &context)
            case .rectangle(let annotation):
                drawRectangle(annotation, context: &context)
            case .arrow(let annotation):
                drawArrow(annotation, context: &context)
            }
        }
        .allowsHitTesting(false)
    }

    private func drawFreehand(_ stroke: ScreenshotFreehandStroke, context: inout GraphicsContext) {
        guard let first = stroke.points.first else { return }
        let width = displayLength(stroke.lineWidth)
        if stroke.points.count == 1 {
            let center = displayPoint(first)
            context.fill(
                Path(ellipseIn: CGRect(x: center.x - width / 2, y: center.y - width / 2, width: width, height: width)),
                with: .color(stroke.color.swiftUIColor)
            )
            return
        }

        var path = Path()
        path.move(to: displayPoint(first))
        for point in stroke.points.dropFirst() {
            path.addLine(to: displayPoint(point))
        }
        context.stroke(
            path,
            with: .color(stroke.color.swiftUIColor),
            style: StrokeStyle(lineWidth: width, lineCap: .round, lineJoin: .round)
        )
    }

    private func drawMosaic(_ stroke: ScreenshotMosaicStroke, context: inout GraphicsContext) {
        guard let first = stroke.points.first, let mosaicImage else { return }
        let diameter = displayLength(stroke.brushDiameter)
        let mask: Path
        if stroke.points.count == 1 {
            let center = displayPoint(first)
            mask = Path(ellipseIn: CGRect(
                x: center.x - diameter / 2,
                y: center.y - diameter / 2,
                width: diameter,
                height: diameter
            ))
        } else {
            var path = Path()
            path.move(to: displayPoint(first))
            for point in stroke.points.dropFirst() {
                path.addLine(to: displayPoint(point))
            }
            mask = path.strokedPath(StrokeStyle(
                lineWidth: diameter,
                lineCap: .round,
                lineJoin: .round
            ))
        }

        context.clip(to: mask)
        context.draw(Image(decorative: mosaicImage, scale: 1), in: imageRect)
    }

    private func drawRectangle(
        _ annotation: ScreenshotRectangleAnnotation,
        context: inout GraphicsContext
    ) {
        let start = displayPoint(annotation.start)
        let end = displayPoint(annotation.end)
        let rect = CGRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
        context.stroke(
            Path(rect),
            with: .color(annotation.color.swiftUIColor),
            style: StrokeStyle(
                lineWidth: displayLength(annotation.lineWidth),
                lineCap: .round,
                lineJoin: .round
            )
        )
    }

    private func drawArrow(_ annotation: ScreenshotArrowAnnotation, context: inout GraphicsContext) {
        let start = displayPoint(annotation.start)
        let end = displayPoint(annotation.end)
        let width = displayLength(annotation.lineWidth)
        let dx = end.x - start.x
        let dy = end.y - start.y
        let length = hypot(dx, dy)
        let sourceLength = hypot(
            annotation.end.x - annotation.start.x,
            annotation.end.y - annotation.start.y
        )
        // Match the model and full-resolution renderer: a one-source-pixel
        // arrow remains visible even when the preview is scaled below 1×.
        guard sourceLength >= 1, length > 0 else { return }
        let sourceHeadLength = min(
            max(annotation.lineWidth * 4.5, 12),
            max(12, sourceLength * 0.36)
        )
        let headLength = displayLength(sourceHeadLength)
        let angle = atan2(dy, dx)
        let spread = CGFloat.pi / 6

        var path = Path()
        path.move(to: start)
        path.addLine(to: end)
        path.move(to: CGPoint(
            x: end.x - headLength * cos(angle - spread),
            y: end.y - headLength * sin(angle - spread)
        ))
        path.addLine(to: end)
        path.addLine(to: CGPoint(
            x: end.x - headLength * cos(angle + spread),
            y: end.y - headLength * sin(angle + spread)
        ))
        context.stroke(
            path,
            with: .color(annotation.color.swiftUIColor),
            style: StrokeStyle(lineWidth: width, lineCap: .round, lineJoin: .round)
        )
    }

    private func displayPoint(_ point: ScreenshotEditPoint) -> CGPoint {
        let point = coordinateMapper.displayPoint(from: point)
        return CGPoint(x: point.x, y: point.y)
    }

    private func displayLength(_ pixels: Double) -> CGFloat {
        CGFloat(coordinateMapper.displayLength(fromImagePixels: pixels))
    }

    private var coordinateMapper: ScreenshotEditCoordinateMapper {
        ScreenshotEditCoordinateMapper(
            displayOriginX: Double(imageRect.minX),
            displayOriginY: Double(imageRect.minY),
            displayWidth: Double(imageRect.width),
            displayHeight: Double(imageRect.height),
            imagePixelWidth: pixelWidth,
            imagePixelHeight: pixelHeight
        )
    }
}

private extension ScreenshotEditTool {
    var title: String {
        switch self {
        case .pen: "画笔"
        case .mosaic: "马赛克"
        case .rectangle: "矩形"
        case .arrow: "箭头"
        }
    }

    var systemImage: String {
        switch self {
        case .pen: "pencil.tip"
        case .mosaic: "square.grid.3x3.fill"
        case .rectangle: "rectangle"
        case .arrow: "arrow.up.right"
        }
    }
}

private extension ScreenshotRGBAColor {
    var swiftUIColor: Color {
        Color(.sRGB, red: red, green: green, blue: blue, opacity: alpha)
    }
}
