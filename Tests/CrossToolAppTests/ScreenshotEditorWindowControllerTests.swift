import AppKit
@testable import CrossToolApp
import Testing

@MainActor
@Suite("Screenshot editor window presentation", .serialized)
struct ScreenshotEditorWindowControllerTests {
    @Test("Eligible bare S performs once while key repeat is only consumed")
    func eligibleBareSDecision() throws {
        let bareS = try makeKeyEvent()
        let repeatedS = try makeKeyEvent(isARepeat: true)

        #expect(ScreenshotEditorPinShortcut.isBareSKeyDown(bareS))
        #expect(ScreenshotEditorPinShortcut.isBareSKeyDown(repeatedS))
        #expect(ScreenshotEditorPinShortcut.decision(
            for: bareS,
            isKeyWindow: true,
            hasAttachedSheet: false,
            isEditingText: false
        ) == .performPin)
        #expect(ScreenshotEditorPinShortcut.decision(
            for: repeatedS,
            isKeyWindow: true,
            hasAttachedSheet: false,
            isEditingText: false
        ) == .consume)
    }

    @Test("Bare S is ignored outside an unobstructed key editor")
    func editorStateGuardsIgnoreBareS() throws {
        let bareS = try makeKeyEvent()

        #expect(ScreenshotEditorPinShortcut.decision(
            for: bareS,
            isKeyWindow: false,
            hasAttachedSheet: false,
            isEditingText: false
        ) == .ignore)
        #expect(ScreenshotEditorPinShortcut.decision(
            for: bareS,
            isKeyWindow: true,
            hasAttachedSheet: true,
            isEditingText: false
        ) == .ignore)
        #expect(ScreenshotEditorPinShortcut.decision(
            for: bareS,
            isKeyWindow: true,
            hasAttachedSheet: false,
            isEditingText: true
        ) == .ignore)
    }

    @Test("Command S and other non-bare keys remain available to AppKit")
    func modifiedAndNonSKeysAreIgnored() throws {
        let commandS = try makeKeyEvent(modifierFlags: .command)

        #expect(!ScreenshotEditorPinShortcut.isBareSKeyDown(commandS))
        #expect(ScreenshotEditorPinShortcut.decision(
            for: commandS,
            isKeyWindow: true,
            hasAttachedSheet: false,
            isEditingText: false
        ) == .ignore)

        for modifiers: NSEvent.ModifierFlags in [
            .control,
            .option,
            .shift,
            .function,
            [.command, .shift],
        ] {
            let event = try makeKeyEvent(modifierFlags: modifiers)
            #expect(!ScreenshotEditorPinShortcut.isBareSKeyDown(event))
            #expect(ScreenshotEditorPinShortcut.decision(
                for: event,
                isKeyWindow: true,
                hasAttachedSheet: false,
                isEditingText: false
            ) == .ignore)
        }

        let keyUpS = try makeKeyEvent(type: .keyUp)
        let bareA = try makeKeyEvent(characters: "a", keyCode: 0)
        for event in [keyUpS, bareA] {
            #expect(!ScreenshotEditorPinShortcut.isBareSKeyDown(event))
            #expect(ScreenshotEditorPinShortcut.decision(
                for: event,
                isKeyWindow: true,
                hasAttachedSheet: false,
                isEditingText: false
            ) == .ignore)
        }
    }

    @Test("Title-bar pin creates one detached image and closes the normal editor")
    func titleBarPinCreatesDetachedImage() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }

        var pinCount = 0
        var closeCount = 0
        let controller = try ScreenshotEditorWindowController(
            sourceURL: sourceURL,
            onPinImage: { _ in pinCount += 1 },
            onSharePNG: { _ in },
            onClose: { closeCount += 1 }
        )
        let window = try #require(controller.window as? ScreenshotEditorWindow)
        defer {
            window.delegate = nil
            window.close()
        }
        window.orderFront(nil)

        let accessory = try #require(window.titlebarAccessoryViewControllers.first)
        let button = try #require(accessory.view.subviews.compactMap { $0 as? NSButton }.first)
        let buttonCell = try #require(button.cell as? NSButtonCell)

        #expect(window.level == .normal)
        let buttonType = try #require(buttonCell.value(forKey: "buttonType") as? NSNumber)
        #expect(buttonType.intValue == NSButton.ButtonType.momentaryPushIn.rawValue)
        #expect(button.title.isEmpty)
        #expect(button.image != nil)
        #expect(button.imagePosition == .imageOnly)
        #expect(!button.refusesFirstResponder)
        #expect(button.accessibilityRole() == .button)
        #expect(button.accessibilityLabel() == "将当前截图贴到屏幕")
        #expect(button.accessibilityHelp()?.contains("S") == true)
        #expect(button.toolTip == "贴到屏幕（S）")
        #expect(window.onBareSPin != nil)

        button.performClick(nil)

        #expect(pinCount == 1)
        #expect(closeCount == 1)
        #expect(window.level == .normal)
        #expect(!window.isVisible)
    }

    @Test("A failed pin keeps the normal editor window open")
    func failedPinKeepsEditorOpen() throws {
        let sourceURL = try makeTemporaryPNG()
        defer { try? FileManager.default.removeItem(at: sourceURL) }

        var pinCount = 0
        var closeCount = 0
        let controller = try ScreenshotEditorWindowController(
            sourceURL: sourceURL,
            onPinImage: { _ in
                pinCount += 1
                throw CocoaError(.fileWriteUnknown)
            },
            onSharePNG: { _ in },
            onClose: { closeCount += 1 }
        )
        let window = try #require(controller.window)
        defer {
            window.delegate = nil
            window.close()
        }
        window.orderFront(nil)
        let accessory = try #require(window.titlebarAccessoryViewControllers.first)
        let button = try #require(accessory.view.subviews.compactMap { $0 as? NSButton }.first)

        button.performClick(nil)

        #expect(pinCount == 1)
        #expect(closeCount == 0)
        #expect(controller.window === window)
        #expect(window.level == .normal)
        #expect(window.isVisible)
    }

    private func makeTemporaryPNG() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("crosio-window-pin-\(UUID()).png")
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let context = try #require(CGContext(
            data: nil,
            width: 2,
            height: 2,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(NSColor.systemBlue.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        let image = try #require(context.makeImage())
        let bitmap = NSBitmapImageRep(cgImage: image)
        let pngData = try #require(bitmap.representation(using: .png, properties: [:]))
        try pngData.write(to: url, options: .atomic)
        return url
    }

    private func makeKeyEvent(
        type: NSEvent.EventType = .keyDown,
        modifierFlags: NSEvent.ModifierFlags = [],
        characters: String = "s",
        keyCode: UInt16 = 1,
        windowNumber: Int = 0,
        isARepeat: Bool = false
    ) throws -> NSEvent {
        try #require(NSEvent.keyEvent(
            with: type,
            location: .zero,
            modifierFlags: modifierFlags,
            timestamp: 0,
            windowNumber: windowNumber,
            context: nil,
            characters: characters,
            charactersIgnoringModifiers: characters,
            isARepeat: isARepeat,
            keyCode: keyCode
        ))
    }
}
