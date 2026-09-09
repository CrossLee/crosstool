using System.Collections.ObjectModel;

namespace Crosio.Windows.Media;

public enum RecordingTargetKind
{
    Display,
    Window,
    Region,
}

/// <summary>
/// A concrete recording source. Region requests retain their parent display
/// and must never be broadened to a whole display when cropping is unavailable.
/// </summary>
public abstract record RecordingTarget(RecordingTargetKind Kind, PixelSize SourceSize);

public sealed record DisplayRecordingTarget : RecordingTarget
{
    public DisplayRecordingTarget(string deviceName, nint monitorHandle, PixelSize sourceSize)
        : base(RecordingTargetKind.Display, sourceSize)
    {
        DeviceName = string.IsNullOrWhiteSpace(deviceName)
            ? throw new ArgumentException("A display device name is required.", nameof(deviceName))
            : deviceName;
        MonitorHandle = monitorHandle != nint.Zero
            ? monitorHandle
            : throw new ArgumentException("A non-zero monitor handle is required.", nameof(monitorHandle));
    }

    public string DeviceName { get; }

    public nint MonitorHandle { get; }
}

public sealed record WindowRecordingTarget : RecordingTarget
{
    public WindowRecordingTarget(nint windowHandle, PixelSize sourceSize)
        : base(RecordingTargetKind.Window, sourceSize)
    {
        WindowHandle = windowHandle != nint.Zero
            ? windowHandle
            : throw new ArgumentException("A non-zero window handle is required.", nameof(windowHandle));
    }

    public nint WindowHandle { get; }
}

public sealed record RegionRecordingTarget : RecordingTarget
{
    public RegionRecordingTarget(
        string displayDeviceName,
        nint monitorHandle,
        PixelRectangle sourceBounds,
        PixelRectangle region)
        : base(RecordingTargetKind.Region, region.Size)
    {
        DisplayDeviceName = string.IsNullOrWhiteSpace(displayDeviceName)
            ? throw new ArgumentException("A display device name is required.", nameof(displayDeviceName))
            : displayDeviceName;
        MonitorHandle = monitorHandle != nint.Zero
            ? monitorHandle
            : throw new ArgumentException("A non-zero monitor handle is required.", nameof(monitorHandle));

        if (region.Left < sourceBounds.Left ||
            region.Top < sourceBounds.Top ||
            region.Right > sourceBounds.Right ||
            region.Bottom > sourceBounds.Bottom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                "The recording region must be wholly contained in its selected display.");
        }

        SourceBounds = sourceBounds;
        Region = region;
    }

    public string DisplayDeviceName { get; }

    public nint MonitorHandle { get; }

    public PixelRectangle SourceBounds { get; }

    public PixelRectangle Region { get; }
}

public sealed record RecordingRequest
{
    public RecordingRequest(
        RecordingTarget target,
        bool includeSystemAudio,
        bool includeCursor,
        string completedDirectory,
        string? suggestedBaseName = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        IncludeSystemAudio = includeSystemAudio;
        IncludeCursor = includeCursor;
        CompletedDirectory = string.IsNullOrWhiteSpace(completedDirectory)
            ? throw new ArgumentException("A completed-recording directory is required.", nameof(completedDirectory))
            : Path.GetFullPath(completedDirectory);
        SuggestedBaseName = string.IsNullOrWhiteSpace(suggestedBaseName)
            ? "一爪录屏"
            : suggestedBaseName.Trim();
    }

    public RecordingTarget Target { get; }

    public bool IncludeSystemAudio { get; }

    public bool IncludeCursor { get; }

    public string CompletedDirectory { get; }

    public string SuggestedBaseName { get; }
}

public enum RecordingCapability
{
    WindowsGraphicsCapture,
    DisplayCapture,
    WindowCapture,
    RegionCrop,
    H264Encoding,
    AacEncoding,
    SystemAudioLoopback,
    CursorToggle,
    ProductionPipeline,
}

public enum CapabilityAvailability
{
    Supported,
    Unsupported,
    UnknownUntilStart,
}

public sealed record CapabilityStatus(
    RecordingCapability Capability,
    CapabilityAvailability Availability,
    string Code,
    string Message)
{
    public bool IsSupported => Availability == CapabilityAvailability.Supported;
}

public sealed class RecordingCapabilityReport
{
    private readonly IReadOnlyDictionary<RecordingCapability, CapabilityStatus> _statuses;

    public RecordingCapabilityReport(IEnumerable<CapabilityStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var materialized = statuses.ToArray();
        var duplicate = materialized
            .GroupBy(status => status.Capability)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Capability {duplicate.Key} was reported more than once.",
                nameof(statuses));
        }

        _statuses = new ReadOnlyDictionary<RecordingCapability, CapabilityStatus>(
            materialized.ToDictionary(status => status.Capability));
    }

    public IReadOnlyCollection<CapabilityStatus> Statuses => _statuses.Values.ToArray();

    public CapabilityStatus this[RecordingCapability capability] =>
        _statuses.TryGetValue(capability, out var status)
            ? status
            : new CapabilityStatus(
                capability,
                CapabilityAvailability.Unsupported,
                "capability-not-reported",
                $"The recording backend did not report {capability}.");

    public IReadOnlyList<CapabilityStatus> FindBlockingIssues(RecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var required = new List<RecordingCapability>
        {
            RecordingCapability.WindowsGraphicsCapture,
            RecordingCapability.H264Encoding,
            RecordingCapability.ProductionPipeline,
            request.Target.Kind switch
            {
                RecordingTargetKind.Display => RecordingCapability.DisplayCapture,
                RecordingTargetKind.Window => RecordingCapability.WindowCapture,
                RecordingTargetKind.Region => RecordingCapability.RegionCrop,
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            },
        };

        if (request.IncludeSystemAudio)
        {
            required.Add(RecordingCapability.AacEncoding);
            required.Add(RecordingCapability.SystemAudioLoopback);
        }

        if (!request.IncludeCursor)
        {
            required.Add(RecordingCapability.CursorToggle);
        }

        return required
            .Select(capability => this[capability])
            .Where(status => !status.IsSupported)
            .DistinctBy(status => status.Capability)
            .ToArray();
    }
}

public enum RecordingFailureCode
{
    CapabilityUnavailable,
    InvalidRequest,
    SessionAlreadyActive,
    SessionNotRecording,
    StartCancelled,
    BackendFailed,
    FinalizationFailed,
    InsufficientDiskSpace,
    LimitReached,
}

public sealed class RecordingException : Exception
{
    public RecordingException(
        RecordingFailureCode code,
        string message,
        Exception? innerException = null,
        bool preserveDraft = false)
        : base(message, innerException)
    {
        Code = code;
        PreserveDraft = preserveDraft;
    }

    public RecordingFailureCode Code { get; }

    /// <summary>
    /// True when a timed-out native operation can still own or finish writing
    /// the draft. Deleting such a file would race the recorder and risk data
    /// loss, so the controller surfaces the owned draft instead.
    /// </summary>
    public bool PreserveDraft { get; }
}
