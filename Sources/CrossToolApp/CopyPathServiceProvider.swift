import AppKit
import Foundation

/// Copies only the paths supplied by a Services request, without opening or
/// resolving the selected files and without changing Crosio's window state.
@MainActor
final class CopyPathServiceProvider: NSObject {
    private static let legacyFilenamesType = NSPasteboard.PasteboardType("NSFilenamesPboardType")

    private let outputPasteboard: NSPasteboard

    init(outputPasteboard: NSPasteboard = .general) {
        self.outputPasteboard = outputPasteboard
        super.init()
    }

    @objc(copyPaths:userData:error:)
    func copyPaths(
        _ pasteboard: NSPasteboard,
        userData: String?,
        error: AutoreleasingUnsafeMutablePointer<NSString?>
    ) {
        error.pointee = nil

        do {
            let paths = try Self.paths(from: pasteboard)
            let item = NSPasteboardItem()
            guard item.setString(paths.joined(separator: "\n"), forType: .string) else {
                throw CopyPathError.writeFailed
            }

            // Validate and materialize every path before replacing the user's
            // clipboard. The Services input pasteboard is not our output.
            outputPasteboard.clearContents()
            guard outputPasteboard.writeObjects([item]) else {
                throw CopyPathError.writeFailed
            }
        } catch let copyError {
            error.pointee = copyError.localizedDescription as NSString
        }
    }

    private static func paths(from pasteboard: NSPasteboard) throws -> [String] {
        let fileItems = (pasteboard.pasteboardItems ?? []).filter {
            $0.types.contains(.fileURL)
        }

        if !fileItems.isEmpty {
            return try fileItems.map { item in
                guard let string = item.string(forType: .fileURL),
                      let url = URL(string: string),
                      url.isFileURL,
                      url.host == nil || url.host?.isEmpty == true || url.host == "localhost",
                      url.user == nil,
                      url.password == nil,
                      url.port == nil,
                      url.query == nil,
                      url.fragment == nil,
                      let decodedPath = url.path(percentEncoded: true).removingPercentEncoding,
                      isAbsolutePath(decodedPath),
                      isAbsolutePath(url.path) else {
                    throw CopyPathError.invalidPath
                }
                // Do not standardize or resolve symlinks: users asked for the
                // selected item's path, not the path of its eventual target.
                return url.path
            }
        }

        // Read legacy strings directly. NSURL's compatibility reader can turn
        // a relative legacy filename into an absolute path using the app's cwd.
        guard let paths = pasteboard.propertyList(forType: legacyFilenamesType) as? [String],
              !paths.isEmpty else {
            throw CopyPathError.noSelection
        }
        guard paths.allSatisfy(isAbsolutePath) else {
            throw CopyPathError.invalidPath
        }
        return paths
    }

    private static func isAbsolutePath(_ path: String) -> Bool {
        path.hasPrefix("/") && !path.utf8.contains(0)
    }
}

private enum CopyPathError: LocalizedError {
    case noSelection
    case invalidPath
    case writeFailed

    var errorDescription: String? {
        switch self {
        case .noSelection:
            return "请先在 Finder 中选中文件或文件夹，再复制路径。"
        case .invalidPath:
            return "无法复制路径：所选项目包含无效的本地绝对路径。"
        case .writeFailed:
            return "系统剪贴板拒绝写入路径，请重试。"
        }
    }
}
