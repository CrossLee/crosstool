using System.Text.Json;
using Crosio.Windows.Intelligence.Translation;

namespace Crosio.Windows.Translation;

public enum OfflineModelState
{
    Missing,
    Ready,
    Invalid,
}

public sealed record OfflineModelStatus(
    TranslationDirection Direction,
    OfflineModelState State,
    string? PackId,
    string? Version,
    string? ErrorMessage,
    string? Attribution);

public sealed record InstalledTranslationModel(
    string DirectoryPath,
    TranslationModelPackManifest Manifest,
    string ManifestSha256);

public sealed class OfflineModelCatalog
{
    private const int MaximumPointerBytes = 64 * 1024;
    private readonly string _rootDirectory;

    public OfflineModelCatalog(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public async Task<OfflineModelStatus> GetStatusAsync(
        TranslationDirection direction,
        bool verifyArtifactHashes = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var installed = await ResolveActiveAsync(
                direction,
                verifyArtifactHashes,
                cancellationToken).ConfigureAwait(false);
            return new OfflineModelStatus(
                direction,
                OfflineModelState.Ready,
                installed.Manifest.PackId,
                installed.Manifest.Version,
                null,
                installed.Manifest.License.Attribution);
        }
        catch (TranslationModelMissingException error)
        {
            return new OfflineModelStatus(direction, OfflineModelState.Missing, null, null, error.Message, null);
        }
        catch (TranslationModelInvalidException error)
        {
            return new OfflineModelStatus(direction, OfflineModelState.Invalid, null, null, error.Message, null);
        }
    }

    public async Task<InstalledTranslationModel> ResolveActiveAsync(
        TranslationDirection direction,
        bool verifyArtifactHashes = true,
        CancellationToken cancellationToken = default)
    {
        var pointerPath = GetPointerPath(direction);
        if (!File.Exists(pointerPath))
        {
            throw new TranslationModelMissingException(direction);
        }

        byte[] pointerBytes;
        try
        {
            pointerBytes = await ReadBoundedFileAsync(
                pointerPath,
                MaximumPointerBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TranslationModelInvalidException("无法读取离线模型索引", error);
        }

        var pointer = ModelPackManifestCodec.DeserializePointer(pointerBytes);
        ValidatePointer(pointer, direction);
        var packDirectory = Path.Combine(_rootDirectory, "packs", pointer.DirectoryName);
        var installed = await ValidatePackDirectoryAsync(
            packDirectory,
            pointer.ManifestSha256,
            verifyArtifactHashes,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(installed.Manifest.PackId, pointer.PackId, StringComparison.Ordinal) ||
            installed.Manifest.Direction != direction)
        {
            throw new TranslationModelInvalidException("离线模型索引指向了错误的模型或翻译方向");
        }
        return installed;
    }

    internal async Task<InstalledTranslationModel> ValidatePackDirectoryAsync(
        string packDirectory,
        string expectedManifestSha256,
        bool verifyArtifactHashes,
        CancellationToken cancellationToken)
    {
        if (!HashUtility.IsSha256(expectedManifestSha256))
        {
            throw new TranslationModelInvalidException("离线模型索引中的 manifest 哈希无效");
        }
        EnsureNotReparsePoint(packDirectory, "模型目录");
        var manifestPath = ModelPackPathPolicy.ResolveWithin(packDirectory, ModelPackPathPolicy.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new TranslationModelInvalidException("模型目录缺少 manifest.json");
        }
        EnsureNotReparsePoint(manifestPath, "模型 manifest");

        var manifestBytes = await ReadBoundedFileAsync(
            manifestPath,
            ModelPackManifestCodec.MaximumManifestBytes,
            cancellationToken).ConfigureAwait(false);
        var actualManifestSha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!HashUtility.EqualsSha256(expectedManifestSha256, actualManifestSha256))
        {
            throw new TranslationModelInvalidException("模型 manifest SHA-256 校验失败");
        }

        var manifest = ModelPackManifestCodec.DeserializeManifest(manifestBytes);
        var expectedDirectoryName = ModelPackPathPolicy.PackDirectoryName(manifest.PackId);
        if (!string.Equals(
                new DirectoryInfo(packDirectory).Name,
                expectedDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationModelInvalidException("模型目录与 packId 不匹配");
        }

        if (verifyArtifactHashes)
        {
            await ValidateArtifactsAsync(packDirectory, manifest, cancellationToken).ConfigureAwait(false);
        }
        return new InstalledTranslationModel(packDirectory, manifest, actualManifestSha256);
    }

    internal async Task ActivateAsync(
        InstalledTranslationModel model,
        CancellationToken cancellationToken)
    {
        var activeDirectory = Path.Combine(_rootDirectory, "active");
        Directory.CreateDirectory(activeDirectory);
        EnsureNotReparsePoint(activeDirectory, "模型索引目录");

        var pointer = new ActiveModelPointer
        {
            SchemaVersion = TranslationModelPackManifest.CurrentSchemaVersion,
            PackId = model.Manifest.PackId,
            DirectoryName = new DirectoryInfo(model.DirectoryPath).Name,
            ManifestSha256 = model.ManifestSha256,
        };
        var pointerBytes = ModelPackManifestCodec.SerializePointer(pointer);
        var pointerPath = GetPointerPath(model.Manifest.Direction);
        var temporaryPath = Path.Combine(activeDirectory, $".{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(pointerBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, pointerPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A best-effort cleanup failure must not hide the activation result.
            }
        }
    }

    private async Task ValidateArtifactsAsync(
        string packDirectory,
        TranslationModelPackManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in manifest.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ModelPackPathPolicy.ResolveWithin(packDirectory, artifact.Path);
            if (!File.Exists(path))
            {
                throw new TranslationModelInvalidException($"模型文件缺失：{artifact.Path}");
            }
            EnsureNotReparsePoint(path, $"模型文件 {artifact.Path}");
            var info = new FileInfo(path);
            if (info.Length != artifact.Bytes)
            {
                throw new TranslationModelInvalidException($"模型文件大小校验失败：{artifact.Path}");
            }
            var hash = await HashUtility.ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!HashUtility.EqualsSha256(artifact.Sha256, hash))
            {
                throw new TranslationModelInvalidException($"模型文件 SHA-256 校验失败：{artifact.Path}");
            }
        }
    }

    private string GetPointerPath(TranslationDirection direction) =>
        Path.Combine(_rootDirectory, "active", $"{ModelPackPathPolicy.DirectionKey(direction)}.json");

    private static void ValidatePointer(ActiveModelPointer pointer, TranslationDirection direction)
    {
        if (pointer.SchemaVersion != TranslationModelPackManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(pointer.PackId) ||
            string.IsNullOrWhiteSpace(pointer.DirectoryName) ||
            pointer.DirectoryName != ModelPackPathPolicy.PackDirectoryName(pointer.PackId) ||
            !HashUtility.IsSha256(pointer.ManifestSha256))
        {
            throw new TranslationModelInvalidException(
                $"{ModelPackPathPolicy.DirectionKey(direction)} 离线模型索引无效");
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 || info.Length > maximumBytes)
        {
            throw new TranslationModelInvalidException($"文件大小无效：{info.Name}");
        }
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new TranslationModelInvalidException($"{description}不存在");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TranslationModelInvalidException($"{description}不能是符号链接或重解析点");
        }
    }
}
