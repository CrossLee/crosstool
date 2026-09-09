using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Crosio.Windows.Intelligence.Translation;

namespace Crosio.Windows.Translation;

/// <summary>
/// User-facing metadata for one of the model revisions pinned by Crosio.
/// Model weights are downloaded only when <see cref="OfficialTranslationModelInstaller.DownloadAndInstallAsync"/>
/// is explicitly invoked.
/// </summary>
public sealed record OfficialTranslationModelInfo(
    string PackId,
    TranslationDirection Direction,
    string ModelName,
    string Revision,
    string LicenseSpdxId,
    long DownloadBytes);

/// <summary>
/// Downloads the fixed, hash-pinned Marian ONNX files used by Crosio and turns
/// them into the same strictly validated package accepted by
/// <see cref="OfflineModelPackInstaller"/>.
/// </summary>
public sealed class OfficialTranslationModelInstaller : IDisposable, IAsyncDisposable
{
    public const long MaximumDownloadBytes = 512L * 1024 * 1024;

    private readonly OfflineModelCatalog _catalog;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyDictionary<TranslationDirection, OfficialTranslationModelDefinition> _definitions;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly AsyncOperationLifetime _operations = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public OfficialTranslationModelInstaller(
        OfflineModelCatalog catalog,
        HttpClient? httpClient = null)
        : this(catalog, httpClient, OfficialTranslationModelDefinitions.All)
    {
    }

    internal OfficialTranslationModelInstaller(
        OfflineModelCatalog catalog,
        HttpClient? httpClient,
        IReadOnlyDictionary<TranslationDirection, OfficialTranslationModelDefinition> definitions)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _httpClient = httpClient ?? CreateHttpClient();
        _ownsHttpClient = httpClient is null;
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    }

    public static OfficialTranslationModelInfo GetModelInfo(TranslationDirection direction)
    {
        var definition = GetDefinition(OfficialTranslationModelDefinitions.All, direction);
        ValidateDefinition(definition);
        return definition.ToInfo();
    }

    public async Task<InstalledTranslationModel> DownloadAndInstallAsync(
        TranslationDirection direction,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        var definition = GetDefinition(_definitions, direction);
        ValidateDefinition(definition);

        await _installGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            return await DownloadBuildAndInstallAsync(definition, progress, operation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _installGate.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _operations.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _installGate.Dispose();
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    private static HttpClient CreateHttpClient() => new()
    {
        // The largest pinned artifact is about 184 MiB. Keep a bounded but
        // realistic timeout for slower school networks; callers can still
        // cancel immediately through the per-install cancellation token.
        Timeout = TimeSpan.FromMinutes(30),
    };

    private async Task<InstalledTranslationModel> DownloadBuildAndInstallAsync(
        OfficialTranslationModelDefinition definition,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var downloadsRoot = PrepareDownloadsRoot(_catalog.RootDirectory);
        var workDirectory = Path.Combine(downloadsRoot, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var downloaded = new List<DownloadedOfficialArtifact>(definition.Artifacts.Count);

        try
        {
            var completedBytes = 0L;
            for (var index = 0; index < definition.Artifacts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = definition.Artifacts[index];
                var temporaryPath = Path.Combine(workDirectory, $"artifact-{index:D2}.download");
                await MaterializeArtifactAsync(
                    artifact,
                    temporaryPath,
                    completedBytes,
                    definition.DownloadBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                completedBytes = checked(completedBytes + artifact.Bytes);
                downloaded.Add(new DownloadedOfficialArtifact(artifact, temporaryPath));
            }

            progress?.Report(new ModelInstallProgress(
                ModelInstallStage.VerifyingFiles,
                definition.DownloadBytes,
                definition.DownloadBytes,
                "官方模型文件校验完成，正在生成本地模型包"));

            var manifest = definition.CreateManifest();
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ModelPackManifestCodec.Options);
            _ = ModelPackManifestCodec.DeserializeManifest(manifestBytes);
            var archivePath = Path.Combine(workDirectory, "official-model-pack.zip");
            await BuildArchiveAsync(
                archivePath,
                manifestBytes,
                downloaded,
                cancellationToken).ConfigureAwait(false);

            var archiveInfo = new FileInfo(archivePath);
            if (archiveInfo.Length is <= 0 or > OfflineModelPackInstaller.MaximumArchiveBytes)
            {
                throw new TranslationModelInvalidException("生成的官方模型包大小超过安全限制");
            }
            var archiveHash = await HashUtility.ComputeFileSha256Async(archivePath, cancellationToken)
                .ConfigureAwait(false);

            await using var archive = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var packageInstaller = new OfflineModelPackInstaller(_catalog, _httpClient);
            return await packageInstaller.InstallArchiveAsync(
                archive,
                new ModelPackInstallDescriptor
                {
                    PackId = manifest.PackId,
                    Direction = manifest.Direction,
                    ArchiveBytes = archiveInfo.Length,
                    ArchiveSha256 = archiveHash,
                },
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = BestEffortFileSystemCleanup.TryRun(
                "official translation model work directory",
                () =>
                {
                    if (Directory.Exists(workDirectory))
                    {
                        Directory.Delete(workDirectory, recursive: true);
                    }
                });
        }
    }

    private async Task MaterializeArtifactAsync(
        OfficialTranslationModelArtifact artifact,
        string outputPath,
        long previouslyCompletedBytes,
        long totalBytes,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (artifact.EmbeddedResourceId is { } embeddedResourceId)
        {
            await using var embeddedInput =
                OfficialTranslationModelEmbeddedArtifacts.OpenRead(embeddedResourceId);
            await WriteVerifiedArtifactAsync(
                embeddedInput,
                artifact,
                outputPath,
                previouslyCompletedBytes,
                totalBytes,
                progress,
                $"正在准备 {artifact.LocalPath}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            artifact.DownloadUri ?? throw new TranslationModelInvalidException(
                $"官方模型文件来源无效：{artifact.LocalPath}"));
        request.Headers.UserAgent.ParseAdd("Crosio-Windows/official-translation-model-installer");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            !finalUri.IsAbsoluteUri ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationModelInvalidException("官方模型下载重定向到了非 HTTPS 地址");
        }
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != artifact.Bytes)
        {
            throw new TranslationModelInvalidException($"官方模型文件大小校验失败：{artifact.LocalPath}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await WriteVerifiedArtifactAsync(
            input,
            artifact,
            outputPath,
            previouslyCompletedBytes,
            totalBytes,
            progress,
            $"正在下载 {artifact.LocalPath}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteVerifiedArtifactAsync(
        Stream input,
        OfficialTranslationModelArtifact artifact,
        string outputPath,
        long previouslyCompletedBytes,
        long totalBytes,
        IProgress<ModelInstallProgress>? progress,
        string progressDescription,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var downloadedBytes = 0L;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                downloadedBytes = checked(downloadedBytes + count);
                var cumulativeBytes = checked(previouslyCompletedBytes + downloadedBytes);
                if (downloadedBytes > artifact.Bytes || cumulativeBytes > totalBytes ||
                    cumulativeBytes > MaximumDownloadBytes)
                {
                    throw new TranslationModelInvalidException(
                        $"官方模型文件超过声明大小：{artifact.LocalPath}");
                }
                hash.AppendData(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModelInstallProgress(
                    ModelInstallStage.Downloading,
                    cumulativeBytes,
                    totalBytes,
                    progressDescription));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (downloadedBytes != artifact.Bytes)
        {
            throw new TranslationModelInvalidException($"官方模型文件下载不完整：{artifact.LocalPath}");
        }
        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!HashUtility.EqualsSha256(artifact.Sha256, actualHash))
        {
            throw new TranslationModelInvalidException($"官方模型文件 SHA-256 校验失败：{artifact.LocalPath}");
        }
    }

    private static async Task BuildArchiveAsync(
        string archivePath,
        byte[] manifestBytes,
        IReadOnlyList<DownloadedOfficialArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteArchiveEntryAsync(
            archive,
            ModelPackPathPolicy.ManifestFileName,
            new MemoryStream(manifestBytes, writable: false),
            cancellationToken).ConfigureAwait(false);
        foreach (var downloaded in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = new FileStream(
                downloaded.TemporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await WriteArchiveEntryAsync(
                archive,
                downloaded.Artifact.LocalPath,
                input,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteArchiveEntryAsync(
        ZipArchive archive,
        string entryName,
        Stream input,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        await using var output = entry.Open();
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private static OfficialTranslationModelDefinition GetDefinition(
        IReadOnlyDictionary<TranslationDirection, OfficialTranslationModelDefinition> definitions,
        TranslationDirection direction) =>
        definitions.TryGetValue(direction, out var definition)
            ? definition
            : throw new TranslationModelInvalidException($"没有 {direction} 的官方离线模型定义");

    private static void ValidateDefinition(OfficialTranslationModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Direction is not (TranslationDirection.ChineseToEnglish or TranslationDirection.EnglishToChinese) ||
            string.IsNullOrWhiteSpace(definition.PackId) ||
            definition.PackId.Length > 128 ||
            string.IsNullOrWhiteSpace(definition.Revision) ||
            definition.Artifacts is null ||
            definition.Artifacts.Count is 0 or > 32)
        {
            throw new TranslationModelInvalidException("官方模型定义无效");
        }

        var localPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0L;
        foreach (var artifact in definition.Artifacts)
        {
            var normalizedPath = ModelPackPathPolicy.NormalizeRelativePath(artifact.LocalPath);
            if (!string.Equals(normalizedPath, artifact.LocalPath, StringComparison.Ordinal) ||
                !localPaths.Add(normalizedPath))
            {
                throw new TranslationModelInvalidException(
                    $"官方模型文件路径重复或不规范：{artifact.LocalPath}");
            }
            var hasNetworkSource = artifact.DownloadUri is not null;
            var hasEmbeddedSource = !string.IsNullOrWhiteSpace(artifact.EmbeddedResourceId);
            if (hasNetworkSource == hasEmbeddedSource)
            {
                throw new TranslationModelInvalidException(
                    $"官方模型文件必须且只能声明一种来源：{artifact.LocalPath}");
            }
            if (hasNetworkSource &&
                (!artifact.DownloadUri!.IsAbsoluteUri ||
                 !string.Equals(
                     artifact.DownloadUri.Scheme,
                     Uri.UriSchemeHttps,
                     StringComparison.OrdinalIgnoreCase)))
            {
                throw new TranslationModelInvalidException(
                    $"官方模型文件地址必须使用 HTTPS：{artifact.LocalPath}");
            }
            if (hasEmbeddedSource &&
                !OfficialTranslationModelEmbeddedArtifacts.Contains(artifact.EmbeddedResourceId!))
            {
                throw new TranslationModelInvalidException(
                    $"官方模型内置资源不存在：{artifact.LocalPath}");
            }
            if (artifact.Bytes <= 0 || !HashUtility.IsSha256(artifact.Sha256))
            {
                throw new TranslationModelInvalidException(
                    $"官方模型文件大小或 SHA-256 无效：{artifact.LocalPath}");
            }
            try
            {
                totalBytes = checked(totalBytes + artifact.Bytes);
            }
            catch (OverflowException error)
            {
                throw new TranslationModelInvalidException("官方模型文件总大小无效", error);
            }
        }
        if (totalBytes != definition.DownloadBytes || totalBytes > MaximumDownloadBytes)
        {
            throw new TranslationModelInvalidException("官方模型下载总大小与固定目录不一致或超过安全限制");
        }

        var manifest = definition.CreateManifest();
        ModelPackManifestValidator.Validate(manifest);
        if (manifest.Artifacts.Count != definition.Artifacts.Count ||
            manifest.Artifacts.Any(manifestArtifact =>
                !definition.Artifacts.Any(artifact =>
                    string.Equals(artifact.LocalPath, manifestArtifact.Path, StringComparison.Ordinal) &&
                    artifact.Bytes == manifestArtifact.Bytes &&
                    HashUtility.EqualsSha256(artifact.Sha256, manifestArtifact.Sha256))))
        {
            throw new TranslationModelInvalidException("官方模型 manifest 与下载目录不一致");
        }
    }

    private static string PrepareDownloadsRoot(string modelRoot)
    {
        Directory.CreateDirectory(modelRoot);
        if ((File.GetAttributes(modelRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TranslationModelInvalidException("模型根目录不能是符号链接或重解析点");
        }
        var downloadsRoot = Path.Combine(modelRoot, ".official-downloads");
        Directory.CreateDirectory(downloadsRoot);
        if ((File.GetAttributes(downloadsRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TranslationModelInvalidException("官方模型临时目录不能是符号链接或重解析点");
        }
        return downloadsRoot;
    }

    private sealed record DownloadedOfficialArtifact(
        OfficialTranslationModelArtifact Artifact,
        string TemporaryPath);
}

internal sealed record OfficialTranslationModelArtifact(
    Uri? DownloadUri,
    string LocalPath,
    long Bytes,
    string Sha256,
    string? EmbeddedResourceId = null);

internal sealed record OfficialTranslationModelDefinition
{
    public required string PackId { get; init; }
    public required TranslationDirection Direction { get; init; }
    public required string Version { get; init; }
    public required string Publisher { get; init; }
    public required string ModelName { get; init; }
    public required string SourceUrl { get; init; }
    public required string Revision { get; init; }
    public required string LicenseSpdxId { get; init; }
    public required string LicenseName { get; init; }
    public required string LicenseUrl { get; init; }
    public required string Attribution { get; init; }
    public required IReadOnlyList<OfficialTranslationModelArtifact> Artifacts { get; init; }

    public long DownloadBytes => Artifacts.Sum(artifact => artifact.Bytes);

    public OfficialTranslationModelInfo ToInfo() => new(
        PackId,
        Direction,
        ModelName,
        Revision,
        LicenseSpdxId,
        DownloadBytes);

    public TranslationModelPackManifest CreateManifest() => new()
    {
        SchemaVersion = TranslationModelPackManifest.CurrentSchemaVersion,
        PackId = PackId,
        Version = Version,
        Direction = Direction,
        ModelFamily = TranslationModelPackManifest.SupportedModelFamily,
        Origin = new ModelOriginManifest
        {
            Publisher = Publisher,
            ModelName = ModelName,
            SourceUrl = SourceUrl,
            Revision = Revision,
        },
        License = new ModelLicenseManifest
        {
            SpdxId = LicenseSpdxId,
            Name = LicenseName,
            Url = LicenseUrl,
            Attribution = Attribution,
            LicenseFile = "LICENSE.txt",
        },
        Graph = new MarianGraphManifest
        {
            EncoderModel = "encoder_model.onnx",
            DecoderModel = "decoder_model.onnx",
        },
        Tokenizer = new MarianTokenizerManifest
        {
            SourceSentencePieceModel = "source.spm",
            TargetSentencePieceModel = "target.spm",
            SourceVocabulary = "vocab.json",
            TargetVocabulary = "vocab.json",
            DecodeWithSourceSentencePiece = false,
        },
        Generation = new MarianGenerationManifest
        {
            UnknownTokenId = 1,
            EndOfSentenceTokenId = 0,
            PaddingTokenId = 65000,
            DecoderStartTokenId = 65000,
            MaxInputTokens = 512,
            MaxOutputTokens = 512,
            MinimumOutputTokens = 1,
            SuppressedTokenIds = [65000],
        },
        Artifacts = Artifacts.Select(artifact => new ModelArtifactManifest
        {
            Path = artifact.LocalPath,
            Bytes = artifact.Bytes,
            Sha256 = artifact.Sha256,
        }).ToArray(),
    };
}

internal static class OfficialTranslationModelDefinitions
{
    private const string ChineseToEnglishRevision = "8e3032ebeebbacda779fe95efa64c03b962f83f3";
    private const string EnglishToChineseRevision = "046f55aec303cdee3e0318604406d4df20f1e8ea";

    public static IReadOnlyDictionary<TranslationDirection, OfficialTranslationModelDefinition> All { get; } =
        new Dictionary<TranslationDirection, OfficialTranslationModelDefinition>
        {
            [TranslationDirection.ChineseToEnglish] = CreateChineseToEnglish(),
            [TranslationDirection.EnglishToChinese] = CreateEnglishToChinese(),
        };

    private static OfficialTranslationModelDefinition CreateChineseToEnglish() => new()
    {
        PackId = $"crosio-official-opus-mt-zh-en-{ChineseToEnglishRevision}",
        Direction = TranslationDirection.ChineseToEnglish,
        Version = $"official-{ChineseToEnglishRevision}",
        Publisher = "Helsinki-NLP / onnx-community",
        ModelName = "opus-mt-zh-en quantized ONNX",
        SourceUrl = $"https://huggingface.co/onnx-community/opus-mt-zh-en/tree/{ChineseToEnglishRevision}",
        Revision = ChineseToEnglishRevision,
        LicenseSpdxId = "CC-BY-4.0",
        LicenseName = "Creative Commons Attribution 4.0 International",
        LicenseUrl = "https://creativecommons.org/licenses/by/4.0/legalcode.txt",
        Attribution = "Helsinki-NLP OPUS-MT opus-mt-zh-en; ONNX artifacts distributed by onnx-community; CC BY 4.0.",
        Artifacts =
        [
            HfArtifact(
                "onnx-community/opus-mt-zh-en",
                ChineseToEnglishRevision,
                "onnx/decoder_model_quantized.onnx",
                "decoder_model.onnx",
                192882669,
                "6ec4ee5c028efb856e933d5d0732bac28f0914b34e652978fb201864931c49a6"),
            HfArtifact(
                "onnx-community/opus-mt-zh-en",
                ChineseToEnglishRevision,
                "onnx/encoder_model_quantized.onnx",
                "encoder_model.onnx",
                52875078,
                "86b0dc5a1d5d8062583800654864aae1311fce2172bba80910d02020d3693577"),
            HfArtifact(
                "onnx-community/opus-mt-zh-en",
                ChineseToEnglishRevision,
                "source.spm",
                "source.spm",
                804677,
                "e27a3a1b539f4959ec72ea60e453f49156289f95d4e6000b29332efc45616203"),
            HfArtifact(
                "onnx-community/opus-mt-zh-en",
                ChineseToEnglishRevision,
                "target.spm",
                "target.spm",
                806530,
                "6a881f4717cd7265f53fea54fd3dc689c767c05338fac7a4590f3088cb2d7855"),
            HfArtifact(
                "onnx-community/opus-mt-zh-en",
                ChineseToEnglishRevision,
                "vocab.json",
                "vocab.json",
                1747906,
                "08a119a1defd522fa047cb5e3bfe3e89633e96caa38ced0dc9cee7ef1021a011"),
            EmbeddedArtifact(
                OfficialTranslationModelEmbeddedArtifacts.CreativeCommonsBy40License,
                "LICENSE.txt",
                18657,
                "9ba9550ad48438d0836ddab3da480b3b69ffa0aac7b7878b5a0039e7ab429411"),
        ],
    };

    private static OfficialTranslationModelDefinition CreateEnglishToChinese() => new()
    {
        PackId = $"crosio-official-opus-mt-en-zh-{EnglishToChineseRevision}",
        Direction = TranslationDirection.EnglishToChinese,
        Version = $"official-{EnglishToChineseRevision}",
        Publisher = "Helsinki-NLP / Xenova",
        ModelName = "opus-mt-en-zh quantized ONNX",
        SourceUrl = $"https://huggingface.co/Xenova/opus-mt-en-zh/tree/{EnglishToChineseRevision}",
        Revision = EnglishToChineseRevision,
        LicenseSpdxId = "Apache-2.0",
        LicenseName = "Apache License 2.0",
        LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0.txt",
        Attribution = "Helsinki-NLP OPUS-MT opus-mt-en-zh; ONNX artifacts distributed by Xenova; Apache-2.0.",
        Artifacts =
        [
            HfArtifact(
                "Xenova/opus-mt-en-zh",
                EnglishToChineseRevision,
                "onnx/decoder_model_quantized.onnx",
                "decoder_model.onnx",
                59842102,
                "2c66a3981099b40edbb3a0d65e015d05964d42fecb9780f8422776eff5939112"),
            HfArtifact(
                "Xenova/opus-mt-en-zh",
                EnglishToChineseRevision,
                "onnx/encoder_model_quantized.onnx",
                "encoder_model.onnx",
                52899742,
                "d3b7912bf6a9bd27e4c074c2df91d4ff3d5b4bc5f7f6c8d7cc9c805c98fbafee"),
            HfArtifact(
                "Xenova/opus-mt-en-zh",
                EnglishToChineseRevision,
                "source.spm",
                "source.spm",
                806435,
                "5775ddc9e3ff2fae91554da56468ad35ff56edaba870fea74447bc7234bfdaa8"),
            HfArtifact(
                "Xenova/opus-mt-en-zh",
                EnglishToChineseRevision,
                "target.spm",
                "target.spm",
                804600,
                "81dc94efa84e4025ef38d25d5d07429fe41e3eb29d44003f1db6fe98487b0052"),
            HfArtifact(
                "Xenova/opus-mt-en-zh",
                EnglishToChineseRevision,
                "vocab.json",
                "vocab.json",
                1747795,
                "22c957348eed495ee925afc40a36da3e387c8a34a734c8486967c2dca271613e"),
            EmbeddedArtifact(
                OfficialTranslationModelEmbeddedArtifacts.Apache20License,
                "LICENSE.txt",
                11358,
                "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"),
        ],
    };

    private static OfficialTranslationModelArtifact HfArtifact(
        string repository,
        string revision,
        string remotePath,
        string localPath,
        long bytes,
        string sha256)
    {
        var encodedPath = string.Join(
            '/',
            ModelPackPathPolicy.NormalizeRelativePath(remotePath)
                .Split('/')
                .Select(Uri.EscapeDataString));
        return new OfficialTranslationModelArtifact(
            new Uri($"https://huggingface.co/{repository}/resolve/{revision}/{encodedPath}?download=true"),
            localPath,
            bytes,
            sha256);
    }

    private static OfficialTranslationModelArtifact EmbeddedArtifact(
        string resourceId,
        string localPath,
        long bytes,
        string sha256) =>
        new(
            null,
            localPath,
            bytes,
            sha256,
            resourceId);
}
