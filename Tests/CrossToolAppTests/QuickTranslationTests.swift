import Foundation
import Testing
@testable import CrossToolApp

@Test func quickTranslationDirectionRecognizesChineseEnglishAndMixedText() {
    #expect(
        QuickTranslationDirectionResolver.direction(for: "你好，欢迎使用 Crosio")
            == .chineseToEnglish
    )
    #expect(
        QuickTranslationDirectionResolver.direction(for: "Hello, welcome to Crosio.")
            == .englishToChinese
    )
    #expect(
        QuickTranslationDirectionResolver.direction(for: "Crosio 快捷翻译")
            == .chineseToEnglish
    )
    #expect(QuickTranslationDirectionResolver.direction(for: " 123… ") == nil)
}

@Test func quickTranslationDirectionsUseStableChineseAndEnglishIdentifiers() {
    #expect(QuickTranslationDirection.chineseToEnglish.sourceLanguageIdentifier == "zh-Hans")
    #expect(QuickTranslationDirection.chineseToEnglish.targetLanguageIdentifier == "en")
    #expect(QuickTranslationDirection.englishToChinese.sourceLanguageIdentifier == "en")
    #expect(QuickTranslationDirection.englishToChinese.targetLanguageIdentifier == "zh-Hans")
}

@MainActor
@Test func selectedTextCapturePromptsAndDoesNotReadWithoutAccessibilityPermission() async {
    final class Probe: @unchecked Sendable {
        private let lock = NSLock()
        private var prompts = 0
        private var reads = 0

        func recordPrompt() { lock.withLock { prompts += 1 } }
        func recordRead() { lock.withLock { reads += 1 } }
        var snapshot: (prompts: Int, reads: Int) {
            lock.withLock { (prompts, reads) }
        }
    }

    let probe = Probe()
    let service = SelectedTextCaptureService(
        trustChecker: { false },
        permissionRequester: { probe.recordPrompt() },
        selectionReader: { _ in
            probe.recordRead()
            return .selected("should not be read")
        }
    )

    let result = await service.capture(applicationPID: 42)
    #expect(result == .permissionRequired)
    #expect(probe.snapshot.prompts == 1)
    #expect(probe.snapshot.reads == 0)
}

@available(macOS 15.0, *)
@MainActor
@Test func quickTranslationPreparesInputDirectionAndSupportedPair() async throws {
    let suiteName = "quick-translation-\(UUID().uuidString)"
    let defaults = try #require(UserDefaults(suiteName: suiteName))
    defer { defaults.removePersistentDomain(forName: suiteName) }

    let english = Locale.Language(identifier: "en")
    let chinese = Locale.Language(identifier: "zh-Hans")
    let model = TranslationFeatureModel(
        defaults: defaults,
        supportedLanguageLoader: { [english, chinese] },
        pairAvailabilityLoader: { _, _ in .supported }
    )
    await model.loadSupportedLanguagesIfNeeded()

    let prepared = await model.prepareQuickTranslation(
        text: "Hello from another app.",
        direction: .englishToChinese
    )

    #expect(prepared)
    #expect(model.inputText == "Hello from another app.")
    #expect(model.sourceLanguage?.isEquivalent(to: english) == true)
    #expect(model.targetLanguage?.isEquivalent(to: chinese) == true)
    #expect(model.pairValidationState.status == .supported)
    #expect(model.canTranslate)
}

@available(macOS 15.0, *)
@MainActor
@Test func quickTranslationFallsBackToAnAvailableEnglishRegion() async throws {
    let suiteName = "quick-translation-region-\(UUID().uuidString)"
    let defaults = try #require(UserDefaults(suiteName: suiteName))
    defer { defaults.removePersistentDomain(forName: suiteName) }

    let britishEnglish = Locale.Language(identifier: "en-GB")
    let chinese = Locale.Language(identifier: "zh-Hans")
    let model = TranslationFeatureModel(
        defaults: defaults,
        supportedLanguageLoader: { [britishEnglish, chinese] },
        pairAvailabilityLoader: { _, _ in .supported }
    )
    await model.loadSupportedLanguagesIfNeeded()

    let prepared = await model.prepareQuickTranslation(
        text: "Hello from another app.",
        direction: .englishToChinese
    )

    #expect(prepared)
    #expect(model.sourceLanguage?.minimalIdentifier == britishEnglish.minimalIdentifier)
    #expect(model.targetLanguage?.isEquivalent(to: chinese) == true)
}
