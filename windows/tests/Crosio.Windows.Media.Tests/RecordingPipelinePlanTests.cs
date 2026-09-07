using System.Buffers.Binary;
using Crosio.Windows.Media;

namespace Crosio.Windows.Media.Tests;

public sealed class RecordingPipelinePlanTests
{
    [Fact]
    public void RegionCrop_IsConvertedFromVirtualDesktopToDisplayLocalPixels()
    {
        var target = new RegionRecordingTarget(
            @"\\.\DISPLAY2",
            (nint)42,
            new PixelRectangle(-1920, -200, 1920, 1080),
            new PixelRectangle(-1800, -100, 800, 600));
        var request = Prepare(target, includeAudio: false, includeCursor: false);

        var plan = RecordingPipelinePlanner.Create(request);

        Assert.Equal(RecordingTargetKind.Region, plan.TargetKind);
        Assert.Equal(@"\\.\DISPLAY2", plan.DisplayDeviceName);
        Assert.Equal(new PixelRectangle(120, 100, 800, 600), plan.SourceCrop);
        Assert.Equal(request.EncodingProfile.OutputSize, plan.OutputSize);
        Assert.Equal(30U, plan.FrameRate);
        Assert.False(plan.IncludeCursor);
    }

    [Fact]
    public void WindowPlan_PreservesHwndAndAudioChoice()
    {
        var request = Prepare(
            new WindowRecordingTarget((nint)1234, new PixelSize(1280, 720)),
            includeAudio: true,
            includeCursor: true);

        var plan = RecordingPipelinePlanner.Create(request);

        Assert.Equal((nint)1234, plan.WindowHandle);
        Assert.Null(plan.DisplayDeviceName);
        Assert.Null(plan.SourceCrop);
        Assert.True(plan.IncludeSystemAudio);
        Assert.True(plan.IncludeCursor);
    }

    [Theory]
    [InlineData(120, 100, 500, RecordingRuntimeLimit.MaximumDuration)]
    [InlineData(10, 1_000, 500, RecordingRuntimeLimit.MaximumFileSize)]
    [InlineData(10, 100, 49, RecordingRuntimeLimit.DiskReserve)]
    public void RuntimeLimitEvaluator_StopsAtEveryConfiguredBoundary(
        int elapsedSeconds,
        long fileBytes,
        long freeBytes,
        RecordingRuntimeLimit expected)
    {
        var limits = new RecordingSafetyLimits(
            TimeSpan.FromSeconds(120),
            1_000,
            100,
            50,
            1920,
            2_000_000);

        var actual = RecordingRuntimeLimitEvaluator.Evaluate(
            new RecordingRuntimeSnapshot(TimeSpan.FromSeconds(elapsedSeconds), fileBytes, freeBytes),
            limits);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Mp4Inspector_RequiresFileTypeMovieMetadataAndNonEmptyMediaData()
    {
        using var valid = BuildMp4("ftyp", "moov", "mdat");
        using var noMovie = BuildMp4("ftyp", "mdat");
        using var emptyMedia = BuildMp4("ftyp", "moov", "mdat", emptyLastBox: true);

        Assert.True(Mp4ContainerInspector.IsFinalized(valid));
        Assert.False(Mp4ContainerInspector.IsFinalized(noMovie));
        Assert.False(Mp4ContainerInspector.IsFinalized(emptyMedia));
    }

    [Fact]
    public void Mp4Inspector_RejectsBoxThatRunsBeyondStream()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 100);
        "ftyp"u8.CopyTo(bytes.AsSpan(4, 4));
        using var stream = new MemoryStream(bytes);

        Assert.False(Mp4ContainerInspector.IsFinalized(stream));
    }

    [Fact]
    public void NativeFailureClassifier_ExplainsArchitectureMismatchInChinese()
    {
        var message = NativeRecordingFailureClassifier.Describe(
            new TypeInitializationException("Recorder", new BadImageFormatException("wrong architecture")));

        Assert.StartsWith("[recording-architecture-mismatch]", message, StringComparison.Ordinal);
        Assert.Contains("x64", message, StringComparison.Ordinal);
        Assert.Contains("ARM64", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeFailureClassifier_ExplainsVisualCppRuntimeAndMediaFeaturePack()
    {
        var runtime = NativeRecordingFailureClassifier.Describe(
            new DllNotFoundException("VCRUNTIME140_1.dll"));
        var media = NativeRecordingFailureClassifier.Describe(
            new InvalidOperationException("MF_E_PLATFORM_NOT_INITIALIZED (0xC00D36B0)"));

        Assert.StartsWith("[vc-runtime-missing]", runtime, StringComparison.Ordinal);
        Assert.Contains("Visual C++", runtime, StringComparison.Ordinal);
        Assert.StartsWith("[media-foundation-unavailable]", media, StringComparison.Ordinal);
        Assert.Contains("Media Feature Pack", media, StringComparison.Ordinal);
    }

    private static PreparedRecordingRequest Prepare(
        RecordingTarget target,
        bool includeAudio,
        bool includeCursor)
    {
        var request = new RecordingRequest(
            target,
            includeAudio,
            includeCursor,
            Path.GetTempPath());
        return new PreparedRecordingRequest(
            Guid.NewGuid(),
            request,
            RecordingProfilePlanner.Create(target.SourceSize, includeAudio),
            RecordingSafetyLimits.Default,
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.partial.mp4"));
    }

    private static MemoryStream BuildMp4(
        string first,
        string second,
        string? third = null,
        bool emptyLastBox = false)
    {
        var stream = new MemoryStream();
        WriteBox(stream, first, [1, 2, 3, 4]);
        WriteBox(stream, second, [5, 6, 7, 8]);
        if (third is not null)
        {
            WriteBox(stream, third, emptyLastBox ? [] : [9, 10, 11, 12]);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteBox(Stream stream, string type, byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], checked((uint)(8 + payload.Length)));
        System.Text.Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(payload);
    }
}
