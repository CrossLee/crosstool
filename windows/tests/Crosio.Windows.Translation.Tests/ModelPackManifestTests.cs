using System.Text.Json;
using Xunit;

namespace Crosio.Windows.Translation.Tests;

public sealed class ModelPackManifestTests
{
    [Fact]
    public void AcceptsCompleteLicensedManifest()
    {
        var package = TestModelPack.Create();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            package.Manifest,
            ModelPackManifestCodec.Options);

        var parsed = ModelPackManifestCodec.DeserializeManifest(bytes);

        Assert.Equal(package.Manifest.PackId, parsed.PackId);
        Assert.Equal("CC-BY-4.0", parsed.License.SpdxId);
        Assert.Contains("Helsinki-NLP", parsed.License.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsPathTraversal()
    {
        var package = TestModelPack.Create();
        var invalid = package.Manifest with
        {
            Graph = package.Manifest.Graph with { EncoderModel = "../encoder.onnx" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invalid, ModelPackManifestCodec.Options);

        var error = Assert.Throws<TranslationModelInvalidException>(
            () => ModelPackManifestCodec.DeserializeManifest(bytes));

        Assert.Contains("不安全", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMissingLicenseArtifact()
    {
        var package = TestModelPack.Create();
        var invalid = package.Manifest with
        {
            Artifacts = package.Manifest.Artifacts
                .Where(artifact => artifact.Path != "LICENSE.txt")
                .ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invalid, ModelPackManifestCodec.Options);

        var error = Assert.Throws<TranslationModelInvalidException>(
            () => ModelPackManifestCodec.DeserializeManifest(bytes));

        Assert.Contains("LICENSE.txt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonHttpsOriginOrLicenseUrl()
    {
        var package = TestModelPack.Create();
        var invalid = package.Manifest with
        {
            Origin = package.Manifest.Origin with { SourceUrl = "http://example.test/model" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invalid, ModelPackManifestCodec.Options);

        var error = Assert.Throws<TranslationModelInvalidException>(
            () => ModelPackManifestCodec.DeserializeManifest(bytes));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownManifestFields()
    {
        var package = TestModelPack.Create();
        var json = JsonSerializer.Serialize(package.Manifest, ModelPackManifestCodec.Options);
        var invalid = json[..^1] + ",\"notTrusted\":true}";

        Assert.Throws<TranslationModelInvalidException>(
            () => ModelPackManifestCodec.DeserializeManifest(System.Text.Encoding.UTF8.GetBytes(invalid)));
    }
}
