import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins
import CrossToolCore
import Foundation
import ImageIO

enum ScreenshotEditorRenderError: LocalizedError {
    case unreadableImage
    case unsupportedImageSize
    case bitmapCreationFailed
    case pngEncodingFailed

    var errorDescription: String? {
        switch self {
        case .unreadableImage:
            return "无法读取这张截图"
        case .unsupportedImageSize:
            return "截图尺寸无效或过大"
        case .bitmapCreationFailed:
            return "无法创建图片画布"
        case .pngEncodingFailed:
            return "无法生成 PNG 图片"
        }
    }
}

@MainActor
final class ScreenshotEditorRenderer {
    private static let maximumPixelCount = 50_000_000

    let originalImage: CGImage
    let pixelWidth: Int
    let pixelHeight: Int

    private let ciContext = CIContext(options: [
        .cacheIntermediates: false,
        .useSoftwareRenderer: false
    ])
    // Keep only one full-resolution mosaic. A 6K image can consume tens of
    // megabytes, so caching every slider step would quickly exhaust memory.
    private var cachedMosaic: (pixelSize: Int, image: CGImage)?

    init(imageURL: URL) throws {
        guard let source = CGImageSourceCreateWithURL(imageURL as CFURL, [
            kCGImageSourceShouldCacheImmediately: true
        ] as CFDictionary),
        let image = CGImageSourceCreateImageAtIndex(source, 0, [
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceShouldAllowFloat: false
        ] as CFDictionary) else {
            throw ScreenshotEditorRenderError.unreadableImage
        }

        guard image.width > 0, image.height > 0,
              image.width <= 32_768, image.height <= 32_768,
              image.width * image.height <= Self.maximumPixelCount else {
            throw ScreenshotEditorRenderError.unsupportedImageSize
        }

        originalImage = image
        pixelWidth = image.width
        pixelHeight = image.height
    }

    func render(operations: [ScreenshotEditOperation]) throws -> CGImage {
        guard let context = makeBitmapContext() else {
            throw ScreenshotEditorRenderError.bitmapCreationFailed
        }

        let bounds = CGRect(x: 0, y: 0, width: pixelWidth, height: pixelHeight)
        context.interpolationQuality = .high
        context.draw(originalImage, in: bounds)

        for operation in operations {
            context.saveGState()
            switch operation {
            case .freehand(let stroke):
                drawFreehand(stroke, in: context)
            case .mosaic(let stroke):
                try drawMosaic(stroke, in: context, bounds: bounds)
            case .rectangle(let annotation):
                drawRectangle(annotation, in: context)
            case .arrow(let annotation):
                drawArrow(annotation, in: context)
            }
            context.restoreGState()
        }

        guard let image = context.makeImage() else {
            throw ScreenshotEditorRenderError.bitmapCreationFailed
        }
        return image
    }

    func mosaicImage(pixelSize: Double) throws -> CGImage {
        let normalizedSize = max(4, min(80, Int(pixelSize.rounded())))
        if let cachedMosaic, cachedMosaic.pixelSize == normalizedSize {
            return cachedMosaic.image
        }

        let input = CIImage(cgImage: originalImage)
        let filter = CIFilter.pixellate()
        filter.inputImage = input
        filter.scale = Float(normalizedSize)
        filter.center = CGPoint(x: input.extent.midX, y: input.extent.midY)

        guard let output = filter.outputImage?.cropped(to: input.extent),
              let image = ciContext.createCGImage(output, from: input.extent) else {
            throw ScreenshotEditorRenderError.bitmapCreationFailed
        }
        cachedMosaic = (normalizedSize, image)
        return image
    }

    static func pngData(from image: CGImage) throws -> Data {
        let representation = NSBitmapImageRep(cgImage: image)
        guard let data = representation.representation(using: .png, properties: [:]) else {
            throw ScreenshotEditorRenderError.pngEncodingFailed
        }
        return data
    }

    private func makeBitmapContext() -> CGContext? {
        let colorSpace = originalImage.colorSpace
            ?? CGColorSpace(name: CGColorSpace.sRGB)
            ?? CGColorSpaceCreateDeviceRGB()
        return CGContext(
            data: nil,
            width: pixelWidth,
            height: pixelHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )
    }

    private func drawFreehand(_ stroke: ScreenshotFreehandStroke, in context: CGContext) {
        guard let first = stroke.points.first else { return }
        context.setStrokeColor(stroke.color.cgColor)
        context.setFillColor(stroke.color.cgColor)
        context.setLineWidth(CGFloat(max(1, stroke.lineWidth)))
        context.setLineCap(.round)
        context.setLineJoin(.round)

        if stroke.points.count == 1 {
            let point = canvasPoint(first)
            let radius = CGFloat(max(1, stroke.lineWidth)) / 2
            context.fillEllipse(in: CGRect(
                x: point.x - radius,
                y: point.y - radius,
                width: radius * 2,
                height: radius * 2
            ))
            return
        }

        context.beginPath()
        context.move(to: canvasPoint(first))
        for point in stroke.points.dropFirst() {
            context.addLine(to: canvasPoint(point))
        }
        context.strokePath()
    }

    private func drawMosaic(
        _ stroke: ScreenshotMosaicStroke,
        in context: CGContext,
        bounds: CGRect
    ) throws {
        guard let first = stroke.points.first else { return }
        let diameter = CGFloat(max(2, stroke.brushDiameter))

        if stroke.points.count == 1 {
            let point = canvasPoint(first)
            context.addEllipse(in: CGRect(
                x: point.x - diameter / 2,
                y: point.y - diameter / 2,
                width: diameter,
                height: diameter
            ))
            context.clip()
        } else {
            context.beginPath()
            context.move(to: canvasPoint(first))
            for point in stroke.points.dropFirst() {
                context.addLine(to: canvasPoint(point))
            }
            context.setLineWidth(diameter)
            context.setLineCap(.round)
            context.setLineJoin(.round)
            context.replacePathWithStrokedPath()
            context.clip()
        }

        context.interpolationQuality = .none
        context.draw(try mosaicImage(pixelSize: stroke.pixelSize), in: bounds)
    }

    private func drawRectangle(_ annotation: ScreenshotRectangleAnnotation, in context: CGContext) {
        context.setStrokeColor(annotation.color.cgColor)
        context.setLineWidth(CGFloat(max(1, annotation.lineWidth)))
        context.setLineCap(.round)
        context.setLineJoin(.round)
        context.stroke(rect(from: annotation.start, to: annotation.end))
    }

    private func drawArrow(_ annotation: ScreenshotArrowAnnotation, in context: CGContext) {
        let start = canvasPoint(annotation.start)
        let end = canvasPoint(annotation.end)
        let lineWidth = CGFloat(max(1, annotation.lineWidth))
        let dx = end.x - start.x
        let dy = end.y - start.y
        let length = hypot(dx, dy)
        guard length >= 1 else { return }

        let headLength = min(max(lineWidth * 4.5, 12), max(12, length * 0.36))
        let angle = atan2(dy, dx)
        let spread = CGFloat.pi / 6
        let left = CGPoint(
            x: end.x - headLength * cos(angle - spread),
            y: end.y - headLength * sin(angle - spread)
        )
        let right = CGPoint(
            x: end.x - headLength * cos(angle + spread),
            y: end.y - headLength * sin(angle + spread)
        )

        context.setStrokeColor(annotation.color.cgColor)
        context.setLineWidth(lineWidth)
        context.setLineCap(.round)
        context.setLineJoin(.round)
        context.beginPath()
        context.move(to: start)
        context.addLine(to: end)
        context.move(to: left)
        context.addLine(to: end)
        context.addLine(to: right)
        context.strokePath()
    }

    private func canvasPoint(_ point: ScreenshotEditPoint) -> CGPoint {
        CGPoint(x: point.x, y: Double(pixelHeight) - point.y)
    }

    private func rect(from start: ScreenshotEditPoint, to end: ScreenshotEditPoint) -> CGRect {
        let first = canvasPoint(start)
        let second = canvasPoint(end)
        return CGRect(
            x: min(first.x, second.x),
            y: min(first.y, second.y),
            width: abs(second.x - first.x),
            height: abs(second.y - first.y)
        )
    }
}

private extension ScreenshotRGBAColor {
    var cgColor: CGColor {
        CGColor(
            red: CGFloat(red),
            green: CGFloat(green),
            blue: CGFloat(blue),
            alpha: CGFloat(alpha)
        )
    }
}
