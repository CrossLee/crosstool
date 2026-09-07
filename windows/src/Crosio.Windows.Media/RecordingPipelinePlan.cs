namespace Crosio.Windows.Media;

/// <summary>
/// Platform-neutral description of the native recorder configuration. Keeping
/// this mapping outside the C++/CLI boundary makes target selection and fixed
/// region cropping independently testable on every build host.
/// </summary>
public sealed record RecordingPipelinePlan(
    RecordingTargetKind TargetKind,
    string? DisplayDeviceName,
    nint WindowHandle,
    PixelRectangle? SourceCrop,
    PixelSize OutputSize,
    uint FrameRate,
    uint VideoBitrate,
    bool IncludeSystemAudio,
    bool IncludeCursor);

public static class RecordingPipelinePlanner
{
    public static RecordingPipelinePlan Create(PreparedRecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? displayDeviceName = null;
        nint windowHandle = nint.Zero;
        PixelRectangle? sourceCrop = null;

        switch (request.Request.Target)
        {
            case DisplayRecordingTarget display:
                displayDeviceName = display.DeviceName;
                break;

            case WindowRecordingTarget window:
                windowHandle = window.WindowHandle;
                break;

            case RegionRecordingTarget region:
                displayDeviceName = region.DisplayDeviceName;
                sourceCrop = new PixelRectangle(
                    checked(region.Region.Left - region.SourceBounds.Left),
                    checked(region.Region.Top - region.SourceBounds.Top),
                    region.Region.Width,
                    region.Region.Height);
                break;

            default:
                throw new RecordingException(
                    RecordingFailureCode.InvalidRequest,
                    "The requested Windows recording target is unsupported.");
        }

        return new RecordingPipelinePlan(
            request.Request.Target.Kind,
            displayDeviceName,
            windowHandle,
            sourceCrop,
            request.EncodingProfile.OutputSize,
            request.EncodingProfile.FrameRateNumerator / request.EncodingProfile.FrameRateDenominator,
            request.EncodingProfile.VideoBitrate,
            request.Request.IncludeSystemAudio,
            request.Request.IncludeCursor);
    }
}

public enum RecordingRuntimeLimit
{
    MaximumDuration,
    MaximumFileSize,
    DiskReserve,
}

public sealed record RecordingRuntimeSnapshot(
    TimeSpan Elapsed,
    long FileBytes,
    long AvailableBytes);

public static class RecordingRuntimeLimitEvaluator
{
    public static RecordingRuntimeLimit? Evaluate(
        RecordingRuntimeSnapshot snapshot,
        RecordingSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(limits);

        if (snapshot.Elapsed >= limits.MaximumDuration)
        {
            return RecordingRuntimeLimit.MaximumDuration;
        }

        if (snapshot.FileBytes >= limits.MaximumFileBytes)
        {
            return RecordingRuntimeLimit.MaximumFileSize;
        }

        if (snapshot.AvailableBytes <= limits.ReservedFreeBytesWhileRecording)
        {
            return RecordingRuntimeLimit.DiskReserve;
        }

        return null;
    }
}
