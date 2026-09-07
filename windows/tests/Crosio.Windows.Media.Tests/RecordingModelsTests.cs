using Crosio.Windows.Media;

namespace Crosio.Windows.Media.Tests;

public sealed class RecordingModelsTests
{
    [Fact]
    public void RegionTarget_AcceptsNegativeCoordinateDisplay()
    {
        var source = new PixelRectangle(-1920, -200, 1920, 1080);
        var region = new PixelRectangle(-1800, -100, 800, 600);

        var target = new RegionRecordingTarget("DISPLAY2", (nint)42, source, region);

        Assert.Equal(region, target.Region);
        Assert.Equal(new PixelSize(800, 600), target.SourceSize);
    }

    [Fact]
    public void RegionTarget_RejectsRegionOutsideSelectedDisplay()
    {
        var source = new PixelRectangle(0, 0, 1920, 1080);
        var region = new PixelRectangle(1800, 900, 500, 300);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RegionRecordingTarget("DISPLAY1", (nint)42, source, region));
    }

    [Fact]
    public void CapabilityReport_RequiresAudioCapabilitiesOnlyWhenRequested()
    {
        var report = new RecordingCapabilityReport(
        [
            Supported(RecordingCapability.WindowsGraphicsCapture),
            Supported(RecordingCapability.DisplayCapture),
            Supported(RecordingCapability.H264Encoding),
            Supported(RecordingCapability.CursorToggle),
            Supported(RecordingCapability.ProductionPipeline),
            Unsupported(RecordingCapability.AacEncoding),
            Unsupported(RecordingCapability.SystemAudioLoopback),
        ]);
        var target = new DisplayRecordingTarget("DISPLAY1", (nint)1, new PixelSize(1920, 1080));

        var silentRequest = new RecordingRequest(target, false, true, Path.GetTempPath());
        var audioRequest = new RecordingRequest(target, true, true, Path.GetTempPath());

        Assert.Empty(report.FindBlockingIssues(silentRequest));
        Assert.Collection(
            report.FindBlockingIssues(audioRequest),
            issue => Assert.Equal(RecordingCapability.AacEncoding, issue.Capability),
            issue => Assert.Equal(RecordingCapability.SystemAudioLoopback, issue.Capability));
    }

    [Fact]
    public void CapabilityReport_TreatsUnknownUntilStartAsBlocking()
    {
        var report = new RecordingCapabilityReport(
        [
            Supported(RecordingCapability.WindowsGraphicsCapture),
            Supported(RecordingCapability.WindowCapture),
            Unknown(RecordingCapability.H264Encoding),
            Supported(RecordingCapability.ProductionPipeline),
        ]);
        var target = new WindowRecordingTarget((nint)7, new PixelSize(1280, 720));
        var request = new RecordingRequest(target, false, true, Path.GetTempPath());

        var blocker = Assert.Single(report.FindBlockingIssues(request));

        Assert.Equal(RecordingCapability.H264Encoding, blocker.Capability);
        Assert.Equal(CapabilityAvailability.UnknownUntilStart, blocker.Availability);
    }

    private static CapabilityStatus Supported(RecordingCapability capability) =>
        new(capability, CapabilityAvailability.Supported, "supported", "supported");

    private static CapabilityStatus Unsupported(RecordingCapability capability) =>
        new(capability, CapabilityAvailability.Unsupported, "unsupported", "unsupported");

    private static CapabilityStatus Unknown(RecordingCapability capability) =>
        new(capability, CapabilityAvailability.UnknownUntilStart, "unknown", "unknown");
}
