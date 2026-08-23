import Foundation

public enum SharedContentStoreError: LocalizedError {
    case notARegularFile
    case fileTooLarge
    case emptyText
    case itemNotFound

    public var errorDescription: String? {
        switch self {
        case .notARegularFile:
            return "只能分享普通文件"
        case .fileTooLarge:
            return "文件超过当前版本的 256 MB 上传限制"
        case .emptyText:
            return "文字内容不能为空"
        case .itemNotFound:
            return "共享内容不存在或已经移除"
        }
    }
}

public final class SharedContentStore: @unchecked Sendable {
    public static let maximumUploadBytes = 256 * 1024 * 1024

    private let lock = NSLock()
    private let inboxIOLock = NSLock()
    private var outgoingItems: [SharedItem] = []
    private var incomingItems: [SharedItem] = []
    private var hiddenIncomingItemIDs: Set<UUID> = []
    private var changeHandler: (@Sendable () -> Void)?

    public let inboxDirectory: URL

    public init(inboxDirectory: URL) throws {
        self.inboxDirectory = inboxDirectory
        try FileManager.default.createDirectory(
            at: inboxDirectory,
            withIntermediateDirectories: true
        )
    }

    public func setChangeHandler(_ handler: (@Sendable () -> Void)?) {
        lock.lock()
        changeHandler = handler
        lock.unlock()
    }

    @discardableResult
    public func addSharedFile(at url: URL) throws -> SharedItem {
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
        guard values.isRegularFile == true else {
            throw SharedContentStoreError.notARegularFile
        }

        let item = SharedItem(
            kind: MIMEType.kind(forFileAt: url),
            direction: .outgoing,
            title: url.lastPathComponent,
            fileURL: url,
            byteCount: values.fileSize.map(Int64.init),
            mimeType: MIMEType.forFile(at: url)
        )
        mutate {
            outgoingItems.insert(item, at: 0)
        }
        return item
    }

    @discardableResult
    public func addSharedText(_ text: String) throws -> SharedItem {
        let cleaned = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty else {
            throw SharedContentStoreError.emptyText
        }
        let item = SharedItem(
            kind: .text,
            direction: .outgoing,
            title: cleaned.count > 48 ? String(cleaned.prefix(48)) + "…" : cleaned,
            detail: cleaned,
            byteCount: Int64(cleaned.utf8.count),
            mimeType: "text/plain; charset=utf-8"
        )
        mutate {
            outgoingItems.insert(item, at: 0)
        }
        return item
    }

    @discardableResult
    public func receiveFile(
        data: Data,
        filename: String,
        remoteAddress: String?
    ) throws -> SharedItem {
        guard data.count <= Self.maximumUploadBytes else {
            throw SharedContentStoreError.fileTooLarge
        }

        let safeName = Self.sanitizeFilename(filename)
        let destination = try inboxIOLock.withLock {
            let destination = uniqueDestinationURL(for: safeName)
            try data.write(to: destination, options: .atomic)
            return destination
        }

        let item = SharedItem(
            kind: MIMEType.kind(forFileAt: destination),
            direction: .incoming,
            title: destination.lastPathComponent,
            fileURL: destination,
            byteCount: Int64(data.count),
            mimeType: MIMEType.forFile(at: destination),
            remoteAddress: remoteAddress
        )
        mutate {
            hiddenIncomingItemIDs.remove(item.id)
            incomingItems.insert(item, at: 0)
        }
        return item
    }

    @discardableResult
    public func receiveText(_ text: String, remoteAddress: String?) throws -> SharedItem {
        let cleaned = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty else {
            throw SharedContentStoreError.emptyText
        }
        let item = SharedItem(
            kind: .text,
            direction: .incoming,
            title: cleaned.count > 48 ? String(cleaned.prefix(48)) + "…" : cleaned,
            detail: cleaned,
            byteCount: Int64(cleaned.utf8.count),
            mimeType: "text/plain; charset=utf-8",
            remoteAddress: remoteAddress
        )
        mutate {
            hiddenIncomingItemIDs.remove(item.id)
            incomingItems.insert(item, at: 0)
        }
        return item
    }

    public func outgoingSnapshot() -> [SharedItem] {
        lock.withLock { outgoingItems }
    }

    public func incomingSnapshot() -> [SharedItem] {
        lock.withLock { incomingItems }
    }

    public func publicSnapshot() -> [SharedItem] {
        lock.withLock {
            let visibleIncoming = incomingItems.filter { !hiddenIncomingItemIDs.contains($0.id) }
            return (outgoingItems + visibleIncoming).sorted { $0.createdAt > $1.createdAt }
        }
    }

    public func publicItem(id: UUID) -> SharedItem? {
        lock.withLock {
            outgoingItems.first(where: { $0.id == id })
                ?? incomingItems.first(where: {
                    $0.id == id && !hiddenIncomingItemIDs.contains($0.id)
                })
        }
    }

    public func removeOutgoing(id: UUID) {
        mutate {
            outgoingItems.removeAll(where: { $0.id == id })
        }
    }

    /// Removes every outgoing entry that points at the specified local file.
    /// This keeps the classroom list from retaining a broken download after a
    /// managed recording is deleted from disk.
    @discardableResult
    public func removeOutgoingFiles(at url: URL) -> Int {
        let normalizedURL = url.standardizedFileURL
        var removedCount = 0
        mutate {
            let originalCount = outgoingItems.count
            outgoingItems.removeAll { item in
                item.fileURL?.standardizedFileURL == normalizedURL
            }
            removedCount = originalCount - outgoingItems.count
        }
        return removedCount
    }

    public func hideIncomingFromPublic(id: UUID) {
        mutate {
            guard incomingItems.contains(where: { $0.id == id }) else { return }
            hiddenIncomingItemIDs.insert(id)
        }
    }

    public func clearOutgoing() {
        mutate {
            outgoingItems.removeAll()
        }
    }

    public func clearPublicItems() {
        mutate {
            outgoingItems.removeAll()
            hiddenIncomingItemIDs.formUnion(incomingItems.map(\.id))
        }
    }

    public func clearIncoming(deleteFiles: Bool = false) {
        let files: [URL] = lock.withLock {
            let urls = incomingItems.compactMap(\.fileURL)
            incomingItems.removeAll()
            hiddenIncomingItemIDs.removeAll()
            return urls
        }
        if deleteFiles {
            for url in files {
                try? FileManager.default.removeItem(at: url)
            }
        }
        notifyChange()
    }

    public static func sanitizeFilename(_ filename: String) -> String {
        let lastComponent = (filename as NSString).lastPathComponent
        let invalid = CharacterSet(charactersIn: "/\\:\0").union(.controlCharacters)
        let components = lastComponent.components(separatedBy: invalid)
        var result = components.joined(separator: "_")
            .trimmingCharacters(in: .whitespacesAndNewlines)

        if result.isEmpty || result == "." || result == ".." {
            result = "未命名文件"
        }
        if result.count > 180 {
            let extensionPart = (result as NSString).pathExtension
            let stem = (result as NSString).deletingPathExtension
            let suffix = extensionPart.isEmpty ? "" : ".\(extensionPart)"
            result = String(stem.prefix(max(1, 180 - suffix.count))) + suffix
        }
        return result
    }

    private func uniqueDestinationURL(for filename: String) -> URL {
        let original = inboxDirectory.appendingPathComponent(filename)
        guard FileManager.default.fileExists(atPath: original.path) else {
            return original
        }

        let extensionPart = (filename as NSString).pathExtension
        let stem = (filename as NSString).deletingPathExtension
        for index in 2...9_999 {
            let candidateName = extensionPart.isEmpty
                ? "\(stem) \(index)"
                : "\(stem) \(index).\(extensionPart)"
            let candidate = inboxDirectory.appendingPathComponent(candidateName)
            if !FileManager.default.fileExists(atPath: candidate.path) {
                return candidate
            }
        }
        return inboxDirectory.appendingPathComponent("\(UUID().uuidString)-\(filename)")
    }

    private func mutate(_ body: () -> Void) {
        lock.lock()
        body()
        lock.unlock()
        notifyChange()
    }

    private func notifyChange() {
        let handler = lock.withLock { changeHandler }
        handler?()
    }
}

private extension NSLock {
    func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try body()
    }
}
