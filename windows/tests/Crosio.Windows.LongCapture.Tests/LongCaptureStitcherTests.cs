namespace Crosio.Windows.LongCapture.Tests;

public sealed class LongCaptureStitcherTests
{
    [Fact]
    public void AppendsFramesWhilePreservingEveryContentRow()
    {
        var stitcher = CreateStitcher();
        Assert.True(stitcher.AddInitialFrame(FrameFactory.FromContentRows(0, 10)).Accepted);

        var result = stitcher.AddFrame(
            FrameFactory.FromContentRows(4, 10),
            VerticalScrollDirection.Down);

        Assert.Equal(LongCaptureAddFrameStatus.Added, result.Status);
        Assert.Equal(4, result.AddedPixels);
        Assert.Equal(14, result.OutputHeight);
        FrameFactory.AssertContentRows(stitcher.BuildFrame(), 0);
    }

    [Fact]
    public void PrependsFramesWhilePreservingEveryContentRow()
    {
        var stitcher = CreateStitcher();
        _ = stitcher.AddInitialFrame(FrameFactory.FromContentRows(4, 10));

        var result = stitcher.AddFrame(
            FrameFactory.FromContentRows(0, 10),
            VerticalScrollDirection.Up);

        Assert.Equal(LongCaptureAddFrameStatus.Added, result.Status);
        Assert.Equal(14, result.OutputHeight);
        FrameFactory.AssertContentRows(stitcher.BuildFrame(), 0);
    }

    [Fact]
    public void DirectionReversalDoesNotDuplicateExistingRows()
    {
        var stitcher = CreateStitcher();
        _ = stitcher.AddInitialFrame(FrameFactory.FromContentRows(4, 10));
        _ = stitcher.AddFrame(
            FrameFactory.FromContentRows(8, 10),
            VerticalScrollDirection.Down);

        var result = stitcher.AddFrame(
            FrameFactory.FromContentRows(0, 10),
            VerticalScrollDirection.Up);

        Assert.Equal(LongCaptureAddFrameStatus.Added, result.Status);
        Assert.Equal(18, result.OutputHeight);
        FrameFactory.AssertContentRows(stitcher.BuildFrame(), 0);
    }

    [Fact]
    public void AmbiguousFrameDoesNotMutateCurrentOutput()
    {
        var matcher = new VerticalOverlapMatcher(
            new VerticalOverlapMatcherOptions(
                MinimumOverlapPixels: 2,
                MaximumMeanChannelError: 0,
                AmbiguityScoreMargin: 0,
                AmbiguitySeparationPixels: 2));
        var stitcher = new LongCaptureStitcher(matcher);
        var previous = FrameFactory.PeriodicRows(0, 12);
        var next = FrameFactory.PeriodicRows(2, 12);
        _ = stitcher.AddInitialFrame(previous);

        var result = stitcher.AddFrame(next, VerticalScrollDirection.Down);

        Assert.Equal(LongCaptureAddFrameStatus.AmbiguousOverlap, result.Status);
        Assert.Equal(12, stitcher.OutputHeight);
        Assert.Equal(previous.CopyPixels(), stitcher.BuildFrame().CopyPixels());
    }

    [Fact]
    public void RejectsExpansionBeyondConfiguredPixelLimit()
    {
        var stitcher = CreateStitcher(maximumPixelCount: 120);
        _ = stitcher.AddInitialFrame(FrameFactory.FromContentRows(0, 10));

        var result = stitcher.AddFrame(
            FrameFactory.FromContentRows(4, 10),
            VerticalScrollDirection.Down);

        Assert.Equal(LongCaptureAddFrameStatus.LimitExceeded, result.Status);
        Assert.Equal(10, stitcher.OutputHeight);
    }

    [Fact]
    public void RejectsInitialFrameBeyondConfiguredEdgeLimitWithoutMutation()
    {
        var matcher = new VerticalOverlapMatcher();
        var stitcher = new LongCaptureStitcher(
            matcher,
            maximumPixelCount: 1_000_000,
            maximumEdgePixels: 8);

        var result = stitcher.AddInitialFrame(FrameFactory.FromContentRows(0, 10));

        Assert.Equal(LongCaptureAddFrameStatus.LimitExceeded, result.Status);
        Assert.False(stitcher.HasFrame);
        Assert.Equal(0, stitcher.OutputHeight);
    }

    private static LongCaptureStitcher CreateStitcher(long maximumPixelCount = 1_000_000)
    {
        var matcher = new VerticalOverlapMatcher(
            new VerticalOverlapMatcherOptions(
                MinimumOverlapPixels: 2,
                HorizontalSamples: 12,
                VerticalSamples: 10,
                MaximumMeanChannelError: 0,
                AmbiguityScoreMargin: 0,
                AmbiguitySeparationPixels: 2));
        return new LongCaptureStitcher(
            matcher,
            maximumPixelCount,
            maximumEdgePixels: 20_000);
    }
}
