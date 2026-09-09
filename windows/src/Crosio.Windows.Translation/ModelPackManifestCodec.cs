using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crosio.Windows.Translation;

internal static class ModelPackManifestCodec
{
    public const int MaximumManifestBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static TranslationModelPackManifest DeserializeManifest(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaximumManifestBytes)
        {
            throw new TranslationModelInvalidException("模型 manifest 大小无效");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<TranslationModelPackManifest>(utf8, Options)
                ?? throw new TranslationModelInvalidException("模型 manifest 为空");
            ModelPackManifestValidator.Validate(manifest);
            return manifest;
        }
        catch (TranslationModelInvalidException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new TranslationModelInvalidException("无法解析模型 manifest", error);
        }
    }

    public static ActiveModelPointer DeserializePointer(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<ActiveModelPointer>(utf8, Options)
                ?? throw new TranslationModelInvalidException("离线模型索引为空");
        }
        catch (TranslationModelInvalidException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new TranslationModelInvalidException("无法解析离线模型索引", error);
        }
    }

    public static byte[] SerializePointer(ActiveModelPointer pointer) =>
        JsonSerializer.SerializeToUtf8Bytes(pointer, Options);
}

internal static class ModelPackManifestValidator
{
    private const long MaximumUncompressedBytes = 2L * 1024 * 1024 * 1024;

    public static void Validate(TranslationModelPackManifest manifest)
    {
        if (manifest.SchemaVersion != TranslationModelPackManifest.CurrentSchemaVersion)
        {
            throw new TranslationModelInvalidException(
                $"不支持模型 manifest 版本 {manifest.SchemaVersion}");
        }
        RequireText(manifest.PackId, nameof(manifest.PackId), 128);
        RequireText(manifest.Version, nameof(manifest.Version), 64);
        if (!string.Equals(
                manifest.ModelFamily,
                TranslationModelPackManifest.SupportedModelFamily,
                StringComparison.Ordinal))
        {
            throw new TranslationModelInvalidException($"不支持模型类型 {manifest.ModelFamily}");
        }

        ValidateOrigin(manifest.Origin);
        ValidateLicense(manifest.License);
        ValidateGraph(manifest.Graph);
        ValidateTokenizer(manifest.Tokenizer);
        ValidateGeneration(manifest.Generation);

        if (manifest.Artifacts is null || manifest.Artifacts.Count is 0 or > 256)
        {
            throw new TranslationModelInvalidException("模型 manifest 文件数量无效");
        }

        var files = new Dictionary<string, ModelArtifactManifest>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var artifact in manifest.Artifacts)
        {
            var path = ModelPackPathPolicy.NormalizeRelativePath(artifact.Path);
            if (string.Equals(path, ModelPackPathPolicy.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new TranslationModelInvalidException("manifest.json 不能把自身声明为模型文件");
            }
            if (!string.Equals(path, artifact.Path, StringComparison.Ordinal))
            {
                throw new TranslationModelInvalidException($"模型文件路径必须使用规范斜杠：{artifact.Path}");
            }
            if (!files.TryAdd(path, artifact))
            {
                throw new TranslationModelInvalidException($"模型文件重复：{path}");
            }
            if (artifact.Bytes <= 0)
            {
                throw new TranslationModelInvalidException($"模型文件大小无效：{path}");
            }
            if (!HashUtility.IsSha256(artifact.Sha256))
            {
                throw new TranslationModelInvalidException($"模型文件 SHA-256 无效：{path}");
            }

            try
            {
                totalBytes = checked(totalBytes + artifact.Bytes);
            }
            catch (OverflowException error)
            {
                throw new TranslationModelInvalidException("模型文件总大小无效", error);
            }
        }

        if (totalBytes > MaximumUncompressedBytes)
        {
            throw new TranslationModelInvalidException("模型包解压后超过 2 GiB 安全上限");
        }

        var requiredFiles = new[]
        {
            manifest.License.LicenseFile,
            manifest.Graph.EncoderModel,
            manifest.Graph.DecoderModel,
            manifest.Tokenizer.SourceSentencePieceModel,
            manifest.Tokenizer.TargetSentencePieceModel,
            manifest.Tokenizer.SourceVocabulary,
            manifest.Tokenizer.TargetVocabulary,
        };
        foreach (var required in requiredFiles)
        {
            var normalized = ModelPackPathPolicy.NormalizeRelativePath(required);
            if (!files.ContainsKey(normalized))
            {
                throw new TranslationModelInvalidException(
                    $"模型 manifest 缺少必需文件声明：{normalized}");
            }
        }
    }

    private static void ValidateOrigin(ModelOriginManifest origin)
    {
        if (origin is null)
        {
            throw new TranslationModelInvalidException("模型来源信息缺失");
        }
        RequireText(origin.Publisher, "origin.publisher", 200);
        RequireText(origin.ModelName, "origin.modelName", 200);
        RequireHttpsUrl(origin.SourceUrl, "origin.sourceUrl");
        RequireText(origin.Revision, "origin.revision", 128);
    }

    private static void ValidateLicense(ModelLicenseManifest license)
    {
        if (license is null)
        {
            throw new TranslationModelInvalidException("模型许可信息缺失");
        }
        RequireText(license.SpdxId, "license.spdxId", 80);
        if (license.SpdxId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.' and not '+'))
        {
            throw new TranslationModelInvalidException("license.spdxId 不是有效的 SPDX 标识");
        }
        RequireText(license.Name, "license.name", 200);
        RequireHttpsUrl(license.Url, "license.url");
        RequireText(license.Attribution, "license.attribution", 1000);
        _ = ModelPackPathPolicy.NormalizeRelativePath(license.LicenseFile);
    }

    private static void ValidateGraph(MarianGraphManifest graph)
    {
        if (graph is null)
        {
            throw new TranslationModelInvalidException("Marian 图配置缺失");
        }
        _ = ModelPackPathPolicy.NormalizeRelativePath(graph.EncoderModel);
        _ = ModelPackPathPolicy.NormalizeRelativePath(graph.DecoderModel);
        RequireText(graph.EncoderInputIds, "graph.encoderInputIds", 128);
        RequireText(graph.EncoderAttentionMask, "graph.encoderAttentionMask", 128);
        RequireText(graph.EncoderOutput, "graph.encoderOutput", 128);
        RequireText(graph.DecoderInputIds, "graph.decoderInputIds", 128);
        RequireText(graph.DecoderAttentionMask, "graph.decoderAttentionMask", 128);
        RequireText(graph.DecoderEncoderHiddenStates, "graph.decoderEncoderHiddenStates", 128);
        RequireText(graph.DecoderLogitsOutput, "graph.decoderLogitsOutput", 128);
    }

    private static void ValidateTokenizer(MarianTokenizerManifest tokenizer)
    {
        if (tokenizer is null)
        {
            throw new TranslationModelInvalidException("SentencePiece 配置缺失");
        }
        _ = ModelPackPathPolicy.NormalizeRelativePath(tokenizer.SourceSentencePieceModel);
        _ = ModelPackPathPolicy.NormalizeRelativePath(tokenizer.TargetSentencePieceModel);
        _ = ModelPackPathPolicy.NormalizeRelativePath(tokenizer.SourceVocabulary);
        _ = ModelPackPathPolicy.NormalizeRelativePath(tokenizer.TargetVocabulary);
    }

    private static void ValidateGeneration(MarianGenerationManifest generation)
    {
        if (generation is null)
        {
            throw new TranslationModelInvalidException("Marian 生成配置缺失");
        }
        if (generation.UnknownTokenId < 0 ||
            generation.EndOfSentenceTokenId < 0 ||
            generation.PaddingTokenId < 0 ||
            generation.DecoderStartTokenId < 0 ||
            generation.ForcedBeginningOfSentenceTokenId is < 0 ||
            generation.SuppressedTokenIds.Any(id => id < 0))
        {
            throw new TranslationModelInvalidException("模型特殊词元 ID 不能为负数");
        }
        if (generation.MaxInputTokens is < 8 or > 4096 ||
            generation.MaxOutputTokens is < 8 or > 4096 ||
            generation.MinimumOutputTokens < 0 ||
            generation.MinimumOutputTokens >= generation.MaxOutputTokens)
        {
            throw new TranslationModelInvalidException("模型词元长度限制无效");
        }
    }

    private static void RequireText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new TranslationModelInvalidException($"{name} 为空或过长");
        }
    }

    private static void RequireHttpsUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationModelInvalidException($"{name} 必须是 HTTPS 地址");
        }
    }
}
