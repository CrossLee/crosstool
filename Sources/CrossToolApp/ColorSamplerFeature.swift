import AppKit
import CrossToolCore
import SwiftUI

@MainActor
protocol ScreenColorSampling: AnyObject {
    func sampleColor() async -> NSColor?
}

/// Compatibility fallback for Macs where the real-time ScreenCaptureKit
/// sampler is unavailable. This does not retain the screen and therefore does
/// not require Screen Recording access.
@MainActor
final class NativeScreenColorSampler: ScreenColorSampling {
    func sampleColor() async -> NSColor? {
        await NSColorSampler().sample()
    }
}

extension ScreenSampledColor {
    init?(appKitColor: NSColor) {
        guard let color = appKitColor.usingColorSpace(.sRGB) else { return nil }

        var red: CGFloat = 0
        var green: CGFloat = 0
        var blue: CGFloat = 0
        var alpha: CGFloat = 0
        color.getRed(&red, green: &green, blue: &blue, alpha: &alpha)

        self.init(
            sRGBRed: Double(red),
            green: Double(green),
            blue: Double(blue),
            alpha: Double(alpha)
        )
    }

    var appKitColor: NSColor {
        NSColor(
            srgbRed: CGFloat(red) / 255,
            green: CGFloat(green) / 255,
            blue: CGFloat(blue) / 255,
            alpha: CGFloat(alpha) / 255
        )
    }
}

@MainActor
final class ColorSamplerViewModel: ObservableObject {
    @Published private(set) var selectedColor: ScreenSampledColor?
    @Published private(set) var recentColors: [ScreenSampledColor]
    @Published private(set) var isSampling = false
    @Published private(set) var statusMessage: String?
    @Published var selectedFormat: ScreenColorTextFormat {
        didSet {
            defaults.set(selectedFormat.rawValue, forKey: Self.formatDefaultsKey)
        }
    }

    private static let recentColorsDefaultsKey = "colorSampler.recentColors.v1"
    private static let formatDefaultsKey = "colorSampler.copyFormat.v1"
    private static let maximumRecentColorCount = 12

    private let sampler: any ScreenColorSampling
    private let defaults: UserDefaults

    init(
        defaults: UserDefaults = .standard,
        sampler: (any ScreenColorSampling)? = nil
    ) {
        self.defaults = defaults
        self.sampler = sampler ?? RealtimeScreenColorSampler()

        if let rawFormat = defaults.string(forKey: Self.formatDefaultsKey),
           let format = ScreenColorTextFormat(rawValue: rawFormat) {
            selectedFormat = format
        } else {
            selectedFormat = .hex
        }

        if let data = defaults.data(forKey: Self.recentColorsDefaultsKey),
           let decoded = try? JSONDecoder().decode([ScreenSampledColor].self, from: data) {
            recentColors = Array(decoded.prefix(Self.maximumRecentColorCount))
        } else {
            recentColors = []
        }
        selectedColor = recentColors.first
    }

    @discardableResult
    func beginSampling() async -> ScreenSampledColor? {
        guard !isSampling else { return nil }
        isSampling = true
        statusMessage = "移动鼠标查看实时颜色，点击确认，按 Esc 可取消"

        let sampledColor = await sampler.sampleColor()
        isSampling = false

        guard let sampledColor else {
            statusMessage = "已取消取色"
            return nil
        }
        guard let color = ScreenSampledColor(appKitColor: sampledColor) else {
            statusMessage = "无法将所选颜色转换为标准 sRGB"
            return nil
        }

        select(color, addsToHistory: true)
        statusMessage = "已提取 \(color.hexText)"
        return color
    }

    /// Samples a fresh color and copies its HEX value. A cancelled sampling
    /// session never falls back to (and therefore never copies) an older color.
    @discardableResult
    func sampleAndCopyHex() async -> ScreenSampledColor? {
        guard let color = await beginSampling() else { return nil }
        copy(color, as: .hex)
        return color
    }

    func selectRecentColor(_ color: ScreenSampledColor) {
        select(color, addsToHistory: false)
        statusMessage = "已选择 \(color.hexText)"
    }

    func copySelectedColor() {
        guard let selectedColor else { return }
        copy(selectedColor, as: selectedFormat)
    }

    func copy(_ color: ScreenSampledColor, as format: ScreenColorTextFormat) {
        let value = color.text(for: format)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        guard pasteboard.setString(value, forType: .string) else {
            statusMessage = "复制失败，请重试"
            return
        }
        statusMessage = "已复制 \(value)"
    }

    func clearRecentColors() {
        recentColors = []
        selectedColor = nil
        defaults.removeObject(forKey: Self.recentColorsDefaultsKey)
        statusMessage = "已清空最近颜色"
    }

    private func select(_ color: ScreenSampledColor, addsToHistory: Bool) {
        selectedColor = color
        guard addsToHistory else { return }

        recentColors.removeAll { $0 == color }
        recentColors.insert(color, at: 0)
        if recentColors.count > Self.maximumRecentColorCount {
            recentColors.removeLast(recentColors.count - Self.maximumRecentColorCount)
        }
        persistRecentColors()
    }

    private func persistRecentColors() {
        guard let data = try? JSONEncoder().encode(recentColors) else { return }
        defaults.set(data, forKey: Self.recentColorsDefaultsKey)
    }
}

/// A self-contained page that can be placed directly in Crosio's sidebar.
@MainActor
struct ColorSamplerPage: View {
    @EnvironmentObject private var appModel: AppModel
    @ObservedObject private var model: ColorSamplerViewModel

    init(model: ColorSamplerViewModel) {
        self.model = model
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("提取颜色")
                        .font(.system(size: 28, weight: .semibold))
                    Text("实时预览屏幕像素并提取标准 sRGB 颜色，可复制 HEX、RGB 或 HSL")
                        .foregroundStyle(.secondary)
                }

                selectionCard
                recentColorsCard
            }
            .padding(30)
        }
    }

    private var selectionCard: some View {
        HStack(alignment: .top, spacing: 24) {
            colorPreview
                .frame(width: 176, height: 176)

            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(model.selectedColor == nil ? "尚未取色" : "当前颜色")
                            .font(.headline)
                        Text(model.statusMessage ?? "开始后，鼠标旁会实时显示放大像素、色块与色值")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button {
                        appModel.beginColorSampling()
                    } label: {
                        Label(model.isSampling ? "正在取色…" : "开始取色", systemImage: "eyedropper")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.isSampling)

                    Text(appModel.shortcutLabel(for: .pickColor))
                        .font(.system(.caption, design: .monospaced, weight: .semibold))
                        .foregroundStyle(Color.crossToolAccent)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 5)
                        .background(Color.crossToolAccent.opacity(0.09))
                        .clipShape(Capsule())
                }

                if let color = model.selectedColor {
                    Picker("复制格式", selection: $model.selectedFormat) {
                        ForEach(ScreenColorTextFormat.allCases) { format in
                            Text(format.title).tag(format)
                        }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()

                    HStack(spacing: 10) {
                        Text(color.text(for: model.selectedFormat))
                            .font(.system(.title3, design: .monospaced, weight: .medium))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        Button("复制", systemImage: "doc.on.doc") {
                            model.copySelectedColor()
                        }
                        .keyboardShortcut("c", modifiers: [.command, .shift])
                    }
                    .padding(12)
                    .background(Color.secondary.opacity(0.08))
                    .clipShape(RoundedRectangle(cornerRadius: 10))

                    VStack(spacing: 0) {
                        ForEach(ScreenColorTextFormat.allCases) { format in
                            ColorValueRow(
                                title: format.title,
                                value: color.text(for: format)
                            ) {
                                model.copy(color, as: format)
                            }
                            if format != ScreenColorTextFormat.allCases.last {
                                Divider()
                            }
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(22)
        .background(.background)
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .stroke(Color.primary.opacity(0.11), lineWidth: 1)
        }
    }

    @ViewBuilder
    private var colorPreview: some View {
        if let color = model.selectedColor {
            RoundedRectangle(cornerRadius: 18)
                .fill(Color(nsColor: color.appKitColor))
                .overlay(alignment: .bottomLeading) {
                    Text(color.hexText)
                        .font(.system(.subheadline, design: .monospaced, weight: .semibold))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .background(.regularMaterial, in: Capsule())
                        .padding(12)
                }
                .overlay {
                    RoundedRectangle(cornerRadius: 18)
                        .stroke(Color.primary.opacity(0.16), lineWidth: 1)
                }
        } else {
            RoundedRectangle(cornerRadius: 18)
                .fill(Color.secondary.opacity(0.07))
                .overlay {
                    VStack(spacing: 10) {
                        Image(systemName: "eyedropper")
                            .font(.system(size: 34, weight: .light))
                        Text("等待取色")
                            .font(.subheadline)
                    }
                    .foregroundStyle(.secondary)
                }
                .overlay {
                    RoundedRectangle(cornerRadius: 18)
                        .stroke(Color.secondary.opacity(0.3), style: StrokeStyle(lineWidth: 1, dash: [6, 5]))
                }
        }
    }

    private var recentColorsCard: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("最近颜色")
                        .font(.headline)
                    Text("最多保留 12 个，退出 Crosio 后仍会保留")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if !model.recentColors.isEmpty {
                    Button("清空", role: .destructive) {
                        model.clearRecentColors()
                    }
                }
            }

            if model.recentColors.isEmpty {
                HStack(spacing: 10) {
                    Image(systemName: "clock")
                    Text("提取的颜色会显示在这里")
                }
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, minHeight: 72, alignment: .leading)
            } else {
                LazyVGrid(
                    columns: [GridItem(.adaptive(minimum: 112), spacing: 12)],
                    spacing: 12
                ) {
                    ForEach(model.recentColors) { color in
                        Button {
                            model.selectRecentColor(color)
                        } label: {
                            VStack(alignment: .leading, spacing: 8) {
                                RoundedRectangle(cornerRadius: 8)
                                    .fill(Color(nsColor: color.appKitColor))
                                    .frame(height: 54)
                                    .overlay {
                                        RoundedRectangle(cornerRadius: 8)
                                            .stroke(Color.primary.opacity(0.14), lineWidth: 1)
                                    }
                                Text(color.hexText)
                                    .font(.system(.caption, design: .monospaced))
                                    .foregroundStyle(.primary)
                            }
                            .padding(8)
                            .background(Color.secondary.opacity(0.055))
                            .clipShape(RoundedRectangle(cornerRadius: 11))
                        }
                        .buttonStyle(.plain)
                        .contextMenu {
                            ForEach(ScreenColorTextFormat.allCases) { format in
                                Button("复制 \(format.title)") {
                                    model.copy(color, as: format)
                                }
                            }
                        }
                        .accessibilityLabel("最近颜色 \(color.hexText)")
                    }
                }
            }
        }
        .padding(20)
        .background(.background)
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .stroke(Color.primary.opacity(0.11), lineWidth: 1)
        }
    }
}

private struct ColorValueRow: View {
    let title: String
    let value: String
    let copy: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .frame(width: 36, alignment: .leading)
            Text(value)
                .font(.system(.subheadline, design: .monospaced))
                .textSelection(.enabled)
            Spacer()
            Button("复制 \(title)", systemImage: "doc.on.doc", action: copy)
                .labelStyle(.iconOnly)
                .help("复制 \(title)")
        }
        .padding(.vertical, 8)
    }
}
