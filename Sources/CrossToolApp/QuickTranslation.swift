@preconcurrency import ApplicationServices
import Foundation

enum QuickTranslationDirection: Equatable, Sendable {
    case chineseToEnglish
    case englishToChinese

    var sourceLanguageIdentifier: String {
        switch self {
        case .chineseToEnglish: return "zh-Hans"
        case .englishToChinese: return "en"
        }
    }

    var targetLanguageIdentifier: String {
        switch self {
        case .chineseToEnglish: return "en"
        case .englishToChinese: return "zh-Hans"
        }
    }
}

enum QuickTranslationDirectionResolver {
    static func direction(for text: String) -> QuickTranslationDirection? {
        var containsChinese = false
        var containsLatinLetter = false

        for scalar in text.unicodeScalars {
            if isHan(scalar.value) {
                containsChinese = true
                break
            }
            if isLatinLetter(scalar.value) {
                containsLatinLetter = true
            }
        }

        if containsChinese {
            return .chineseToEnglish
        }
        if containsLatinLetter {
            return .englishToChinese
        }
        return nil
    }

    private static func isHan(_ value: UInt32) -> Bool {
        switch value {
        case 0x3400 ... 0x4DBF,
             0x4E00 ... 0x9FFF,
             0xF900 ... 0xFAFF,
             0x20000 ... 0x2EBEF,
             0x30000 ... 0x323AF:
            return true
        default:
            return false
        }
    }

    private static func isLatinLetter(_ value: UInt32) -> Bool {
        switch value {
        case 0x0041 ... 0x005A,
             0x0061 ... 0x007A,
             0x00C0 ... 0x024F,
             0x1E00 ... 0x1EFF:
            return true
        default:
            return false
        }
    }
}

struct TextTranslationLaunchRequest: Identifiable, Equatable, Sendable {
    enum Payload: Equatable, Sendable {
        case translate(text: String, direction: QuickTranslationDirection)
        case message(String)
    }

    let id: UUID
    let payload: Payload

    init(id: UUID = UUID(), payload: Payload) {
        self.id = id
        self.payload = payload
    }
}

enum SelectedTextCaptureResult: Equatable, Sendable {
    case selected(String)
    case permissionRequired
    case noSelection
    case unavailable
}

/// Reads the current selection without synthesizing Copy or changing the
/// clipboard. The global shortcut calls this before Crosio is activated, so
/// the focused accessibility element still belongs to the user's foreground
/// application.
@MainActor
struct SelectedTextCaptureService {
    typealias TrustChecker = @MainActor @Sendable () -> Bool
    typealias PermissionRequester = @MainActor @Sendable () -> Void
    typealias SelectionReader = @Sendable (pid_t) -> SelectedTextCaptureResult

    private let trustChecker: TrustChecker
    private let permissionRequester: PermissionRequester
    private let selectionReader: SelectionReader

    init(
        trustChecker: @escaping TrustChecker = { AXIsProcessTrusted() },
        permissionRequester: @escaping PermissionRequester = {
            let promptKey = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
            let options = [promptKey: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
        },
        selectionReader: @escaping SelectionReader = { applicationPID in
            AXSelectedTextReader.read(applicationPID: applicationPID)
        }
    ) {
        self.trustChecker = trustChecker
        self.permissionRequester = permissionRequester
        self.selectionReader = selectionReader
    }

    func capture(
        applicationPID: pid_t?,
        promptForPermission: Bool = true
    ) async -> SelectedTextCaptureResult {
        guard trustChecker() else {
            if promptForPermission {
                permissionRequester()
            }
            return .permissionRequired
        }
        guard let applicationPID else { return .unavailable }

        let selectionReader = self.selectionReader
        let readTask = Task.detached(priority: .userInitiated) {
            selectionReader(applicationPID)
        }
        return await withTaskCancellationHandler {
            await readTask.value
        } onCancel: {
            readTask.cancel()
        }
    }
}

private enum AXSelectedTextReader {
    private static let elementTimeout: Float = 0.2
    private static let overallTimeout: TimeInterval = 1.0

    private struct CandidateResult {
        let text: String?
        let supportsSelection: Bool
    }

    static func read(applicationPID: pid_t) -> SelectedTextCaptureResult {
        let deadline = ProcessInfo.processInfo.systemUptime + overallTimeout
        let applicationElement = AXUIElementCreateApplication(applicationPID)
        AXUIElementSetMessagingTimeout(applicationElement, elementTimeout)
        guard shouldContinue(before: deadline) else { return .unavailable }
        var focusedValue: CFTypeRef?
        let focusedError = AXUIElementCopyAttributeValue(
            applicationElement,
            kAXFocusedUIElementAttribute as CFString,
            &focusedValue
        )
        guard focusedError == .success,
              let focusedValue,
              CFGetTypeID(focusedValue) == AXUIElementGetTypeID() else {
            return .unavailable
        }

        var currentElement = unsafeBitCast(focusedValue, to: AXUIElement.self)
        var foundSelectionCapability = false

        for _ in 0 ..< 8 {
            guard shouldContinue(before: deadline) else { return .unavailable }
            AXUIElementSetMessagingTimeout(currentElement, elementTimeout)
            let candidate = selectionCandidate(from: currentElement, deadline: deadline)
            foundSelectionCapability = foundSelectionCapability || candidate.supportsSelection
            if let text = candidate.text {
                return .selected(text)
            }

            var parentValue: CFTypeRef?
            guard shouldContinue(before: deadline) else { return .unavailable }
            guard AXUIElementCopyAttributeValue(
                currentElement,
                kAXParentAttribute as CFString,
                &parentValue
            ) == .success,
            let parentValue,
            CFGetTypeID(parentValue) == AXUIElementGetTypeID() else {
                break
            }
            currentElement = unsafeBitCast(parentValue, to: AXUIElement.self)
        }

        return foundSelectionCapability ? .noSelection : .unavailable
    }

    private static func selectionCandidate(
        from element: AXUIElement,
        deadline: TimeInterval
    ) -> CandidateResult {
        guard shouldContinue(before: deadline) else {
            return CandidateResult(text: nil, supportsSelection: false)
        }
        var value: CFTypeRef?
        let selectedTextError = AXUIElementCopyAttributeValue(
            element,
            kAXSelectedTextAttribute as CFString,
            &value
        )
        if selectedTextError == .success,
           let text = value as? String,
           let selectedText = nonemptySelection(text) {
            return CandidateResult(text: selectedText, supportsSelection: true)
        }
        let selectedTextSupported = selectedTextError == .success || selectedTextError == .noValue

        let rangeResult = selectedTextUsingRange(from: element, deadline: deadline)
        return CandidateResult(
            text: rangeResult.text,
            supportsSelection: selectedTextSupported || rangeResult.supportsSelection
        )
    }

    private static func shouldContinue(before deadline: TimeInterval) -> Bool {
        let isCancelled = withUnsafeCurrentTask { task in
            task?.isCancelled ?? false
        }
        return !isCancelled && ProcessInfo.processInfo.systemUptime < deadline
    }

    private static func selectedTextUsingRange(
        from element: AXUIElement,
        deadline: TimeInterval
    ) -> CandidateResult {
        guard shouldContinue(before: deadline) else {
            return CandidateResult(text: nil, supportsSelection: false)
        }
        var rangeValue: CFTypeRef?
        let rangeError = AXUIElementCopyAttributeValue(
            element,
            kAXSelectedTextRangeAttribute as CFString,
            &rangeValue
        )
        guard rangeError == .success,
              let rangeValue,
              CFGetTypeID(rangeValue) == AXValueGetTypeID() else {
            return CandidateResult(text: nil, supportsSelection: rangeError == .noValue)
        }

        var selectedRange = CFRange()
        let accessibilityRange = unsafeBitCast(rangeValue, to: AXValue.self)
        guard AXValueGetValue(accessibilityRange, .cfRange, &selectedRange),
              selectedRange.location != kCFNotFound,
              selectedRange.location >= 0 else {
            return CandidateResult(text: nil, supportsSelection: true)
        }
        guard selectedRange.length > 0 else {
            return CandidateResult(text: nil, supportsSelection: true)
        }

        guard shouldContinue(before: deadline) else {
            return CandidateResult(text: nil, supportsSelection: true)
        }
        var parameterizedText: CFTypeRef?
        if AXUIElementCopyParameterizedAttributeValue(
            element,
            kAXStringForRangeParameterizedAttribute as CFString,
            rangeValue,
            &parameterizedText
        ) == .success,
        let text = parameterizedText as? String {
            return CandidateResult(text: nonemptySelection(text), supportsSelection: true)
        }

        guard shouldContinue(before: deadline) else {
            return CandidateResult(text: nil, supportsSelection: true)
        }
        var textValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element,
            kAXValueAttribute as CFString,
            &textValue
        ) == .success,
        let fullText = textValue as? String else {
            return CandidateResult(text: nil, supportsSelection: true)
        }

        let string = fullText as NSString
        let range = NSRange(location: selectedRange.location, length: selectedRange.length)
        guard NSMaxRange(range) <= string.length else {
            return CandidateResult(text: nil, supportsSelection: true)
        }
        return CandidateResult(
            text: nonemptySelection(string.substring(with: range)),
            supportsSelection: true
        )
    }

    private static func nonemptySelection(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : text
    }
}
