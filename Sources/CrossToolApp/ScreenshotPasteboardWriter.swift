import AppKit
import Foundation

enum ScreenshotPasteboardError: LocalizedError {
    case invalidPNG
    case writeFailed

    var errorDescription: String? {
        switch self {
        case .invalidPNG:
            return "截图 PNG 数据无效"
        case .writeFailed:
            return "系统剪贴板拒绝写入图片"
        }
    }
}

@MainActor
protocol ScreenshotPasteboardWriting {
    func writePNG(_ pngData: Data, tiffData: Data?) throws
}

/// Writes eager image bytes instead of a file URL so the pasteboard remains
/// usable after Crosio removes its managed screenshot draft.
@MainActor
struct ScreenshotPasteboardWriter: ScreenshotPasteboardWriting {
    private static let pngSignature = Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
    private let pasteboard: NSPasteboard

    init(pasteboard: NSPasteboard = .general) {
        self.pasteboard = pasteboard
    }

    func writePNG(_ pngData: Data, tiffData: Data? = nil) throws {
        // Every input is a Crosio-generated draft. Checking the signature is
        // enough to reject an obviously invalid payload without decoding a
        // potentially 20-megapixel image on the main thread before the editor
        // can appear.
        guard pngData.starts(with: Self.pngSignature) else {
            throw ScreenshotPasteboardError.invalidPNG
        }

        let item = NSPasteboardItem()
        guard item.setData(pngData, forType: .png) else {
            throw ScreenshotPasteboardError.writeFailed
        }
        if let tiffData {
            item.setData(tiffData, forType: .tiff)
        }

        pasteboard.clearContents()
        guard pasteboard.writeObjects([item]) else {
            throw ScreenshotPasteboardError.writeFailed
        }
    }
}
