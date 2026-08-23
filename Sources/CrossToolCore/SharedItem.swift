import Foundation
import UniformTypeIdentifiers

public enum SharedItemKind: String, Codable, Sendable {
    case file
    case image
    case text
}

public enum SharedItemDirection: String, Codable, Sendable {
    case outgoing
    case incoming
}

public struct SharedItem: Identifiable, Codable, Equatable, Sendable {
    public let id: UUID
    public let kind: SharedItemKind
    public let direction: SharedItemDirection
    public let title: String
    public let detail: String?
    public let fileURL: URL?
    public let byteCount: Int64?
    public let mimeType: String
    public let createdAt: Date
    public let remoteAddress: String?

    public init(
        id: UUID = UUID(),
        kind: SharedItemKind,
        direction: SharedItemDirection,
        title: String,
        detail: String? = nil,
        fileURL: URL? = nil,
        byteCount: Int64? = nil,
        mimeType: String,
        createdAt: Date = Date(),
        remoteAddress: String? = nil
    ) {
        self.id = id
        self.kind = kind
        self.direction = direction
        self.title = title
        self.detail = detail
        self.fileURL = fileURL
        self.byteCount = byteCount
        self.mimeType = mimeType
        self.createdAt = createdAt
        self.remoteAddress = remoteAddress
    }
}
public enum MIMEType {
    public static func forFile(at url: URL) -> String {
        guard !url.pathExtension.isEmpty,
              let type = UTType(filenameExtension: url.pathExtension),
              let mimeType = type.preferredMIMEType else {
            return "application/octet-stream"
        }
        return mimeType
    }

    public static func kind(forFileAt url: URL) -> SharedItemKind {
        guard !url.pathExtension.isEmpty,
              let type = UTType(filenameExtension: url.pathExtension) else {
            return .file
        }
        return type.conforms(to: .image) ? .image : .file
    }
}
