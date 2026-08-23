import CoreGraphics
import Foundation

enum ScreenshotFrameRenderError: LocalizedError {
    case invalidImage
    case imageTooLarge
    case bitmapCreationFailed

    var errorDescription: String? {
        switch self {
        case .invalidImage:
            return "无法读取要加外框的屏幕截图"
        case .imageTooLarge:
            return "带壳截图尺寸过大"
        case .bitmapCreationFailed:
            return "无法生成带壳截图"
        }
    }
}

/// Renders an original, trademark-free Crosio laptop shell around a display
/// capture. The source pixels are never stretched: the shell is calculated
/// from the source aspect ratio and the screenshot is drawn 1:1 inside it.
enum ScreenshotFrameRenderer {
    private static let maximumPixelCount = 50_000_000

    static func render(screenImage: CGImage) throws -> CGImage {
        guard screenImage.width > 0, screenImage.height > 0 else {
            throw ScreenshotFrameRenderError.invalidImage
        }

        let sourceWidth = screenImage.width
        let sourceHeight = screenImage.height
        let sideBezel = max(22, Int((Double(sourceWidth) * 0.027).rounded()))
        let topBezel = max(24, Int((Double(sourceWidth) * 0.030).rounded()))
        let bottomBezel = max(30, Int((Double(sourceWidth) * 0.040).rounded()))
        let baseHeight = max(42, Int((Double(sourceWidth) * 0.070).rounded()))
        let outerPadding = max(24, Int((Double(sourceWidth) * 0.035).rounded()))

        let lidWidth = sourceWidth + sideBezel * 2
        let lidHeight = sourceHeight + topBezel + bottomBezel
        let canvasWidth = lidWidth + outerPadding * 2
        let canvasHeight = lidHeight + baseHeight + outerPadding * 2

        guard canvasWidth <= 32_768,
              canvasHeight <= 32_768,
              canvasWidth * canvasHeight <= maximumPixelCount else {
            throw ScreenshotFrameRenderError.imageTooLarge
        }

        let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)
            ?? CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: nil,
            width: canvasWidth,
            height: canvasHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw ScreenshotFrameRenderError.bitmapCreationFailed
        }

        context.clear(CGRect(x: 0, y: 0, width: canvasWidth, height: canvasHeight))
        context.setAllowsAntialiasing(true)
        context.setShouldAntialias(true)

        let lidRect = CGRect(
            x: outerPadding,
            y: outerPadding + baseHeight,
            width: lidWidth,
            height: lidHeight
        )
        let screenRect = CGRect(
            x: lidRect.minX + CGFloat(sideBezel),
            y: lidRect.minY + CGFloat(bottomBezel),
            width: CGFloat(sourceWidth),
            height: CGFloat(sourceHeight)
        )

        context.saveGState()
        context.setShadow(
            offset: CGSize(width: 0, height: -CGFloat(max(8, outerPadding / 3))),
            blur: CGFloat(max(18, outerPadding)),
            color: CGColor(gray: 0, alpha: 0.30)
        )
        context.setFillColor(CGColor(red: 0.075, green: 0.082, blue: 0.105, alpha: 1))
        context.addPath(CGPath(
            roundedRect: lidRect,
            cornerWidth: CGFloat(max(18, sideBezel)),
            cornerHeight: CGFloat(max(18, sideBezel)),
            transform: nil
        ))
        context.fillPath()
        context.restoreGState()

        context.saveGState()
        context.addPath(CGPath(
            roundedRect: screenRect,
            cornerWidth: CGFloat(max(7, sideBezel / 3)),
            cornerHeight: CGFloat(max(7, sideBezel / 3)),
            transform: nil
        ))
        context.clip()
        context.interpolationQuality = .high
        context.draw(screenImage, in: screenRect)
        context.restoreGState()

        let cameraRadius = CGFloat(max(2, topBezel / 12))
        context.setFillColor(CGColor(red: 0.19, green: 0.22, blue: 0.28, alpha: 1))
        context.fillEllipse(in: CGRect(
            x: lidRect.midX - cameraRadius,
            y: lidRect.maxY - CGFloat(topBezel) / 2 - cameraRadius,
            width: cameraRadius * 2,
            height: cameraRadius * 2
        ))

        let baseRect = CGRect(
            x: CGFloat(outerPadding) * 0.42,
            y: CGFloat(outerPadding),
            width: CGFloat(canvasWidth) - CGFloat(outerPadding) * 0.84,
            height: CGFloat(baseHeight)
        )
        let baseGradient = CGGradient(
            colorsSpace: colorSpace,
            colors: [
                CGColor(red: 0.84, green: 0.86, blue: 0.90, alpha: 1),
                CGColor(red: 0.56, green: 0.59, blue: 0.65, alpha: 1),
            ] as CFArray,
            locations: [0, 1]
        )

        context.saveGState()
        context.addPath(CGPath(
            roundedRect: baseRect,
            cornerWidth: CGFloat(max(8, baseHeight / 4)),
            cornerHeight: CGFloat(max(8, baseHeight / 4)),
            transform: nil
        ))
        context.clip()
        if let baseGradient {
            context.drawLinearGradient(
                baseGradient,
                start: CGPoint(x: baseRect.midX, y: baseRect.maxY),
                end: CGPoint(x: baseRect.midX, y: baseRect.minY),
                options: []
            )
        }
        context.restoreGState()

        let notchWidth = CGFloat(lidWidth) * 0.16
        let notchRect = CGRect(
            x: CGFloat(canvasWidth) / 2 - notchWidth / 2,
            y: baseRect.maxY - CGFloat(max(5, baseHeight / 8)),
            width: notchWidth,
            height: CGFloat(max(6, baseHeight / 7))
        )
        context.setFillColor(CGColor(red: 0.42, green: 0.45, blue: 0.51, alpha: 0.72))
        context.addPath(CGPath(
            roundedRect: notchRect,
            cornerWidth: notchRect.height / 2,
            cornerHeight: notchRect.height / 2,
            transform: nil
        ))
        context.fillPath()

        guard let rendered = context.makeImage() else {
            throw ScreenshotFrameRenderError.bitmapCreationFailed
        }
        return rendered
    }
}
