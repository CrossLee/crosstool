using Crosio.Windows.Intelligence.Translation;
using Xunit;

namespace Crosio.Windows.Translation.Tests;

public sealed class OfflineModelPackInstallerTests
{
    [Fact]
    public async Task InstallsVerifiesAndAtomicallyActivatesPack()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create();
        var progress = new SynchronousProgress<ModelInstallProgress>();

        await using var archive = new MemoryStream(package.Bytes);
        var installed = await installer.InstallArchiveAsync(archive, package.Descriptor, progress);
        var status = await catalog.GetStatusAsync(TranslationDirection.ChineseToEnglish);

        Assert.Equal(package.Manifest.PackId, installed.Manifest.PackId);
        Assert.Equal(OfflineModelState.Ready, status.State);
        Assert.Equal(package.Manifest.PackId, status.PackId);
        Assert.Contains("Helsinki-NLP", status.Attribution, StringComparison.Ordinal);
        Assert.Equal(ModelInstallStage.Complete, progress.Values[^1].Stage);
        Assert.Equal(1, progress.Values[^1].Fraction);
    }

    [Fact]
    public async Task ReinstallingSameVerifiedPackIsIdempotent()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create();

        await installer.InstallArchiveAsync(new MemoryStream(package.Bytes), package.Descriptor);
        var second = await installer.InstallArchiveAsync(new MemoryStream(package.Bytes), package.Descriptor);

        Assert.Equal(package.Manifest.PackId, second.Manifest.PackId);
        Assert.Single(Directory.EnumerateDirectories(System.IO.Path.Combine(root.Path, "packs")));
    }

    [Fact]
    public async Task ReinstallingSamePackIdRepairsCorruptedArtifactsAtomically()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create();
        var installed = await installer.InstallArchiveAsync(
            new MemoryStream(package.Bytes),
            package.Descriptor);
        var encoderPath = System.IO.Path.Combine(installed.DirectoryPath, "encoder_model.onnx");
        var originalEncoder = await File.ReadAllBytesAsync(encoderPath);
        await File.WriteAllBytesAsync(encoderPath, Enumerable.Repeat((byte)'x', originalEncoder.Length).ToArray());

        Assert.Equal(
            OfflineModelState.Invalid,
            (await catalog.GetStatusAsync(package.Manifest.Direction, verifyArtifactHashes: true)).State);

        var repaired = await installer.InstallArchiveAsync(
            new MemoryStream(package.Bytes),
            package.Descriptor);

        Assert.Equal(installed.DirectoryPath, repaired.DirectoryPath);
        Assert.Equal(originalEncoder, await File.ReadAllBytesAsync(encoderPath));
        Assert.Equal(
            OfflineModelState.Ready,
            (await catalog.GetStatusAsync(package.Manifest.Direction, verifyArtifactHashes: true)).State);
        Assert.Single(Directory.EnumerateDirectories(System.IO.Path.Combine(root.Path, "packs")));
        Assert.Empty(EnumerateChildrenIfPresent(System.IO.Path.Combine(root.Path, ".quarantine")));
        Assert.Empty(EnumerateChildrenIfPresent(System.IO.Path.Combine(root.Path, ".staging")));
    }

    [Fact]
    public async Task CancelledRepairRollsBackTheQuarantinedDirectory()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create();
        var installed = await installer.InstallArchiveAsync(
            new MemoryStream(package.Bytes),
            package.Descriptor);
        var encoderPath = System.IO.Path.Combine(installed.DirectoryPath, "encoder_model.onnx");
        var original = await File.ReadAllBytesAsync(encoderPath);
        var corrupted = Enumerable.Repeat((byte)'x', original.Length).ToArray();
        await File.WriteAllBytesAsync(encoderPath, corrupted);
        using var cancellation = new CancellationTokenSource();
        var progress = new ActionProgress<ModelInstallProgress>(value =>
        {
            if (value.Stage == ModelInstallStage.Activating)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallArchiveAsync(
                new MemoryStream(package.Bytes),
                package.Descriptor,
                progress,
                cancellation.Token));

        Assert.Equal(corrupted, await File.ReadAllBytesAsync(encoderPath));
        Assert.Equal(
            OfflineModelState.Invalid,
            (await catalog.GetStatusAsync(package.Manifest.Direction, verifyArtifactHashes: true)).State);
        Assert.Single(Directory.EnumerateDirectories(System.IO.Path.Combine(root.Path, "packs")));
        Assert.Empty(EnumerateChildrenIfPresent(System.IO.Path.Combine(root.Path, ".quarantine")));
        Assert.Empty(EnumerateChildrenIfPresent(System.IO.Path.Combine(root.Path, ".staging")));
    }

    [Fact]
    public async Task InstallsAnExplicitlySelectedLocalPackageThroughStrictValidation()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create(packId: "local-user-selected-pack");
        Directory.CreateDirectory(root.Path);
        var archivePath = System.IO.Path.Combine(root.Path, "model.zip");
        await File.WriteAllBytesAsync(archivePath, package.Bytes);

        var installed = await installer.InstallLocalArchiveAsync(archivePath);

        Assert.Equal(package.Manifest.PackId, installed.Manifest.PackId);
        Assert.Equal(
            OfflineModelState.Ready,
            (await catalog.GetStatusAsync(package.Manifest.Direction)).State);
    }

    [Fact]
    public async Task RejectsArchiveHashMismatchWithoutActivating()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create();
        var wrongDescriptor = package.Descriptor with { ArchiveSha256 = new string('0', 64) };

        await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.InstallArchiveAsync(new MemoryStream(package.Bytes), wrongDescriptor));
        var status = await catalog.GetStatusAsync(TranslationDirection.ChineseToEnglish);

        Assert.Equal(OfflineModelState.Missing, status.State);
    }

    [Fact]
    public async Task RejectsTamperedArtifactWithoutActivating()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create(corruptEncoderAfterManifest: true);

        var error = await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.InstallArchiveAsync(new MemoryStream(package.Bytes), package.Descriptor));
        var status = await catalog.GetStatusAsync(TranslationDirection.ChineseToEnglish);

        Assert.Contains("encoder_model.onnx", error.Message, StringComparison.Ordinal);
        Assert.Equal(OfflineModelState.Missing, status.State);
    }

    [Fact]
    public async Task RejectsUnexpectedArchiveEntry()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create(addUnexpectedFile: true);

        var error = await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.InstallArchiveAsync(new MemoryStream(package.Bytes), package.Descriptor));

        Assert.Contains("未声明", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedReplacementKeepsPreviouslyActivePack()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var valid = TestModelPack.Create(packId: "valid-pack");
        await installer.InstallArchiveAsync(new MemoryStream(valid.Bytes), valid.Descriptor);
        var invalid = TestModelPack.Create(packId: "invalid-pack", corruptEncoderAfterManifest: true);

        await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.InstallArchiveAsync(new MemoryStream(invalid.Bytes), invalid.Descriptor));
        var active = await catalog.ResolveActiveAsync(TranslationDirection.ChineseToEnglish);

        Assert.Equal("valid-pack", active.Manifest.PackId);
    }

    [Fact]
    public async Task CancellationDoesNotActivatePartialPack()
    {
        using var root = new TemporaryModelRoot();
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = new OfflineModelPackInstaller(catalog);
        var package = TestModelPack.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallArchiveAsync(
                new MemoryStream(package.Bytes),
                package.Descriptor,
                cancellationToken: cancellation.Token));
        var status = await catalog.GetStatusAsync(TranslationDirection.ChineseToEnglish);

        Assert.Equal(OfflineModelState.Missing, status.State);
    }

    [Fact]
    public async Task DownloadRequestContainsNoUserTextAndUsesGetOnly()
    {
        using var root = new TemporaryModelRoot();
        var package = TestModelPack.Create();
        var handler = new RecordingModelHandler(package.Bytes);
        using var httpClient = new HttpClient(handler);
        using var installer = new OfflineModelPackInstaller(new OfflineModelCatalog(root.Path), httpClient);
        var descriptor = new ModelPackDownloadDescriptor
        {
            DownloadUri = new Uri("https://models.example.test/opus/zh-en.zip"),
            Package = package.Descriptor,
        };

        await installer.DownloadAndInstallAsync(descriptor);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(descriptor.DownloadUri, handler.RequestUri);
        Assert.False(handler.HadRequestContent);
    }

    [Fact]
    public async Task DisposeAsyncCancelsAndDrainsAnActiveImportBeforeClosingResources()
    {
        using var root = new TemporaryModelRoot();
        var installer = new OfflineModelPackInstaller(new OfflineModelCatalog(root.Path));
        await using var input = new BlockingReadStream();
        var descriptor = new ModelPackInstallDescriptor
        {
            PackId = "blocked-import",
            Direction = TranslationDirection.ChineseToEnglish,
            ArchiveBytes = 1,
            ArchiveSha256 = new string('0', 64),
        };

        var install = installer.InstallArchiveAsync(input, descriptor);
        await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = installer.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(EnumerateChildrenIfPresent(System.IO.Path.Combine(root.Path, ".staging")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            installer.InstallArchiveAsync(new MemoryStream([0]), descriptor));
    }

    [Fact]
    public void BestEffortCleanupPreservesPrimaryOutcomeWhenDeletionFails()
    {
        foreach (var deletionError in new Exception[]
                 {
                     new IOException("file is still locked"),
                     new UnauthorizedAccessException("directory is protected"),
                 })
        {
            var debugMessages = new List<string>();

            var completed = BestEffortFileSystemCleanup.TryRun(
                "test staging path",
                () => throw deletionError,
                debugMessages.Add);

            Assert.False(completed);
            var message = Assert.Single(debugMessages);
            Assert.Contains("test staging path", message, StringComparison.Ordinal);
            Assert.Contains(deletionError.Message, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BestEffortCleanupReportsSuccessfulDeletion()
    {
        var cleanupRan = false;

        var completed = BestEffortFileSystemCleanup.TryRun(
            "test staging path",
            () => cleanupRan = true);

        Assert.True(completed);
        Assert.True(cleanupRan);
    }

    private sealed class RecordingModelHandler(byte[] responseBytes) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public bool HadRequestContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Method = request.Method;
            RequestUri = request.RequestUri;
            HadRequestContent = request.Content is not null;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(responseBytes),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateChildrenIfPresent(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path)
            : [];

    private sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
