namespace Crosio.Windows.Intelligence.Ocr;

public static class OcrLineMerger
{
    public static IReadOnlyList<RecognizedTextLine> Merge(IEnumerable<RecognizedTextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var ordered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToList();
        var result = new List<RecognizedTextLine>(ordered.Count);

        foreach (var candidate in ordered)
        {
            var duplicateIndex = result.FindIndex(existing => IsDuplicate(existing, candidate));
            if (duplicateIndex < 0)
            {
                result.Add(candidate with { Text = candidate.Text.Trim() });
                continue;
            }

            if (candidate.Confidence > result[duplicateIndex].Confidence)
            {
                result[duplicateIndex] = candidate with { Text = candidate.Text.Trim() };
            }
        }

        return result
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();
    }

    private static bool IsDuplicate(RecognizedTextLine first, RecognizedTextLine second)
    {
        if (!Normalize(first.Text).Equals(Normalize(second.Text), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var intersectionWidth = Math.Max(0, Math.Min(first.Bounds.Right, second.Bounds.Right) -
            Math.Max(first.Bounds.X, second.Bounds.X));
        var intersectionHeight = Math.Max(0, Math.Min(first.Bounds.Bottom, second.Bounds.Bottom) -
            Math.Max(first.Bounds.Y, second.Bounds.Y));
        var intersection = (long)intersectionWidth * intersectionHeight;
        var smallerArea = Math.Min(
            (long)first.Bounds.Width * first.Bounds.Height,
            (long)second.Bounds.Width * second.Bounds.Height);
        if (smallerArea <= 0)
        {
            return false;
        }

        return intersection >= smallerArea * 0.55;
    }

    private static string Normalize(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
