using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Crosio.Windows.Translation;

internal sealed class MarianSentencePieceCodec
{
    private const int MaximumVocabularyBytes = 64 * 1024 * 1024;

    private readonly SentencePieceTokenizer _sourceTokenizer;
    private readonly SentencePieceTokenizer _targetTokenizer;
    private readonly IReadOnlyDictionary<string, int> _sourceVocabulary;
    private readonly IReadOnlyDictionary<int, string> _targetVocabulary;
    private readonly IReadOnlyDictionary<string, int> _targetSentencePieceVocabulary;
    private readonly MarianGenerationManifest _generation;

    private MarianSentencePieceCodec(
        SentencePieceTokenizer sourceTokenizer,
        SentencePieceTokenizer targetTokenizer,
        IReadOnlyDictionary<string, int> sourceVocabulary,
        IReadOnlyDictionary<int, string> targetVocabulary,
        MarianGenerationManifest generation)
    {
        _sourceTokenizer = sourceTokenizer;
        _targetTokenizer = targetTokenizer;
        _sourceVocabulary = sourceVocabulary;
        _targetVocabulary = targetVocabulary;
        _targetSentencePieceVocabulary = targetTokenizer.Vocabulary;
        _generation = generation;
    }

    public static MarianSentencePieceCodec Load(InstalledTranslationModel model)
    {
        try
        {
            var tokenizer = model.Manifest.Tokenizer;
            using var sourceModel = File.OpenRead(ModelPackPathPolicy.ResolveWithin(
                model.DirectoryPath,
                tokenizer.SourceSentencePieceModel));
            using var targetModel = File.OpenRead(ModelPackPathPolicy.ResolveWithin(
                model.DirectoryPath,
                tokenizer.TargetSentencePieceModel));
            var sourceTokenizer = SentencePieceTokenizer.Create(
                sourceModel,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                specialTokens: null);
            var targetTokenizer = SentencePieceTokenizer.Create(
                targetModel,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                specialTokens: null);
            var sourceVocabulary = LoadVocabulary(ModelPackPathPolicy.ResolveWithin(
                model.DirectoryPath,
                tokenizer.SourceVocabulary));
            var targetVocabulary = LoadVocabulary(ModelPackPathPolicy.ResolveWithin(
                model.DirectoryPath,
                tokenizer.TargetVocabulary));
            var inverseTarget = targetVocabulary.ToDictionary(pair => pair.Value, pair => pair.Key);
            ValidateSpecialTokens(sourceVocabulary, targetVocabulary, model.Manifest.Generation);
            return new MarianSentencePieceCodec(
                sourceTokenizer,
                tokenizer.DecodeWithSourceSentencePiece ? sourceTokenizer : targetTokenizer,
                sourceVocabulary,
                inverseTarget,
                model.Manifest.Generation);
        }
        catch (TranslationModelInvalidException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new TranslationModelInvalidException("无法载入 Marian SentencePiece 分词器", error);
        }
    }

    public long[] Encode(string text)
    {
        var pieces = _sourceTokenizer.EncodeToTokens(
            text,
            out _,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            considerPreTokenization: true,
            considerNormalization: true);
        var ids = new List<long>(pieces.Count + 1);
        foreach (var piece in pieces)
        {
            ids.Add(_sourceVocabulary.TryGetValue(piece.Value, out var id)
                ? id
                : _generation.UnknownTokenId);
        }
        ids.Add(_generation.EndOfSentenceTokenId);
        if (ids.Count > _generation.MaxInputTokens)
        {
            throw new TranslationInputTooLongException(ids.Count, _generation.MaxInputTokens);
        }
        return [.. ids];
    }

    public string Decode(IEnumerable<int> generatedIds)
    {
        var sentencePieceIds = new List<int>();
        foreach (var generatedId in generatedIds)
        {
            if (generatedId == _generation.EndOfSentenceTokenId ||
                generatedId == _generation.PaddingTokenId ||
                generatedId == _generation.DecoderStartTokenId ||
                generatedId == _generation.ForcedBeginningOfSentenceTokenId)
            {
                continue;
            }
            if (!_targetVocabulary.TryGetValue(generatedId, out var piece))
            {
                throw new TranslationModelInvalidException(
                    $"Marian 输出了目标词表中不存在的词元 {generatedId}");
            }
            if (!_targetSentencePieceVocabulary.TryGetValue(piece, out var sentencePieceId))
            {
                throw new TranslationModelInvalidException(
                    $"目标词表词元无法由 SentencePiece 解码：{piece}");
            }
            sentencePieceIds.Add(sentencePieceId);
        }
        return _targetTokenizer.Decode(sentencePieceIds, considerSpecialTokens: false).Trim();
    }

    private static Dictionary<string, int> LoadVocabulary(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumVocabularyBytes)
        {
            throw new TranslationModelInvalidException($"词表大小无效：{info.Name}");
        }
        using var stream = File.OpenRead(path);
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
            ?? throw new TranslationModelInvalidException($"词表为空：{info.Name}");
        if (vocabulary.Count == 0 || vocabulary.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Value < 0))
        {
            throw new TranslationModelInvalidException($"词表内容无效：{info.Name}");
        }
        if (vocabulary.Values.Distinct().Count() != vocabulary.Count)
        {
            throw new TranslationModelInvalidException($"词表包含重复 ID：{info.Name}");
        }
        return vocabulary;
    }

    private static void ValidateSpecialTokens(
        IReadOnlyDictionary<string, int> sourceVocabulary,
        IReadOnlyDictionary<string, int> targetVocabulary,
        MarianGenerationManifest generation)
    {
        var validSourceIds = sourceVocabulary.Values.ToHashSet();
        var validIds = targetVocabulary.Values.ToHashSet();
        var required = new[]
        {
            generation.UnknownTokenId,
            generation.EndOfSentenceTokenId,
            generation.PaddingTokenId,
            generation.DecoderStartTokenId,
        };
        if (!validSourceIds.Contains(generation.UnknownTokenId) ||
            !validSourceIds.Contains(generation.EndOfSentenceTokenId) ||
            required.Any(id => !validIds.Contains(id)) ||
            generation.ForcedBeginningOfSentenceTokenId is { } forced && !validIds.Contains(forced))
        {
            throw new TranslationModelInvalidException("Marian 特殊词元 ID 不在目标词表中");
        }
    }
}
