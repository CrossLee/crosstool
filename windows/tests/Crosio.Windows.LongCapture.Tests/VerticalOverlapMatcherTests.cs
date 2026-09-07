namespace Crosio.Windows.LongCapture.Tests;

public sealed class VerticalOverlapMatcherTests
{
    private static VerticalOverlapMatcher CreateExactMatcher() => new(
        new VerticalOverlapMatcherOptions(
            MinimumOverlapPixels: 2,
            HorizontalSamples: 12,
            VerticalSamples: 10,
            MaximumMeanChannelError: 0,
            AmbiguityScoreMargin: 0,
            AmbiguitySeparationPixels: 2));

    [Fact]
    public void FindsDownwardOverlap()
    {
        var result = CreateExactMatcher().Match(
            FrameFactory.FromContentRows(0, 10),
            FrameFactory.FromContentRows(4, 10),
            VerticalScrollDirection.Down);

        Assert.Equal(OverlapMatchStatus.Matched, result.Status);
        Assert.Equal(6, result.OverlapPixels);
        Assert.Equal(4, result.DisplacementPixels);
        Assert.Equal(0, result.MeanChannelError);
    }

    [Fact]
    public void FindsUpwardOverlap()
    {
        var result = CreateExactMatcher().Match(
            FrameFactory.FromContentRows(4, 10),
            FrameFactory.FromContentRows(0, 10),
            VerticalScrollDirection.Up);

        Assert.Equal(OverlapMatchStatus.Matched, result.Status);
        Assert.Equal(6, result.OverlapPixels);
        Assert.Equal(4, result.DisplacementPixels);
    }

    [Fact]
    public void IdenticalFramesAreNoMovement()
    {
        var frame = FrameFactory.FromContentRows(7, 10);

        var result = CreateExactMatcher().Match(frame, frame, VerticalScrollDirection.Down);

        Assert.Equal(OverlapMatchStatus.NoMovement, result.Status);
        Assert.Equal(10, result.OverlapPixels);
        Assert.Equal(0, result.DisplacementPixels);
    }

    [Fact]
    public void IdenticalFlatFramesAreSafelyTreatedAsNoMovement()
    {
        var frame = FrameFactory.Solid(8, 12, 100);

        var result = CreateExactMatcher().Match(frame, frame, VerticalScrollDirection.Down);

        Assert.Equal(OverlapMatchStatus.NoMovement, result.Status);
        Assert.Equal(12, result.OverlapPixels);
    }

    [Fact]
    public void RepeatedTextureIsRejectedAsAmbiguous()
    {
        var previous = FrameFactory.PeriodicRows(0, 12);
        var next = FrameFactory.PeriodicRows(2, 12);

        var result = CreateExactMatcher().Match(
            previous,
            next,
            VerticalScrollDirection.Down);

        Assert.Equal(OverlapMatchStatus.Ambiguous, result.Status);
        Assert.True(result.CompetingOverlapPixels > 0);
    }

    [Fact]
    public void UnrelatedFramesHaveNoMatch()
    {
        var result = CreateExactMatcher().Match(
            FrameFactory.FromContentRows(0, 10),
            FrameFactory.FromContentRows(30, 10),
            VerticalScrollDirection.Down);

        Assert.Equal(OverlapMatchStatus.NoMatch, result.Status);
    }

    [Fact]
    public void DifferentDimensionsAreIncompatible()
    {
        var result = CreateExactMatcher().Match(
            FrameFactory.FromContentRows(0, 10, width: 12),
            FrameFactory.FromContentRows(0, 10, width: 10),
            VerticalScrollDirection.Down);

        Assert.Equal(OverlapMatchStatus.IncompatibleFrames, result.Status);
    }
}
