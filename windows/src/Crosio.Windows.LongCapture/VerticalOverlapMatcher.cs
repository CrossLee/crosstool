namespace Crosio.Windows.LongCapture;

public enum VerticalScrollDirection
{
    Down,
    Up,
}

public enum OverlapMatchStatus
{
    Matched,
    NoMovement,
    NoMatch,
    Ambiguous,
    IncompatibleFrames,
}

public sealed record VerticalOverlapMatcherOptions(
    int MinimumOverlapPixels = 24,
    int HorizontalSamples = 48,
    int VerticalSamples = 64,
    double MaximumMeanChannelError = 18,
    double AmbiguityScoreMargin = 1.5,
    int AmbiguitySeparationPixels = 3)
{
    internal void Validate()
    {
        if (MinimumOverlapPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumOverlapPixels));
        }

        if (HorizontalSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HorizontalSamples));
        }

        if (VerticalSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VerticalSamples));
        }

        if (!double.IsFinite(MaximumMeanChannelError) || MaximumMeanChannelError < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMeanChannelError));
        }

        if (!double.IsFinite(AmbiguityScoreMargin) || AmbiguityScoreMargin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AmbiguityScoreMargin));
        }

        if (AmbiguitySeparationPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AmbiguitySeparationPixels));
        }
    }
}

public sealed record VerticalOverlapMatch(
    OverlapMatchStatus Status,
    VerticalScrollDirection Direction,
    int OverlapPixels,
    int DisplacementPixels,
    double MeanChannelError,
    int CompetingOverlapPixels = 0)
{
    public bool CanStitch => Status is OverlapMatchStatus.Matched or OverlapMatchStatus.NoMovement;
}

/// <summary>
/// Finds a vertical translation between two equal-size captures. More than one
/// similarly good, materially separated overlap is rejected so repeated list
/// rows cannot silently produce a corrupt long screenshot.
/// </summary>
public sealed class VerticalOverlapMatcher
{
    private readonly VerticalOverlapMatcherOptions _options;

    public VerticalOverlapMatcher(VerticalOverlapMatcherOptions? options = null)
    {
        _options = options ?? new VerticalOverlapMatcherOptions();
        _options.Validate();
    }

    public VerticalOverlapMatch Match(
        LongCaptureFrame previous,
        LongCaptureFrame next,
        VerticalScrollDirection direction)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.Width != next.Width || previous.Height != next.Height)
        {
            return new VerticalOverlapMatch(
                OverlapMatchStatus.IncompatibleFrames,
                direction,
                0,
                0,
                double.PositiveInfinity);
        }

        var minimumOverlap = Math.Min(_options.MinimumOverlapPixels, previous.Height);
        var candidates = new List<Candidate>(previous.Height - minimumOverlap + 1);
        for (var overlap = minimumOverlap; overlap <= previous.Height; overlap++)
        {
            var score = Score(previous, next, direction, overlap);
            if (score <= _options.MaximumMeanChannelError)
            {
                candidates.Add(new Candidate(overlap, score));
            }
        }

        if (candidates.Count == 0)
        {
            return new VerticalOverlapMatch(
                OverlapMatchStatus.NoMatch,
                direction,
                0,
                0,
                double.PositiveInfinity);
        }

        var fullFrame = candidates.FirstOrDefault(candidate => candidate.Overlap == previous.Height);
        if (fullFrame != default && fullFrame.Score <= Math.Min(0.35, _options.MaximumMeanChannelError))
        {
            return new VerticalOverlapMatch(
                OverlapMatchStatus.NoMovement,
                direction,
                previous.Height,
                0,
                fullFrame.Score);
        }

        candidates.Sort(static (left, right) =>
        {
            var scoreOrder = left.Score.CompareTo(right.Score);
            return scoreOrder != 0
                ? scoreOrder
                : right.Overlap.CompareTo(left.Overlap);
        });
        var best = candidates[0];
        var competitor = candidates
            .Skip(1)
            .FirstOrDefault(candidate =>
                Math.Abs(candidate.Overlap - best.Overlap) >= _options.AmbiguitySeparationPixels
                && candidate.Score <= best.Score + _options.AmbiguityScoreMargin);

        if (competitor != default)
        {
            return new VerticalOverlapMatch(
                OverlapMatchStatus.Ambiguous,
                direction,
                best.Overlap,
                previous.Height - best.Overlap,
                best.Score,
                competitor.Overlap);
        }

        var displacement = previous.Height - best.Overlap;
        return new VerticalOverlapMatch(
            displacement == 0
                ? OverlapMatchStatus.NoMovement
                : OverlapMatchStatus.Matched,
            direction,
            best.Overlap,
            displacement,
            best.Score);
    }

    private double Score(
        LongCaptureFrame previous,
        LongCaptureFrame next,
        VerticalScrollDirection direction,
        int overlap)
    {
        var previousStartY = direction == VerticalScrollDirection.Down
            ? previous.Height - overlap
            : 0;
        var nextStartY = direction == VerticalScrollDirection.Down
            ? 0
            : next.Height - overlap;
        var xSamples = Math.Min(previous.Width, _options.HorizontalSamples);
        var ySamples = Math.Min(overlap, _options.VerticalSamples);
        Span<double> rowErrors = stackalloc double[ySamples];
        var previousPixels = previous.PixelSpan;
        var nextPixels = next.PixelSpan;

        for (var sampleY = 0; sampleY < ySamples; sampleY++)
        {
            var relativeY = EvenlySpacedIndex(sampleY, ySamples, overlap);
            var previousRow = checked((previousStartY + relativeY) * previous.Width * LongCaptureFrame.BytesPerPixel);
            var nextRow = checked((nextStartY + relativeY) * next.Width * LongCaptureFrame.BytesPerPixel);
            long rowError = 0;
            long rowChannelCount = 0;
            for (var sampleX = 0; sampleX < xSamples; sampleX++)
            {
                var x = EvenlySpacedIndex(sampleX, xSamples, previous.Width);
                var previousOffset = checked(previousRow + (x * LongCaptureFrame.BytesPerPixel));
                var nextOffset = checked(nextRow + (x * LongCaptureFrame.BytesPerPixel));
                for (var channel = 0; channel < 3; channel++)
                {
                    rowError += Math.Abs(previousPixels[previousOffset + channel] - nextPixels[nextOffset + channel]);
                    rowChannelCount++;
                }
            }

            rowErrors[sampleY] = rowError / (double)rowChannelCount;
        }

        // Scrollbars, carets, sticky controls and animations can disturb a few
        // horizontal bands. Ignore the noisiest sixth while still requiring a
        // large majority of the overlap to agree.
        rowErrors.Sort();
        var retainedCount = Math.Max(1, rowErrors.Length * 5 / 6);
        var retainedTotal = 0d;
        for (var index = 0; index < retainedCount; index++)
        {
            retainedTotal += rowErrors[index];
        }

        return retainedTotal / retainedCount;
    }

    private static int EvenlySpacedIndex(int index, int count, int upperBound) =>
        count <= 1 || upperBound <= 1
            ? 0
            : index * (upperBound - 1) / (count - 1);

    private readonly record struct Candidate(int Overlap, double Score);
}
