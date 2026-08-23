import Foundation

public enum ManagedRecordingDeletionError: Error, Equatable, LocalizedError {
    case outsideRecordingsDirectory
    case recordingsDirectoryUnavailable
    case symbolicLinkNotAllowed
    case notARegularFile
    case fileNotFound
    case deletionFailed(Int)

    public var errorDescription: String? {
        switch self {
        case .outsideRecordingsDirectory:
            return "只能删除 Crosio 录屏文件夹中的成片"
        case .recordingsDirectoryUnavailable:
            return "Crosio 录屏文件夹不可用"
        case .symbolicLinkNotAllowed:
            return "为保护本机文件，不能删除符号链接"
        case .notARegularFile:
            return "目标不是可删除的录屏文件"
        case .fileNotFound:
            return "录屏文件已经不存在"
        case .deletionFailed(let code):
            return "系统未能删除录屏文件（错误码 \(code)）"
        }
    }
}

/// Deletes one finalized recording without following links or traversing outside
/// the managed `Recordings` directory. Drafts live in a nested directory and are
/// deliberately ineligible.
public enum ManagedRecordingDeletion {
    public static func delete(
        recordingURL: URL,
        recordingsDirectory: URL,
        fileManager: FileManager = .default
    ) throws {
        let recording = recordingURL.standardizedFileURL
        let directory = recordingsDirectory.standardizedFileURL

        guard recording != directory,
              recording.deletingLastPathComponent() == directory else {
            throw ManagedRecordingDeletionError.outsideRecordingsDirectory
        }

        let directoryType = try fileType(
            at: directory,
            missingError: .recordingsDirectoryUnavailable,
            fileManager: fileManager
        )
        if directoryType == .typeSymbolicLink {
            throw ManagedRecordingDeletionError.symbolicLinkNotAllowed
        }
        guard directoryType == .typeDirectory else {
            throw ManagedRecordingDeletionError.recordingsDirectoryUnavailable
        }

        let recordingType = try fileType(
            at: recording,
            missingError: .fileNotFound,
            fileManager: fileManager
        )
        if recordingType == .typeSymbolicLink {
            throw ManagedRecordingDeletionError.symbolicLinkNotAllowed
        }
        guard recordingType == .typeRegular else {
            throw ManagedRecordingDeletionError.notARegularFile
        }

        // This catches a link in the directory chain without following a file
        // link. Both URLs may share harmless system-level resolved ancestors.
        let resolvedDirectory = directory.resolvingSymlinksInPath().standardizedFileURL
        let resolvedRecording = recording.resolvingSymlinksInPath().standardizedFileURL
        guard resolvedRecording.deletingLastPathComponent() == resolvedDirectory else {
            throw ManagedRecordingDeletionError.symbolicLinkNotAllowed
        }

        do {
            try fileManager.removeItem(at: recording)
        } catch let error as NSError {
            throw ManagedRecordingDeletionError.deletionFailed(error.code)
        }
    }

    private static func fileType(
        at url: URL,
        missingError: ManagedRecordingDeletionError,
        fileManager: FileManager
    ) throws -> FileAttributeType {
        do {
            let attributes = try fileManager.attributesOfItem(atPath: url.path)
            guard let type = attributes[.type] as? FileAttributeType else {
                throw missingError
            }
            return type
        } catch let error as ManagedRecordingDeletionError {
            throw error
        } catch let error as NSError {
            if error.domain == NSCocoaErrorDomain,
               error.code == NSFileNoSuchFileError
                || error.code == NSFileReadNoSuchFileError {
                throw missingError
            }
            throw error
        }
    }
}
