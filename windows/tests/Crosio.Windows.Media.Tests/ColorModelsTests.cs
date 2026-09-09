using Crosio.Windows.Media;

namespace Crosio.Windows.Media.Tests;

public sealed class ColorModelsTests
{
    [Theory]
    [InlineData(255, 0, 0, "#FF0000", "rgb(255, 0, 0)", "hsl(0, 100%, 50%)")]
    [InlineData(0, 255, 0, "#00FF00", "rgb(0, 255, 0)", "hsl(120, 100%, 50%)")]
    [InlineData(0, 0, 255, "#0000FF", "rgb(0, 0, 255)", "hsl(240, 100%, 50%)")]
    [InlineData(128, 128, 128, "#808080", "rgb(128, 128, 128)", "hsl(0, 0%, 50%)")]
    public void FormatsOpaqueColor(
        byte red,
        byte green,
        byte blue,
        string expectedHex,
        string expectedRgb,
        string expectedHsl)
    {
        var color = new SampledColor(red, green, blue);

        Assert.Equal(expectedHex, color.Hex);
        Assert.Equal(expectedRgb, color.Rgb);
        Assert.Equal(expectedHsl, color.HslText);
    }

    [Fact]
    public void FormatsAlphaWithoutLocaleDependentDecimalSeparator()
    {
        var color = new SampledColor(1, 2, 3, 128);

        Assert.Equal("#01020380", color.Hex);
        Assert.Equal("rgba(1, 2, 3, 0.502)", color.Rgb);
    }

    [Theory]
    [InlineData(ColorTextFormat.Hex, "#0C2238")]
    [InlineData(ColorTextFormat.Rgb, "rgb(12, 34, 56)")]
    [InlineData(ColorTextFormat.Hsl, "hsl(210, 65%, 13%)")]
    public async Task ClipboardService_WritesRequestedRepresentation(ColorTextFormat format, string expected)
    {
        var clipboard = new FakeClipboardWriter();
        var service = new ColorClipboardService(clipboard);

        await service.CopyAsync(new SampledColor(12, 34, 56), format);

        Assert.Equal(expected, clipboard.Text);
    }

    [Fact]
    public void RecentColors_DeduplicatesAndKeepsTwelveConfirmedColors()
    {
        var store = new RecentColorStore();
        for (var index = 0; index < 14; index++)
        {
            store.Confirm(new SampledColor((byte)index, 0, 0));
        }

        store.Confirm(new SampledColor(7, 0, 0));

        Assert.Equal(12, store.Colors.Count);
        Assert.Equal(new SampledColor(7, 0, 0), store.Colors[0]);
        Assert.Equal(1, store.Colors.Count(color => color == new SampledColor(7, 0, 0)));
        Assert.DoesNotContain(new SampledColor(0, 0, 0), store.Colors);
    }

    private sealed class FakeClipboardWriter : ITextClipboardWriter
    {
        public string? Text { get; private set; }

        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text = text;
            return Task.CompletedTask;
        }
    }
}
