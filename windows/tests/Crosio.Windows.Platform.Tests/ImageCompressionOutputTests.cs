using Crosio.Windows.Platform.Images;

namespace Crosio.Windows.Platform.Tests;

public sealed class ImageCompressionOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"onepaw-output-tests-{Guid.NewGuid():N}");

    [WindowsFact]
    public async Task ExistingNewAndLegacyCopiesAndSourceAreNeverOverwritten()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "照片.JPEG");
        var legacy = Path.Combine(_root, "照片-crosio.JPEG");
        var occupied = Path.Combine(_root, "照片-一爪.JPEG");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(legacy, [4, 5]);
        await File.WriteAllBytesAsync(occupied, [6, 7]);

        var output = await ImageCompressionService.WriteWithoutOverwritingAsync(source, [8, 9], CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "照片-一爪-2.JPEG"), output);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(source));
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(legacy));
        Assert.Equal([6, 7], await File.ReadAllBytesAsync(occupied));
        Assert.Equal([8, 9], await File.ReadAllBytesAsync(output));
        Assert.Empty(Directory.GetFiles(_root, ".crosio-compression-*.tmp"));
    }

    [WindowsFact]
    public async Task ConcurrentOutputsUseUniqueCurrentBrandNamesAndPreserveExtension()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "截图.PNG");
        await File.WriteAllBytesAsync(source, [255]);

        var outputs = await Task.WhenAll(Enumerable.Range(1, 16).Select(async value =>
        {
            byte[] content = [(byte)value];
            var output = await ImageCompressionService.WriteWithoutOverwritingAsync(source, content, CancellationToken.None);
            Assert.Equal(content, await File.ReadAllBytesAsync(output));
            return output;
        }));

        Assert.Equal(16, outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(outputs, output =>
        {
            Assert.StartsWith("截图-一爪", Path.GetFileName(output));
            Assert.Equal(".PNG", Path.GetExtension(output));
        });
        Assert.Equal([255], await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(_root, ".crosio-compression-*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows path and atomic-file semantics.";
        }
    }
}
