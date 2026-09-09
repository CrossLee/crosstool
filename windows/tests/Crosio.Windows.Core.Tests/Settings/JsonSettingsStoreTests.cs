using Crosio.Windows.Core.Settings;

namespace Crosio.Windows.Core.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"Crosio.Windows.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task MissingFileReturnsFreshDefaults()
    {
        var store = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.NotSame(first, second);
        Assert.Equal(5421, first.PreferredSharingPort);
    }

    [Fact]
    public async Task SavesAndLoadsSettingsWithoutBom()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);
        var expected = new AppSettings
        {
            StartWithWindows = true,
            PreferredSharingPort = 6123,
        };

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();
        var bytes = await File.ReadAllBytesAsync(path);

        Assert.True(actual.StartWithWindows);
        Assert.Equal(6123, actual.PreferredSharingPort);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public async Task CorruptFileIsReportedInsteadOfSilentlyOverwritten()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ definitely not json }");
        var store = new JsonSettingsStore(path);

        await Assert.ThrowsAsync<SettingsFileException>(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
