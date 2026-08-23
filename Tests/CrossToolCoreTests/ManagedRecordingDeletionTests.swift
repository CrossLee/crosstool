import Foundation
import Testing
@testable import CrossToolCore

@Test func managedRecordingDeletionRemovesOnlyRequestedFinalizedFile() throws {
    let fixture = try RecordingDeletionFixture()
    defer { fixture.remove() }
    let requested = try fixture.makeRecording(named: "requested.mov")
    let retained = try fixture.makeRecording(named: "retained.mov")

    try ManagedRecordingDeletion.delete(
        recordingURL: requested,
        recordingsDirectory: fixture.recordings
    )

    #expect(!FileManager.default.fileExists(atPath: requested.path))
    #expect(FileManager.default.fileExists(atPath: retained.path))
}

@Test func managedRecordingDeletionRejectsTraversalAndDrafts() throws {
    let fixture = try RecordingDeletionFixture()
    defer { fixture.remove() }
    let outside = fixture.root.appendingPathComponent("outside.mov")
    try Data("outside".utf8).write(to: outside)
    let draft = fixture.drafts.appendingPathComponent("active.partial.mov")
    try Data("draft".utf8).write(to: draft)

    #expect(throws: ManagedRecordingDeletionError.outsideRecordingsDirectory) {
        try ManagedRecordingDeletion.delete(
            recordingURL: outside,
            recordingsDirectory: fixture.recordings
        )
    }
    #expect(throws: ManagedRecordingDeletionError.outsideRecordingsDirectory) {
        try ManagedRecordingDeletion.delete(
            recordingURL: draft,
            recordingsDirectory: fixture.recordings
        )
    }
    #expect(FileManager.default.fileExists(atPath: outside.path))
    #expect(FileManager.default.fileExists(atPath: draft.path))
}

@Test func managedRecordingDeletionRejectsSymbolicLinkWithoutDeletingDestination() throws {
    let fixture = try RecordingDeletionFixture()
    defer { fixture.remove() }
    let outside = fixture.root.appendingPathComponent("private.mov")
    try Data("keep me".utf8).write(to: outside)
    let link = fixture.recordings.appendingPathComponent("linked.mov")
    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)

    #expect(throws: ManagedRecordingDeletionError.symbolicLinkNotAllowed) {
        try ManagedRecordingDeletion.delete(
            recordingURL: link,
            recordingsDirectory: fixture.recordings
        )
    }
    #expect(FileManager.default.fileExists(atPath: link.path))
    #expect(FileManager.default.fileExists(atPath: outside.path))
    #expect(try String(contentsOf: outside, encoding: .utf8) == "keep me")
}

@Test func managedRecordingDeletionRejectsSymlinkedRecordingsDirectory() throws {
    let fixture = try RecordingDeletionFixture(createRecordings: false)
    defer { fixture.remove() }
    let realRecordings = fixture.root.appendingPathComponent("RealRecordings", isDirectory: true)
    try FileManager.default.createDirectory(at: realRecordings, withIntermediateDirectories: true)
    try FileManager.default.createSymbolicLink(
        at: fixture.recordings,
        withDestinationURL: realRecordings
    )
    let recording = fixture.recordings.appendingPathComponent("linked-root.mov")
    try Data("keep me".utf8).write(to: recording)

    #expect(throws: ManagedRecordingDeletionError.symbolicLinkNotAllowed) {
        try ManagedRecordingDeletion.delete(
            recordingURL: recording,
            recordingsDirectory: fixture.recordings
        )
    }
    #expect(FileManager.default.fileExists(atPath: recording.path))
}

@Test func managedRecordingDeletionRejectsDirectoriesDisguisedAsRecordings() throws {
    let fixture = try RecordingDeletionFixture()
    defer { fixture.remove() }
    let directory = fixture.recordings.appendingPathComponent("folder.mov", isDirectory: true)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)

    #expect(throws: ManagedRecordingDeletionError.notARegularFile) {
        try ManagedRecordingDeletion.delete(
            recordingURL: directory,
            recordingsDirectory: fixture.recordings
        )
    }
    #expect(FileManager.default.fileExists(atPath: directory.path))
}

private struct RecordingDeletionFixture {
    let root: URL
    let recordings: URL
    let drafts: URL

    init(createRecordings: Bool = true) throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-recording-delete-\(UUID().uuidString)", isDirectory: true)
        recordings = root.appendingPathComponent("Recordings", isDirectory: true)
        drafts = recordings.appendingPathComponent("Drafts", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        if createRecordings {
            try FileManager.default.createDirectory(at: drafts, withIntermediateDirectories: true)
        }
    }

    func makeRecording(named name: String) throws -> URL {
        let url = recordings.appendingPathComponent(name)
        try Data(name.utf8).write(to: url)
        return url
    }

    func remove() {
        try? FileManager.default.removeItem(at: root)
    }
}
