using Crosio.Windows.Intelligence.Translation;
using Xunit;

namespace Crosio.Windows.Intelligence.Tests;

public sealed class TranslationHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crosio-translation-{Guid.NewGuid():N}");

    [Fact]
    public async Task HistoryIsAtomicNewestFirstAndLimitedToOneHundredEntries()
    {
        var path = Path.Combine(_root, "history.json");
        var store = new TranslationHistoryStore(path);
        for (var index = 0; index < 105; index++)
        {
            await store.AddAsync(new TranslationResult(
                $"source-{index}",
                $"output-{index}",
                TranslationDirection.EnglishToChinese,
                DateTimeOffset.UnixEpoch.AddSeconds(index)));
        }

        var loaded = await new TranslationHistoryStore(path).LoadAsync();
        Assert.Equal(100, loaded.Count);
        Assert.Equal("source-104", loaded[0].SourceText);
        Assert.Equal("source-5", loaded[^1].SourceText);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
