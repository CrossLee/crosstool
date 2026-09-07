using Crosio.Windows.Media;

namespace Crosio.Windows.Media.Tests;

public sealed class RecordingProfilePlannerTests
{
    [Fact]
    public void Create_UsesExactThirtyFpsH264AndOptionalAac()
    {
        var profile = RecordingProfilePlanner.Create(new PixelSize(1920, 1080), includeSystemAudio: true);

        Assert.Equal(30U, profile.FrameRateNumerator);
        Assert.Equal(1U, profile.FrameRateDenominator);
        Assert.Equal("H264", profile.VideoCodec);
        Assert.Equal("AAC", profile.AudioCodec);
        Assert.Equal(48_000U, profile.AudioSampleRate);
        Assert.Equal(2U, profile.AudioChannels);
        Assert.Equal("MP4", profile.Container);
        Assert.Equal(".mp4", profile.FileExtension);
    }

    [Fact]
    public void Create_OmitsAudioTrackWhenSystemAudioDisabled()
    {
        var profile = RecordingProfilePlanner.Create(new PixelSize(1920, 1080), includeSystemAudio: false);

        Assert.Null(profile.AudioCodec);
        Assert.Equal(0U, profile.AudioSampleRate);
        Assert.Equal(0U, profile.AudioChannels);
        Assert.Equal(0U, profile.AudioBitrate);
    }

    [Theory]
    [InlineData(7680, 4320)]
    [InlineData(5120, 1440)]
    [InlineData(3001, 2001)]
    public void ScaleToLimits_ProducesEvenFrameWithinSafetyLimits(int width, int height)
    {
        var output = RecordingProfilePlanner.ScaleToLimits(
            new PixelSize(width, height),
            maximumLongestEdge: 3840,
            maximumPixelCount: 8_300_000);

        Assert.InRange(Math.Max(output.Width, output.Height), 2, 3840);
        Assert.InRange(output.PixelCount, 4, 8_300_000);
        Assert.Equal(0, output.Width % 2);
        Assert.Equal(0, output.Height % 2);
        Assert.Equal(width / (double)height, output.Width / (double)output.Height, precision: 2);
    }

    [Fact]
    public void DefaultLimits_MatchProductSafetyBoundary()
    {
        var limits = RecordingSafetyLimits.Default;

        Assert.Equal(TimeSpan.FromHours(2), limits.MaximumDuration);
        Assert.Equal(10L * 1024 * 1024 * 1024, limits.MaximumFileBytes);
        Assert.Equal(2L * 1024 * 1024 * 1024, limits.RequiredFreeBytesBeforeStart);
        Assert.Equal(1L * 1024 * 1024 * 1024, limits.ReservedFreeBytesWhileRecording);
    }
}
