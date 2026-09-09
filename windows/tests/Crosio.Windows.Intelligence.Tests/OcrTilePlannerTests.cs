using Crosio.Windows.Intelligence.Ocr;
using Xunit;

namespace Crosio.Windows.Intelligence.Tests;

public sealed class OcrTilePlannerTests
{
    [Fact]
    public void TallImageUsesOverlappingTilesAndCoversFinalPixel()
    {
        var tiles = OcrTilePlanner.CreateTiles(1200, 20_000, 4_000, 100);

        Assert.All(tiles, tile =>
        {
            Assert.InRange(tile.Width, 1, 4_000);
            Assert.InRange(tile.Height, 1, 4_000);
        });
        Assert.Equal(0, tiles[0].Y);
        Assert.Equal(20_000, tiles[^1].Bottom);
        Assert.True(tiles.Zip(tiles.Skip(1)).All(pair => pair.First.Bottom > pair.Second.Y));
    }

    [Fact]
    public void WideAndTallImageCreatesTwoDimensionalGrid()
    {
        var tiles = OcrTilePlanner.CreateTiles(8_000, 8_000, 4_000, 100);

        Assert.Equal(9, tiles.Count);
        Assert.Contains(tiles, tile => tile.Right == 8_000 && tile.Bottom == 8_000);
    }

    [Fact]
    public void MergeDropsSameLineRecognizedInTileOverlapAndKeepsHigherConfidence()
    {
        var lines = OcrLineMerger.Merge([
            new RecognizedTextLine("课堂共享", new OcrRectangle(10, 3900, 180, 40), 0.71),
            new RecognizedTextLine("课堂 共享", new OcrRectangle(12, 3902, 178, 39), 0.92),
            new RecognizedTextLine("下一行", new OcrRectangle(10, 3960, 120, 40), 0.80),
        ]);

        Assert.Equal(2, lines.Count);
        Assert.Equal(0.92, lines[0].Confidence);
        Assert.Equal("下一行", lines[1].Text);
    }
}
