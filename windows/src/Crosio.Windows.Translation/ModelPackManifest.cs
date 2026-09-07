using System.Text.Json.Serialization;
using Crosio.Windows.Intelligence.Translation;

namespace Crosio.Windows.Translation;

public sealed record TranslationModelPackManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string SupportedModelFamily = "marian-onnx";

    public required int SchemaVersion { get; init; }
    public required string PackId { get; init; }
    public required string Version { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<TranslationDirection>))]
    public required TranslationDirection Direction { get; init; }

    public required string ModelFamily { get; init; }
    public required ModelOriginManifest Origin { get; init; }
    public required ModelLicenseManifest License { get; init; }
    public required MarianGraphManifest Graph { get; init; }
    public required MarianTokenizerManifest Tokenizer { get; init; }
    public required MarianGenerationManifest Generation { get; init; }
    public required IReadOnlyList<ModelArtifactManifest> Artifacts { get; init; }
}

public sealed record ModelOriginManifest
{
    public required string Publisher { get; init; }
    public required string ModelName { get; init; }
    public required string SourceUrl { get; init; }
    public string? Revision { get; init; }
}

public sealed record ModelLicenseManifest
{
    public required string SpdxId { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string Attribution { get; init; }
    public required string LicenseFile { get; init; }
}

public sealed record MarianGraphManifest
{
    public required string EncoderModel { get; init; }
    public required string DecoderModel { get; init; }
    public string EncoderInputIds { get; init; } = "input_ids";
    public string EncoderAttentionMask { get; init; } = "attention_mask";
    public string EncoderOutput { get; init; } = "last_hidden_state";
    public string DecoderInputIds { get; init; } = "input_ids";
    public string DecoderAttentionMask { get; init; } = "encoder_attention_mask";
    public string DecoderEncoderHiddenStates { get; init; } = "encoder_hidden_states";
    public string DecoderLogitsOutput { get; init; } = "logits";
}

public sealed record MarianTokenizerManifest
{
    public required string SourceSentencePieceModel { get; init; }
    public required string TargetSentencePieceModel { get; init; }
    public required string SourceVocabulary { get; init; }
    public required string TargetVocabulary { get; init; }
    public required bool DecodeWithSourceSentencePiece { get; init; }
}

public sealed record MarianGenerationManifest
{
    public required int UnknownTokenId { get; init; }
    public required int EndOfSentenceTokenId { get; init; }
    public required int PaddingTokenId { get; init; }
    public required int DecoderStartTokenId { get; init; }
    public int? ForcedBeginningOfSentenceTokenId { get; init; }
    public int MaxInputTokens { get; init; } = 512;
    public int MaxOutputTokens { get; init; } = 512;
    public int MinimumOutputTokens { get; init; } = 1;
    public IReadOnlyList<int> SuppressedTokenIds { get; init; } = [];
}

public sealed record ModelArtifactManifest
{
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ActiveModelPointer
{
    public required int SchemaVersion { get; init; }
    public required string PackId { get; init; }
    public required string DirectoryName { get; init; }
    public required string ManifestSha256 { get; init; }
}
