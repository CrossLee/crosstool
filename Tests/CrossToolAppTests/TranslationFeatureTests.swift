import Foundation
import Testing
@preconcurrency import Translation
@testable import CrossToolApp

@available(macOS 15.0, *)
@MainActor
@Test func translationModelRejectsLateResponsesAndKeepsTheLatestRequest() throws {
    let suiteName = "translation-model-\(UUID().uuidString)"
    let defaults = try #require(UserDefaults(suiteName: suiteName))
    defer { defaults.removePersistentDomain(forName: suiteName) }

    let model = TranslationFeatureModel(defaults: defaults)
    let english = Locale.Language(identifier: "en")
    let chinese = Locale.Language(identifier: "zh-Hans")
    model.selectTargetLanguage(english)
    model.updateInput("第一段")
    #expect(model.beginTranslation())
    let firstRequest = try #require(model.currentRequest())

    model.updateInput("第二段")
    #expect(model.beginTranslation())
    let secondRequest = try #require(model.currentRequest())
    #expect(firstRequest.id != secondRequest.id)
    #expect(!model.isCurrentRequest(firstRequest.id))
    #expect(model.isCurrentRequest(secondRequest.id))

    model.complete(
        TranslationSession.Response(
            sourceLanguage: chinese,
            targetLanguage: english,
            sourceText: "第一段",
            targetText: "First"
        ),
        requestID: firstRequest.id
    )
    #expect(model.outputText.isEmpty)
    #expect(model.currentRequest()?.id == secondRequest.id)

    model.complete(
        TranslationSession.Response(
            sourceLanguage: chinese,
            targetLanguage: english,
            sourceText: "第二段",
            targetText: "Second"
        ),
        requestID: secondRequest.id
    )
    #expect(model.outputText == "Second")
    #expect(model.history.count == 1)
}

@available(macOS 15.0, *)
@MainActor
@Test func translationHistoryPersistsLocallyAndCapsAtOneHundredItems() throws {
    let suiteName = "translation-history-\(UUID().uuidString)"
    let defaults = try #require(UserDefaults(suiteName: suiteName))
    defer { defaults.removePersistentDomain(forName: suiteName) }

    let english = Locale.Language(identifier: "en")
    let chinese = Locale.Language(identifier: "zh-Hans")
    let model = TranslationFeatureModel(defaults: defaults)
    model.selectTargetLanguage(english)

    for index in 0 ... TranslationFeatureModel.maximumHistoryCount {
        let source = "原文 \(index)"
        model.updateInput(source)
        #expect(model.beginTranslation())
        let request = try #require(model.currentRequest())
        model.complete(
            TranslationSession.Response(
                sourceLanguage: chinese,
                targetLanguage: english,
                sourceText: source,
                targetText: "Result \(index)"
            ),
            requestID: request.id
        )
    }

    #expect(model.history.count == TranslationFeatureModel.maximumHistoryCount)
    #expect(model.history.first?.translatedText == "Result 100")
    #expect(model.history.last?.translatedText == "Result 1")

    let restored = TranslationFeatureModel(defaults: defaults)
    #expect(restored.history == model.history)
}

@Test func translationPairValidationRejectsLateResults() throws {
    let chineseToEnglish = TranslationLanguagePair(
        source: Locale.Language(identifier: "zh-Hans"),
        target: Locale.Language(identifier: "en")
    )
    let englishToJapanese = TranslationLanguagePair(
        source: Locale.Language(identifier: "en"),
        target: Locale.Language(identifier: "ja")
    )

    var state = TranslationPairValidationState()
    let firstRequestCandidate = state.beginChecking(chineseToEnglish)
    let firstRequest = try #require(firstRequestCandidate)
    let secondRequestCandidate = state.beginChecking(englishToJapanese)
    let secondRequest = try #require(secondRequestCandidate)

    let acceptedLateResult = state.apply(.unsupported, requestID: firstRequest)
    #expect(!acceptedLateResult)
    #expect(state.pair == englishToJapanese)
    #expect(state.status == .checking)

    let acceptedLatestResult = state.apply(.supported, requestID: secondRequest)
    #expect(acceptedLatestResult)
    #expect(state.pair == englishToJapanese)
    #expect(state.status == .supported)
}

@available(macOS 15.0, *)
@MainActor
@Test func unsupportedExplicitLanguagePairDisablesTranslation() async throws {
    let suiteName = "translation-pair-unsupported-\(UUID().uuidString)"
    let defaults = try #require(UserDefaults(suiteName: suiteName))
    defer { defaults.removePersistentDomain(forName: suiteName) }

    let model = TranslationFeatureModel(
        defaults: defaults,
        pairAvailabilityLoader: { _, _ in .unsupported }
    )
    model.selectTargetLanguage(Locale.Language(identifier: "en"))
    model.selectSourceLanguage(Locale.Language(identifier: "zh-Hans"))
    model.updateInput("需要翻译的文字")

    await model.waitForCurrentPairValidation()

    #expect(model.pairValidationState.status == .unsupported)
    #expect(model.pairValidationMessage?.contains("暂不支持") == true)
    #expect(!model.canTranslate)
    #expect(!model.beginTranslation())
}

@available(macOS 15.0, *)
@MainActor
@Test func supportedLanguageLoadingCanRetryAfterAnEmptyResult() async throws {
    let suiteName = "translation-language-retry-\(UUID().uuidString)"
    let defaults = try #require(UserDefaults(suiteName: suiteName))
    defer { defaults.removePersistentDomain(forName: suiteName) }

    let english = Locale.Language(identifier: "en")
    let chinese = Locale.Language(identifier: "zh-Hans")
    var loadCount = 0
    let model = TranslationFeatureModel(
        defaults: defaults,
        supportedLanguageLoader: {
            loadCount += 1
            return loadCount == 1 ? [] : [english, chinese]
        }
    )

    await model.loadSupportedLanguagesIfNeeded()
    #expect(model.supportedLanguages.isEmpty)
    #expect(model.canRetryLanguageLoading)
    #expect(model.feedback?.message.contains("重试") == true)

    await model.loadSupportedLanguagesIfNeeded()
    #expect(model.supportedLanguages.count == 2)
    #expect(!model.canRetryLanguageLoading)
    #expect(model.feedback == nil)
    #expect(loadCount == 2)
}
