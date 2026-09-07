using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crosio.Windows.Intelligence.Translation;

namespace Crosio.Windows.Translation.Tests;

internal sealed record TestPackage(
    byte[] Bytes,
    ModelPackInstallDescriptor Descriptor,
    TranslationModelPackManifest Manifest);

internal static class TestModelPack
{
    public static TestPackage Create(
        string packId = "helsinki-opus-test-zh-en",
        TranslationDirection direction = TranslationDirection.ChineseToEnglish,
        bool corruptEncoderAfterManifest = false,
        bool addUnexpectedFile = false)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["encoder_model.onnx"] = Encoding.UTF8.GetBytes("test encoder"),
            ["decoder_model.onnx"] = Encoding.UTF8.GetBytes("test decoder"),
            ["source.spm"] = Encoding.UTF8.GetBytes("test source sentencepiece"),
            ["target.spm"] = Encoding.UTF8.GetBytes("test target sentencepiece"),
            ["source-vocab.json"] = Encoding.UTF8.GetBytes("{\"</s>\":0,\"<unk>\":1,\"<pad>\":2,\"hello\":3}"),
            ["target-vocab.json"] = Encoding.UTF8.GetBytes("{\"</s>\":0,\"<unk>\":1,\"<pad>\":2,\"world\":3}"),
            ["LICENSE.txt"] = Encoding.UTF8.GetBytes("Creative Commons Attribution 4.0 International"),
        };
        var manifest = CreateManifest(packId, direction, files);
        if (corruptEncoderAfterManifest)
        {
            files["encoder_model.onnx"] = Encoding.UTF8.GetBytes("tampered encoder");
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(
                archive,
                ModelPackPathPolicy.ManifestFileName,
                JsonSerializer.SerializeToUtf8Bytes(manifest, ModelPackManifestCodec.Options));
            foreach (var file in files)
            {
                WriteEntry(archive, file.Key, file.Value);
            }
            if (addUnexpectedFile)
            {
                WriteEntry(archive, "surprise.txt", Encoding.UTF8.GetBytes("not declared"));
            }
        }
        var bytes = output.ToArray();
        var descriptor = new ModelPackInstallDescriptor
        {
            PackId = packId,
            Direction = direction,
            ArchiveBytes = bytes.Length,
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        return new TestPackage(bytes, descriptor, manifest);
    }

    public static TranslationModelPackManifest CreateManifest(
        string packId,
        TranslationDirection direction,
        IReadOnlyDictionary<string, byte[]> files)
    {
        return new TranslationModelPackManifest
        {
            SchemaVersion = TranslationModelPackManifest.CurrentSchemaVersion,
            PackId = packId,
            Version = "2026.09-test",
            Direction = direction,
            ModelFamily = TranslationModelPackManifest.SupportedModelFamily,
            Origin = new ModelOriginManifest
            {
                Publisher = "Helsinki-NLP",
                ModelName = direction == TranslationDirection.ChineseToEnglish
                    ? "opus-mt-zh-en"
                    : "opus-mt-en-zh",
                SourceUrl = "https://github.com/Helsinki-NLP/Opus-MT",
                Revision = "test-only",
            },
            License = new ModelLicenseManifest
            {
                SpdxId = "CC-BY-4.0",
                Name = "Creative Commons Attribution 4.0 International",
                Url = "https://creativecommons.org/licenses/by/4.0/",
                Attribution = "Translations powered by Helsinki-NLP OPUS-MT models (CC-BY 4.0).",
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
                SourceVocabulary = "source-vocab.json",
                TargetVocabulary = "target-vocab.json",
                DecodeWithSourceSentencePiece = false,
            },
            Generation = new MarianGenerationManifest
            {
                UnknownTokenId = 1,
                EndOfSentenceTokenId = 0,
                PaddingTokenId = 2,
                DecoderStartTokenId = 2,
                MaxInputTokens = 512,
                MaxOutputTokens = 512,
                MinimumOutputTokens = 1,
            },
            Artifacts = files.Select(file => new ModelArtifactManifest
            {
                Path = file.Key,
                Bytes = file.Value.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant(),
            }).ToArray(),
        };
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}

internal sealed class TemporaryModelRoot : IDisposable
{
    public TemporaryModelRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"crosio-translation-tests-{Guid.NewGuid():N}");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class SynchronousProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}
