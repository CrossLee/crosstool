using System.Net;
using System.Security.Cryptography;
using System.Text;
using Crosio.Windows.Intelligence.Translation;
using Xunit;

namespace Crosio.Windows.Translation.Tests;

public sealed class OfficialTranslationModelInstallerTests
{
    [Fact]
    public void ProductionCatalogPinsBothExactRevisionsAndLicenses()
    {
        var chineseToEnglish = OfficialTranslationModelInstaller.GetModelInfo(
            TranslationDirection.ChineseToEnglish);
        var englishToChinese = OfficialTranslationModelInstaller.GetModelInfo(
            TranslationDirection.EnglishToChinese);

        Assert.Equal("8e3032ebeebbacda779fe95efa64c03b962f83f3", chineseToEnglish.Revision);
        Assert.Equal("CC-BY-4.0", chineseToEnglish.LicenseSpdxId);
        Assert.Equal(249135517, chineseToEnglish.DownloadBytes);
        Assert.Equal("046f55aec303cdee3e0318604406d4df20f1e8ea", englishToChinese.Revision);
        Assert.Equal("Apache-2.0", englishToChinese.LicenseSpdxId);
        Assert.Equal(116112032, englishToChinese.DownloadBytes);

        var zhDefinition = OfficialTranslationModelDefinitions.All[
            TranslationDirection.ChineseToEnglish];
        var enDefinition = OfficialTranslationModelDefinitions.All[
            TranslationDirection.EnglishToChinese];
        Assert.Equal(6, zhDefinition.Artifacts.Count);
        Assert.Contains(
            zhDefinition.Artifacts,
            artifact => artifact.LocalPath == "decoder_model.onnx" &&
                artifact.Bytes == 192882669 &&
                artifact.Sha256 == "6ec4ee5c028efb856e933d5d0732bac28f0914b34e652978fb201864931c49a6" &&
                artifact.DownloadUri!.AbsoluteUri.Contains(
                    "/resolve/8e3032ebeebbacda779fe95efa64c03b962f83f3/onnx/decoder_model_quantized.onnx",
                    StringComparison.Ordinal));
        AssertPinnedNetworkArtifacts(zhDefinition, "onnx-community/opus-mt-zh-en");
        AssertPinnedNetworkArtifacts(enDefinition, "Xenova/opus-mt-en-zh");
        AssertEmbeddedLicense(
            zhDefinition,
            OfficialTranslationModelEmbeddedArtifacts.CreativeCommonsBy40License,
            18657,
            "9ba9550ad48438d0836ddab3da480b3b69ffa0aac7b7878b5a0039e7ab429411");
        AssertEmbeddedLicense(
            enDefinition,
            OfficialTranslationModelEmbeddedArtifacts.Apache20License,
            11358,
            "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30");
    }

    [Fact]
    public async Task DownloadsOnlyNetworkArtifactsInjectsEmbeddedLicenseAndUsesStrictAtomicInstaller()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var handler = new ArtifactHandler(fixture.Payloads);
        using var client = new HttpClient(handler);
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = CreateInstaller(catalog, client, fixture.Definition);

        var installed = await installer.DownloadAndInstallAsync(fixture.Definition.Direction);

        Assert.Equal(fixture.Definition.PackId, installed.Manifest.PackId);
        Assert.Equal(OfflineModelState.Ready, (await catalog.GetStatusAsync(fixture.Definition.Direction)).State);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(request.HadContent);
            Assert.Equal("identity", request.AcceptEncoding);
        });
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri.AbsolutePath.EndsWith("LICENSE.txt", StringComparison.Ordinal));
        foreach (var artifact in fixture.Definition.Artifacts)
        {
            var installedPath = Path.Combine(
                installed.DirectoryPath,
                artifact.LocalPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(fixture.Files[artifact.LocalPath], await File.ReadAllBytesAsync(installedPath));
        }
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
    }

    [Fact]
    public async Task ExplicitDownloadRepairsAHashInvalidPackWithTheSamePackId()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var handler = new ArtifactHandler(fixture.Payloads);
        using var client = new HttpClient(handler);
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = CreateInstaller(catalog, client, fixture.Definition);
        var installed = await installer.DownloadAndInstallAsync(fixture.Definition.Direction);
        var encoder = fixture.Definition.Artifacts.Single(
            artifact => artifact.LocalPath == "encoder_model.onnx");
        var encoderPath = Path.Combine(installed.DirectoryPath, encoder.LocalPath);
        var original = fixture.Files[encoder.LocalPath];
        await File.WriteAllBytesAsync(encoderPath, Enumerable.Repeat((byte)'x', original.Length).ToArray());

        Assert.Equal(
            OfflineModelState.Invalid,
            (await catalog.GetStatusAsync(
                fixture.Definition.Direction,
                verifyArtifactHashes: true)).State);

        var repaired = await installer.DownloadAndInstallAsync(fixture.Definition.Direction);

        Assert.Equal(installed.DirectoryPath, repaired.DirectoryPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(encoderPath));
        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(
            OfflineModelState.Ready,
            (await catalog.GetStatusAsync(
                fixture.Definition.Direction,
                verifyArtifactHashes: true)).State);
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
        Assert.Empty(EnumerateChildrenIfPresent(Path.Combine(root.Path, ".quarantine")));
    }

    [Fact]
    public async Task RejectsArtifactHashMismatchAndCleansTemporaryFiles()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var first = fixture.Definition.Artifacts[0];
        var invalid = fixture.Definition with
        {
            Artifacts = [first with { Sha256 = new string('0', 64) }, .. fixture.Definition.Artifacts.Skip(1)],
        };
        var handler = new ArtifactHandler(fixture.Payloads);
        using var client = new HttpClient(handler);
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = CreateInstaller(catalog, client, invalid);

        var error = await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.DownloadAndInstallAsync(invalid.Direction));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Equal(OfflineModelState.Missing, (await catalog.GetStatusAsync(invalid.Direction)).State);
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
    }

    [Fact]
    public async Task RejectsArtifactSizeMismatchBeforeWritingBody()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var first = fixture.Definition.Artifacts[0];
        var invalid = fixture.Definition with
        {
            Artifacts = [first with { Bytes = first.Bytes + 1 }, .. fixture.Definition.Artifacts.Skip(1)],
        };
        var handler = new ArtifactHandler(fixture.Payloads);
        using var client = new HttpClient(handler);
        var catalog = new OfflineModelCatalog(root.Path);
        using var installer = CreateInstaller(catalog, client, invalid);

        var error = await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.DownloadAndInstallAsync(invalid.Direction));

        Assert.Contains("大小", error.Message, StringComparison.Ordinal);
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
    }

    [Fact]
    public async Task RejectsNonHttpsFinalRedirect()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var handler = new ArtifactHandler(
            fixture.Payloads,
            finalUri: new Uri("http://unsafe-cdn.example.test/model.bin"));
        using var client = new HttpClient(handler);
        using var installer = CreateInstaller(new OfflineModelCatalog(root.Path), client, fixture.Definition);

        var error = await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.DownloadAndInstallAsync(fixture.Definition.Direction));

        Assert.Contains("非 HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
    }

    [Fact]
    public async Task RejectsTraversalPathBeforeNetworkOrFilesystemEscape()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var invalid = fixture.Definition with
        {
            Artifacts =
            [
                fixture.Definition.Artifacts[0] with { LocalPath = "../outside.onnx" },
                .. fixture.Definition.Artifacts.Skip(1),
            ],
        };
        var handler = new ArtifactHandler(fixture.Payloads);
        using var client = new HttpClient(handler);
        using var installer = CreateInstaller(new OfflineModelCatalog(root.Path), client, invalid);

        await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.DownloadAndInstallAsync(invalid.Direction));

        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root.Path)!, "outside.onnx")));
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
    }

    [Fact]
    public async Task RejectsCatalogWhoseDeclaredTotalExceedsSafetyLimit()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var first = fixture.Definition.Artifacts[0];
        var invalid = fixture.Definition with
        {
            Artifacts =
            [
                first with { Bytes = OfficialTranslationModelInstaller.MaximumDownloadBytes },
                .. fixture.Definition.Artifacts.Skip(1),
            ],
        };
        var handler = new ArtifactHandler(fixture.Payloads);
        using var client = new HttpClient(handler);
        using var installer = CreateInstaller(new OfflineModelCatalog(root.Path), client, invalid);

        var error = await Assert.ThrowsAsync<TranslationModelInvalidException>(
            () => installer.DownloadAndInstallAsync(invalid.Direction));

        Assert.Contains("安全限制", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationAbortsNetworkAndRemovesSessionDirectory()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        using var installer = CreateInstaller(
            new OfflineModelCatalog(root.Path),
            client,
            fixture.Definition);
        using var cancellation = new CancellationTokenSource();

        var installTask = installer.DownloadAndInstallAsync(
            fixture.Definition.Direction,
            cancellationToken: cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installTask);
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
    }

    [Fact]
    public async Task DisposeAsyncCancelsAndDrainsAnActiveDownloadBeforeClosingResources()
    {
        using var root = new TemporaryModelRoot();
        var fixture = OfficialFixture.Create();
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        var installer = CreateInstaller(
            new OfflineModelCatalog(root.Path),
            client,
            fixture.Definition);

        var install = installer.DownloadAndInstallAsync(fixture.Definition.Direction);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = installer.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(EnumerateTemporaryFiles(root.Path));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => installer.DownloadAndInstallAsync(fixture.Definition.Direction));
    }

    private static OfficialTranslationModelInstaller CreateInstaller(
        OfflineModelCatalog catalog,
        HttpClient client,
        OfficialTranslationModelDefinition definition) =>
        new(
            catalog,
            client,
            new Dictionary<TranslationDirection, OfficialTranslationModelDefinition>
            {
                [definition.Direction] = definition,
            });

    private static IEnumerable<string> EnumerateTemporaryFiles(string root)
    {
        var path = Path.Combine(root, ".official-downloads");
        return Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
            : [];
    }

    private static IEnumerable<string> EnumerateChildrenIfPresent(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path)
            : [];

    private static void AssertPinnedNetworkArtifacts(
        OfficialTranslationModelDefinition definition,
        string repository)
    {
        var networkArtifacts = definition.Artifacts
            .Where(artifact => artifact.DownloadUri is not null)
            .ToArray();
        Assert.Equal(5, networkArtifacts.Length);
        Assert.All(networkArtifacts, artifact =>
        {
            var uri = artifact.DownloadUri!;
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Equal("huggingface.co", uri.Host);
            Assert.Contains(
                $"/{repository}/resolve/{definition.Revision}/",
                uri.AbsolutePath,
                StringComparison.Ordinal);
            Assert.Equal("?download=true", uri.Query);
            Assert.Null(artifact.EmbeddedResourceId);
        });
    }

    private static void AssertEmbeddedLicense(
        OfficialTranslationModelDefinition definition,
        string resourceId,
        long expectedBytes,
        string expectedSha256)
    {
        var artifact = Assert.Single(
            definition.Artifacts,
            value => value.LocalPath == "LICENSE.txt");
        Assert.Null(artifact.DownloadUri);
        Assert.Equal(resourceId, artifact.EmbeddedResourceId);
        Assert.Equal(expectedBytes, artifact.Bytes);
        Assert.Equal(expectedSha256, artifact.Sha256);

        using var input = OfficialTranslationModelEmbeddedArtifacts.OpenRead(resourceId);
        using var output = new MemoryStream();
        input.CopyTo(output);
        var bytes = output.ToArray();
        Assert.Equal(expectedBytes, bytes.LongLength);
        Assert.Equal(
            expectedSha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private sealed record RequestRecord(
        HttpMethod Method,
        bool HadContent,
        string AcceptEncoding,
        Uri RequestUri);

    private sealed class ArtifactHandler(
        IReadOnlyDictionary<Uri, byte[]> payloads,
        Uri? finalUri = null) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestUri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI");
            if (!payloads.TryGetValue(requestUri, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }
            Requests.Add(new RequestRecord(
                request.Method,
                request.Content is not null,
                string.Join(",", request.Headers.AcceptEncoding.Select(value => value.Value)),
                requestUri));
            var responseRequest = finalUri is null
                ? request
                : new HttpRequestMessage(HttpMethod.Get, finalUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = responseRequest,
                Content = new ByteArrayContent(bytes),
            });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellable wait unexpectedly completed");
        }
    }

    private sealed record OfficialFixture(
        OfficialTranslationModelDefinition Definition,
        IReadOnlyDictionary<Uri, byte[]> Payloads,
        IReadOnlyDictionary<string, byte[]> Files)
    {
        public static OfficialFixture Create()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["encoder_model.onnx"] = Encoding.UTF8.GetBytes("test encoder"),
                ["decoder_model.onnx"] = Encoding.UTF8.GetBytes("test decoder"),
                ["source.spm"] = Encoding.UTF8.GetBytes("test source sentencepiece"),
                ["target.spm"] = Encoding.UTF8.GetBytes("test target sentencepiece"),
                ["vocab.json"] = Encoding.UTF8.GetBytes("{\"</s>\":0,\"<unk>\":1,\"<pad>\":65000}"),
                ["LICENSE.txt"] = ReadEmbeddedArtifact(
                    OfficialTranslationModelEmbeddedArtifacts.CreativeCommonsBy40License),
            };
            var artifacts = files.Select(file =>
            {
                if (file.Key == "LICENSE.txt")
                {
                    return new OfficialTranslationModelArtifact(
                        null,
                        file.Key,
                        file.Value.Length,
                        Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant(),
                        OfficialTranslationModelEmbeddedArtifacts.CreativeCommonsBy40License);
                }
                var uri = new Uri($"https://models.example.test/{Uri.EscapeDataString(file.Key)}");
                return new OfficialTranslationModelArtifact(
                    uri,
                    file.Key,
                    file.Value.Length,
                    Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant());
            }).ToArray();
            var payloads = artifacts
                .Where(artifact => artifact.DownloadUri is not null)
                .ToDictionary(
                artifact => artifact.DownloadUri!,
                artifact => files[artifact.LocalPath]);
            return new OfficialFixture(
                new OfficialTranslationModelDefinition
                {
                    PackId = "official-download-test-pack",
                    Direction = TranslationDirection.ChineseToEnglish,
                    Version = "test-revision",
                    Publisher = "Helsinki-NLP test fixture",
                    ModelName = "opus-mt-test",
                    SourceUrl = "https://models.example.test/source",
                    Revision = "0123456789abcdef0123456789abcdef01234567",
                    LicenseSpdxId = "CC-BY-4.0",
                    LicenseName = "Creative Commons Attribution 4.0 International",
                    LicenseUrl = "https://models.example.test/license",
                    Attribution = "Test fixture only.",
                    Artifacts = artifacts,
                },
                payloads,
                files);
        }

        private static byte[] ReadEmbeddedArtifact(string resourceId)
        {
            using var input = OfficialTranslationModelEmbeddedArtifacts.OpenRead(resourceId);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
    }
}
