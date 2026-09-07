namespace Crosio.Windows.Intelligence.Ocr;

public static class OcrTilePlanner
{
    public const int DefaultOverlap = 96;

    public static IReadOnlyList<OcrRectangle> CreateTiles(
        int imageWidth,
        int imageHeight,
        int maximumDimension,
        int overlap = DefaultOverlap)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }
        if (maximumDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        }
        if (overlap < 0 || overlap >= maximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap));
        }

        var xOrigins = Origins(imageWidth, maximumDimension, overlap);
        var yOrigins = Origins(imageHeight, maximumDimension, overlap);
        var tiles = new List<OcrRectangle>(checked(xOrigins.Count * yOrigins.Count));
        foreach (var y in yOrigins)
        {
            foreach (var x in xOrigins)
            {
                tiles.Add(new OcrRectangle(
                    x,
                    y,
                    Math.Min(maximumDimension, imageWidth - x),
                    Math.Min(maximumDimension, imageHeight - y)));
            }
        }
        return tiles;
    }

    private static IReadOnlyList<int> Origins(int length, int tileSize, int overlap)
    {
        if (length <= tileSize)
        {
            return [0];
        }

        var stride = tileSize - overlap;
        var result = new List<int>();
        for (var origin = 0; origin < length; origin += stride)
        {
            var clamped = Math.Min(origin, length - tileSize);
            if (result.Count == 0 || result[^1] != clamped)
            {
                result.Add(clamped);
            }
            if (clamped + tileSize >= length)
            {
                break;
            }
        }
        return result;
    }
}
