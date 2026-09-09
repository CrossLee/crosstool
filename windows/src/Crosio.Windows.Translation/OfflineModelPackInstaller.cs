using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Crosio.Windows.Intelligence.Translation;

namespace Crosio.Windows.Translation;

public enum ModelInstallStage
{
    Downloading,
    VerifyingArchive,
    Extracting,
    VerifyingFiles,
    Activating,
    Complete,
}

public sealed record ModelInstallProgress(
    ModelInstallStage Stage,
    long CompletedBytes,
    long TotalBytes,
    string Message)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1);
}

public sealed record ModelPackInstallDescriptor
{
    public required string PackId { get; init; }
    public required TranslationDirection Direction { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required long ArchiveBytes { get; init; }
}

public sealed record ModelPackDownloadDescriptor
{
    public required Uri DownloadUri { get; init; }
    public required ModelPackInstallDescriptor Package { get; init; }
}

public sealed class OfflineModelPackInstaller : IDisposable, IAsyncDisposable
{
    public const long MaximumArchiveBytes = 1536L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CommitGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly OfflineModelCatalog _catalog;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly AsyncOperationLifetime _operations = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public OfflineModelPackInstaller(OfflineModelCatalog catalog, HttpClient? httpClient = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
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
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    /// <summary>
    /// Installs a model package explicitly chosen by the user. The archive is
    /// held read-only while its manifest, complete SHA-256, and declared size
    /// are materialized, then it goes through the same strict installer as a
    /// catalog-pinned HTTPS download.
    /// </summary>
    public async Task<InstalledTranslationModel> InstallLocalArchiveAsync(
        string archivePath,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        return await InstallLocalArchiveCoreAsync(
            archivePath,
            progress,
            operation.Token).ConfigureAwait(false);
    }

    private async Task<InstalledTranslationModel> InstallLocalArchiveCoreAsync(
        string archivePath,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists ||
            info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length is <= 0 or > MaximumArchiveBytes)
        {
            throw new TranslationModelInvalidException("请选择普通的一爪离线模型 ZIP 文件");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var archiveHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        stream.Position = 0;

        TranslationModelPackManifest manifest;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var manifestBytes = await ReadEntryAsync(
                FindSingleManifestEntry(archive),
                ModelPackManifestCodec.MaximumManifestBytes,
                cancellationToken).ConfigureAwait(false);
            manifest = ModelPackManifestCodec.DeserializeManifest(manifestBytes);
        }
        catch (InvalidDataException error)
        {
            throw new TranslationModelInvalidException("模型包不是有效的 ZIP 文件", error);
        }

        stream.Position = 0;
        return await InstallArchiveCoreAsync(
            stream,
            new ModelPackInstallDescriptor
            {
                PackId = manifest.PackId,
                Direction = manifest.Direction,
                ArchiveBytes = info.Length,
                ArchiveSha256 = archiveHash,
            },
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstalledTranslationModel> DownloadAndInstallAsync(
        ModelPackDownloadDescriptor descriptor,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        return await DownloadAndInstallCoreAsync(
            descriptor,
            progress,
            operation.Token).ConfigureAwait(false);
    }

    private async Task<InstalledTranslationModel> DownloadAndInstallCoreAsync(
        ModelPackDownloadDescriptor descriptor,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor.Package);
        if (!descriptor.DownloadUri.IsAbsoluteUri ||
            !string.Equals(descriptor.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationModelInvalidException("模型下载地址必须使用 HTTPS");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.DownloadUri);
        request.Headers.UserAgent.ParseAdd("Crosio-Windows/translation-model-installer");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationModelInvalidException("模型下载重定向到了非 HTTPS 地址");
        }
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != descriptor.Package.ArchiveBytes)
        {
            throw new TranslationModelInvalidException("模型包下载大小与受信目录不一致");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await InstallArchiveCoreAsync(
            stream,
            descriptor.Package,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstalledTranslationModel> InstallArchiveAsync(
        Stream archiveStream,
        ModelPackInstallDescriptor descriptor,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        return await InstallArchiveCoreAsync(
            archiveStream,
            descriptor,
            progress,
            operation.Token).ConfigureAwait(false);
    }

    private async Task<InstalledTranslationModel> InstallArchiveCoreAsync(
        Stream archiveStream,
        ModelPackInstallDescriptor descriptor,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!archiveStream.CanRead)
        {
            throw new ArgumentException("模型包流不可读", nameof(archiveStream));
        }
        ValidateDescriptor(descriptor);

        var stagingRoot = PrepareOwnedDirectory(_catalog.RootDirectory, ".staging");
        var archivePath = Path.Combine(stagingRoot, $"download-{Guid.NewGuid():N}.zip");
        string? unpackContainer = null;
        string? unpackDirectory = null;

        try
        {
            var archiveHash = await CopyArchiveToOwnedFileAsync(
                archiveStream,
                archivePath,
                descriptor,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!HashUtility.EqualsSha256(descriptor.ArchiveSha256, archiveHash))
            {
                throw new TranslationModelInvalidException("模型包 SHA-256 校验失败");
            }

            progress?.Report(new ModelInstallProgress(
                ModelInstallStage.VerifyingArchive,
                descriptor.ArchiveBytes,
                descriptor.ArchiveBytes,
                "正在验证模型包"));

            await using var input = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var manifestEntry = FindSingleManifestEntry(archive);
            var manifestBytes = await ReadEntryAsync(
                manifestEntry,
                ModelPackManifestCodec.MaximumManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var manifest = ModelPackManifestCodec.DeserializeManifest(manifestBytes);
            if (!string.Equals(manifest.PackId, descriptor.PackId, StringComparison.Ordinal) ||
                manifest.Direction != descriptor.Direction)
            {
                throw new TranslationModelInvalidException("模型包身份或翻译方向与受信目录不一致");
            }

            ValidateArchiveEntries(archive, manifest);
            unpackContainer = Path.Combine(stagingRoot, $"unpack-{Guid.NewGuid():N}");
            unpackDirectory = Path.Combine(
                unpackContainer,
                ModelPackPathPolicy.PackDirectoryName(manifest.PackId));
            Directory.CreateDirectory(unpackDirectory);
            var extractedBytes = 0L;
            var totalExtractedBytes = manifest.Artifacts.Sum(artifact => artifact.Bytes);
            foreach (var artifact in manifest.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(artifact.Path)
                    ?? throw new TranslationModelInvalidException($"模型包缺少文件：{artifact.Path}");
                var outputPath = ModelPackPathPolicy.ResolveWithin(unpackDirectory, artifact.Path);
                var outputDirectory = Path.GetDirectoryName(outputPath)
                    ?? throw new TranslationModelInvalidException($"模型文件路径无效：{artifact.Path}");
                Directory.CreateDirectory(outputDirectory);

                await using var entryStream = entry.Open();
                await using var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualHash = await CopyArtifactAsync(
                    entryStream,
                    output,
                    artifact,
                    bytes =>
                    {
                        progress?.Report(new ModelInstallProgress(
                            ModelInstallStage.Extracting,
                            extractedBytes + bytes,
                            totalExtractedBytes,
                            $"正在安装 {artifact.Path}"));
                    },
                    cancellationToken).ConfigureAwait(false);
                extractedBytes = checked(extractedBytes + artifact.Bytes);
                if (!HashUtility.EqualsSha256(artifact.Sha256, actualHash))
                {
                    throw new TranslationModelInvalidException($"模型文件 SHA-256 校验失败：{artifact.Path}");
                }
            }

            var manifestPath = Path.Combine(unpackDirectory, ModelPackPathPolicy.ManifestFileName);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

            progress?.Report(new ModelInstallProgress(
                ModelInstallStage.VerifyingFiles,
                totalExtractedBytes,
                totalExtractedBytes,
                "正在校验模型文件"));

            var staged = await _catalog.ValidatePackDirectoryAsync(
                unpackDirectory,
                manifestHash,
                verifyArtifactHashes: true,
                cancellationToken).ConfigureAwait(false);
            var packsDirectory = PrepareOwnedDirectory(_catalog.RootDirectory, "packs");
            var finalDirectory = Path.Combine(packsDirectory, ModelPackPathPolicy.PackDirectoryName(manifest.PackId));
            var installed = await CommitAndActivateAsync(
                staged,
                finalDirectory,
                manifestHash,
                totalExtractedBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelInstallProgress(
                ModelInstallStage.Complete,
                totalExtractedBytes,
                totalExtractedBytes,
                "离线模型已安装"));
            return installed;
        }
        catch (InvalidDataException error)
        {
            throw new TranslationModelInvalidException("模型包不是有效的 ZIP 文件", error);
        }
        finally
        {
            _ = BestEffortFileSystemCleanup.TryRun(
                "offline translation model staging archive",
                () =>
                {
                    if (File.Exists(archivePath))
                    {
                        File.Delete(archivePath);
                    }
                });
            _ = BestEffortFileSystemCleanup.TryRun(
                "offline translation model unpack directory",
                () =>
                {
                    if (unpackContainer is not null && Directory.Exists(unpackContainer))
                    {
                        DeleteDirectoryTreeWithoutFollowingReparsePoints(unpackContainer);
                    }
                });
        }
    }

    private async Task<InstalledTranslationModel> CommitAndActivateAsync(
        InstalledTranslationModel staged,
        string finalDirectory,
        string manifestHash,
        long totalExtractedBytes,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var commitGate = CommitGates.GetOrAdd(finalDirectory, static _ => new SemaphoreSlim(1, 1));
        await commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InstalledTranslationModel? existing = null;
            if (Directory.Exists(finalDirectory))
            {
                try
                {
                    existing = await _catalog.ValidatePackDirectoryAsync(
                        finalDirectory,
                        manifestHash,
                        verifyArtifactHashes: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TranslationModelInvalidException)
                {
                    // A fully verified staged copy is allowed to repair a damaged
                    // directory with the same packId. The old directory is moved
                    // aside first and restored if either the rename, validation,
                    // or active-pointer update fails.
                    EnsureOrdinaryDirectory(finalDirectory, "待修复模型目录");
                }
            }

            if (existing is not null)
            {
                ReportActivating(progress, totalExtractedBytes);
                await _catalog.ActivateAsync(existing, cancellationToken).ConfigureAwait(false);
                return existing;
            }

            var quarantineRoot = PrepareOwnedDirectory(_catalog.RootDirectory, ".quarantine");
            var backupDirectory = Directory.Exists(finalDirectory)
                ? Path.Combine(
                    quarantineRoot,
                    $"{Path.GetFileName(finalDirectory)}-{Guid.NewGuid():N}.backup")
                : null;
            var stagedDirectory = staged.DirectoryPath;
            var stagedMoved = false;
            var backupMoved = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (backupDirectory is not null)
                {
                    Directory.Move(finalDirectory, backupDirectory);
                    backupMoved = true;
                }

                Directory.Move(stagedDirectory, finalDirectory);
                stagedMoved = true;
                var installed = await _catalog.ValidatePackDirectoryAsync(
                    finalDirectory,
                    manifestHash,
                    verifyArtifactHashes: true,
                    cancellationToken).ConfigureAwait(false);
                ReportActivating(progress, totalExtractedBytes);
                await _catalog.ActivateAsync(installed, cancellationToken).ConfigureAwait(false);

                if (backupMoved && backupDirectory is not null && Directory.Exists(backupDirectory))
                {
                    try
                    {
                        DeleteDirectoryTreeWithoutFollowingReparsePoints(backupDirectory);
                    }
                    catch (IOException)
                    {
                        // Activation already committed successfully. Keeping an
                        // isolated backup is safer than undoing a valid repair.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // See the IOException case above.
                    }
                }
                return installed;
            }
            catch (Exception installError)
            {
                Exception? rollbackError = null;
                try
                {
                    if (stagedMoved && Directory.Exists(finalDirectory))
                    {
                        Directory.Move(finalDirectory, stagedDirectory);
                    }
                    if (backupMoved && backupDirectory is not null && Directory.Exists(backupDirectory))
                    {
                        Directory.Move(backupDirectory, finalDirectory);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    rollbackError = error;
                }

                if (rollbackError is not null)
                {
                    throw new TranslationModelInvalidException(
                        "离线模型替换失败，旧模型已隔离保留但自动回滚未完成",
                        new AggregateException(installError, rollbackError));
                }
                throw;
            }
        }
        finally
        {
            commitGate.Release();
        }
    }

    private static void ReportActivating(
        IProgress<ModelInstallProgress>? progress,
        long totalExtractedBytes) =>
        progress?.Report(new ModelInstallProgress(
            ModelInstallStage.Activating,
            totalExtractedBytes,
            totalExtractedBytes,
            "正在启用离线模型"));

    private static void EnsureOrdinaryDirectory(string path, string description)
    {
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TranslationModelInvalidException($"{description}不能是符号链接或重解析点");
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string path)
    {
        EnsureOrdinaryDirectory(path, "待清理模型目录");
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryTreeWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(path);
    }

    private static void ValidateDescriptor(ModelPackInstallDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.PackId) || descriptor.PackId.Length > 128)
        {
            throw new TranslationModelInvalidException("模型包 packId 无效");
        }
        if (!HashUtility.IsSha256(descriptor.ArchiveSha256))
        {
            throw new TranslationModelInvalidException("模型包目录缺少有效的 SHA-256");
        }
        if (descriptor.ArchiveBytes is <= 0 or > MaximumArchiveBytes)
        {
            throw new TranslationModelInvalidException("模型包下载大小超过安全限制");
        }
    }

    private static string PrepareOwnedDirectory(string root, string child)
    {
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TranslationModelInvalidException("模型根目录不能是符号链接或重解析点");
        }
        var path = Path.Combine(root, child);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TranslationModelInvalidException("模型安装目录不能是符号链接或重解析点");
        }
        return path;
    }

    private static async Task<string> CopyArchiveToOwnedFileAsync(
        Stream input,
        string outputPath,
        ModelPackInstallDescriptor descriptor,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var bytesRead = 0L;
        try
        {
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                bytesRead = checked(bytesRead + count);
                if (bytesRead > descriptor.ArchiveBytes || bytesRead > MaximumArchiveBytes)
                {
                    throw new TranslationModelInvalidException("模型包下载内容超过声明大小");
                }
                hash.AppendData(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModelInstallProgress(
                    ModelInstallStage.Downloading,
                    bytesRead,
                    descriptor.ArchiveBytes,
                    "正在下载离线翻译模型"));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (bytesRead != descriptor.ArchiveBytes)
        {
            throw new TranslationModelInvalidException("模型包下载不完整");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ZipArchiveEntry FindSingleManifestEntry(ZipArchive archive)
    {
        var entries = archive.Entries
            .Where(entry => string.Equals(
                entry.FullName,
                ModelPackPathPolicy.ManifestFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return entries.Length == 1
            ? entries[0]
            : throw new TranslationModelInvalidException("模型包必须且只能包含一个 manifest.json");
    }

    private static void ValidateArchiveEntries(
        ZipArchive archive,
        TranslationModelPackManifest manifest)
    {
        if (archive.Entries.Count is 0 or > 512)
        {
            throw new TranslationModelInvalidException("模型包 ZIP 文件数量无效");
        }
        var expected = manifest.Artifacts
            .Select(artifact => artifact.Path)
            .Append(ModelPackPathPolicy.ManifestFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                _ = ModelPackPathPolicy.NormalizeRelativePath(entry.FullName.TrimEnd('/'));
                continue;
            }
            var normalized = ModelPackPathPolicy.NormalizeRelativePath(entry.FullName);
            if (!string.Equals(normalized, entry.FullName, StringComparison.Ordinal) ||
                !expected.Contains(normalized) ||
                !seen.Add(normalized))
            {
                throw new TranslationModelInvalidException($"模型包包含未声明、重复或不安全的文件：{entry.FullName}");
            }
        }
        if (!seen.SetEquals(expected))
        {
            throw new TranslationModelInvalidException("模型包文件与 manifest 声明不一致");
        }

        var artifacts = manifest.Artifacts.ToDictionary(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal)))
        {
            if (artifacts.TryGetValue(entry.FullName, out var artifact) && entry.Length != artifact.Bytes)
            {
                throw new TranslationModelInvalidException($"模型文件大小与 manifest 不一致：{entry.FullName}");
            }
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length is <= 0 || entry.Length > maximumBytes)
        {
            throw new TranslationModelInvalidException($"{entry.FullName} 大小无效");
        }
        await using var stream = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (output.Length != entry.Length)
        {
            throw new TranslationModelInvalidException($"{entry.FullName} 内容不完整");
        }
        return output.ToArray();
    }

    private static async Task<string> CopyArtifactAsync(
        Stream input,
        Stream output,
        ModelArtifactManifest artifact,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var bytesRead = 0L;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                bytesRead = checked(bytesRead + count);
                if (bytesRead > artifact.Bytes)
                {
                    throw new TranslationModelInvalidException($"模型文件超过声明大小：{artifact.Path}");
                }
                hash.AppendData(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                report(bytesRead);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (bytesRead != artifact.Bytes)
        {
            throw new TranslationModelInvalidException($"模型文件内容不完整：{artifact.Path}");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
