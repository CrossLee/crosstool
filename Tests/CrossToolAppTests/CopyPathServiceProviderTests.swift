import AppKit
import Foundation
@testable import CrossToolApp
import Testing

@MainActor
@Suite("Copy path service")
struct CopyPathServiceProviderTests {
    @Test("A file is copied as an absolute path and plain text only")
    func copiesSingleFile() throws {
        try withPasteboards { input, output in
            let path = "/Users/example/Documents/report.pdf"
            try writeURLs([URL(fileURLWithPath: path, isDirectory: false)], to: input)

            let error = invoke(input: input, output: output)

            #expect(error == nil)
            #expect(output.string(forType: .string) == path)
            let textTypes: Set<NSPasteboard.PasteboardType> = [.string, .init("NSStringPboardType")]
            #expect(Set(output.types ?? []).isSubset(of: textTypes))
            #expect(input.pasteboardItems?.first?.string(forType: .fileURL) != nil)
        }
    }

    @Test("A folder is copied without enumerating its contents")
    func copiesSingleFolder() throws {
        try withPasteboards { input, output in
            let path = "/Users/example/Documents/课程资料"
            try writeURLs([URL(fileURLWithPath: path, isDirectory: true)], to: input)

            #expect(invoke(input: input, output: output) == nil)
            #expect(output.string(forType: .string) == path)
        }
    }

    @Test("Mixed files and folders retain selection order, one path per line")
    func copiesMixedSelectionInOrder() throws {
        try withPasteboards { input, output in
            let paths = ["/Users/example/z.pdf", "/Users/example/资料", "/Users/example/a.txt"]
            try writeURLs([
                URL(fileURLWithPath: paths[0], isDirectory: false),
                URL(fileURLWithPath: paths[1], isDirectory: true),
                URL(fileURLWithPath: paths[2], isDirectory: false)
            ], to: input)

            #expect(invoke(input: input, output: output) == nil)
            #expect(output.string(forType: .string) == paths.joined(separator: "\n"))
        }
    }

    @Test("Chinese, spaces, hashes and percent signs are decoded without shell quoting")
    func copiesDecodedCharacters() throws {
        try withPasteboards { input, output in
            let path = "/Users/example/课程 资料/第 1 课 #完成 100%.pdf"
            try writeURLs([URL(fileURLWithPath: path, isDirectory: false)], to: input)

            #expect(invoke(input: input, output: output) == nil)
            #expect(output.string(forType: .string) == path)
        }
    }

    @Test("Paths remain lexical, including a nonexistent symlink-shaped path")
    func doesNotResolveOrRequireFiles() throws {
        try withPasteboards { input, output in
            // No test files are created or read. /tmp must not become
            // /private/tmp, and neither the link nor its target need exist.
            let path = "/tmp/crosio-not-created-\(UUID())/symbolic-link/课件.pdf"
            try writeURLs([URL(fileURLWithPath: path, isDirectory: false)], to: input)

            #expect(invoke(input: input, output: output) == nil)
            #expect(output.string(forType: .string) == path)
        }
    }

    @Test("Legacy filename arrays preserve their order and literal characters")
    func copiesLegacyFilenames() throws {
        try withPasteboards { input, output in
            let paths = ["/Users/example/B # 100%.png", "/Users/example/文件夹", "/Users/example/A.txt"]
            try writeLegacy(paths, to: input)

            #expect(invoke(input: input, output: output) == nil)
            #expect(output.string(forType: .string) == paths.joined(separator: "\n"))
        }
    }

    @Test("Empty and invalid inputs report an error without replacing existing output")
    func rejectsInvalidInputWithoutClearingOutput() throws {
        try withPasteboards { input, output in
            let previous = "已有剪贴板内容"
            #expect(output.setString(previous, forType: .string))
            let previousChangeCount = output.changeCount

            let invalidLegacyLists = [
                [], ["relative.txt"], [""], ["~/Documents/file.txt"],
                ["/valid.txt", "relative.txt"], ["/tmp/invalid\u{0}name"]
            ]
            for paths in invalidLegacyLists {
                input.clearContents()
                try writeLegacy(paths, to: input)
                #expect(invoke(input: input, output: output)?.contains("路径") == true)
                #expect(output.string(forType: .string) == previous)
                #expect(output.changeCount == previousChangeCount)
            }

            input.clearContents()
            #expect(invoke(input: input, output: output) != nil)
            #expect(output.string(forType: .string) == previous)
            #expect(output.changeCount == previousChangeCount)
        }
    }

    @Test("A web URL is not treated as a local file path")
    func rejectsWebURL() throws {
        try withPasteboards { input, output in
            let webURL = try #require(URL(string: "https://example.com/report.pdf"))
            try writeURLs([webURL], to: input)
            #expect(output.setString("keep", forType: .string))

            #expect(invoke(input: input, output: output) != nil)
            #expect(output.string(forType: .string) == "keep")
        }
    }

    @Test("Invalid modern file URLs fail the whole request")
    func rejectsMalformedModernURLs() throws {
        try withPasteboards { input, output in
            #expect(output.setString("keep", forType: .string))
            let previousChangeCount = output.changeCount
            for rawURL in [
                "https://example.com/report.pdf",
                "file:relative.txt",
                "file://remote.example/report.pdf",
                "file:///tmp/report.pdf?query=1",
                "file:///tmp/report.pdf#fragment",
                "file:///tmp/invalid%00name"
            ] {
                input.clearContents()
                let valid = NSPasteboardItem()
                #expect(valid.setString("file:///tmp/valid.pdf", forType: .fileURL))
                let invalid = NSPasteboardItem()
                #expect(invalid.setString(rawURL, forType: .fileURL))
                try #require(input.writeObjects([valid, invalid]))

                #expect(invoke(input: input, output: output) != nil)
                #expect(output.string(forType: .string) == "keep")
                #expect(output.changeCount == previousChangeCount)
            }
        }
    }

    @Test("The provider exposes the exact Services selector and clears stale errors")
    func exposesSelectorAndClearsError() throws {
        try withPasteboards { input, output in
            let provider = CopyPathServiceProvider(outputPasteboard: output)
            #expect(provider.responds(to: NSSelectorFromString("copyPaths:userData:error:")))
            try writeURLs([URL(fileURLWithPath: "/tmp/selected.txt", isDirectory: false)], to: input)
            var error: NSString? = "旧错误"

            provider.copyPaths(input, userData: nil, error: &error)

            #expect(error == nil)
            #expect(output.string(forType: .string) == "/tmp/selected.txt")
        }
    }

    private var legacyType: NSPasteboard.PasteboardType {
        NSPasteboard.PasteboardType("NSFilenamesPboardType")
    }

    private func withPasteboards(_ body: (NSPasteboard, NSPasteboard) throws -> Void) rethrows {
        let input = NSPasteboard(name: .init("crosio-copy-path-input-test-\(UUID())"))
        let output = NSPasteboard(name: .init("crosio-copy-path-output-test-\(UUID())"))
        defer {
            input.releaseGlobally()
            output.releaseGlobally()
        }
        try body(input, output)
    }

    private func writeURLs(_ urls: [URL], to pasteboard: NSPasteboard) throws {
        try #require(pasteboard.writeObjects(urls.map { $0 as NSURL }))
    }

    private func writeLegacy(_ paths: [String], to pasteboard: NSPasteboard) throws {
        try #require(pasteboard.setPropertyList(paths, forType: legacyType))
    }

    private func invoke(input: NSPasteboard, output: NSPasteboard) -> String? {
        var error: NSString?
        CopyPathServiceProvider(outputPasteboard: output)
            .copyPaths(input, userData: nil, error: &error)
        return error as String?
    }
}
