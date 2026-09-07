#if WINDOWS
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace Crosio.Windows.Media;

/// <summary>
/// The result of probing the actual Windows APIs available to Crosio. API
/// presence is deliberately separate from an end-to-end production pipeline:
/// constructing a MediaEncodingProfile does not prove that MediaTranscoder has
/// accepted a screen-frame source.
/// </summary>
public sealed record WindowsNativeRecordingSupport(
    bool OperatingSystemSupported,
    bool GraphicsCaptureSupported,
    bool DefaultRenderEndpointPresent,
    bool H264EncodingTypeAvailable,
    bool AacEncodingTypeAvailable,
    string? GraphicsCaptureProbeError,
    string? AudioEndpointProbeError,
    string? MediaEncodingProbeError);

[SupportedOSPlatform("windows10.0.19041")]
public static class WindowsRecordingCapabilityProbe
{
    public static WindowsNativeRecordingSupport Probe()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return new WindowsNativeRecordingSupport(
                false,
                false,
                false,
                false,
                false,
                null,
                null,
                null);
        }

        var graphicsCaptureSupported = false;
        string? graphicsError = null;
        try
        {
            graphicsCaptureSupported = GraphicsCaptureSession.IsSupported();
        }
        catch (Exception exception)
        {
            graphicsError = DescribeProbeError(exception);
        }

        var defaultRenderEndpointPresent = false;
        string? audioError = null;
        try
        {
            defaultRenderEndpointPresent = !string.IsNullOrWhiteSpace(
                MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
        }
        catch (Exception exception)
        {
            audioError = DescribeProbeError(exception);
        }

        var h264Available = false;
        var aacAvailable = false;
        string? mediaError = null;
        try
        {
            var silentProfile = WindowsMediaEncodingProfileFactory.Create(
                RecordingProfilePlanner.Create(new PixelSize(1920, 1080), includeSystemAudio: false));
            h264Available = string.Equals(
                silentProfile.Video.Subtype,
                MediaEncodingSubtypes.H264,
                StringComparison.OrdinalIgnoreCase);

            var audio = AudioEncodingProperties.CreateAac(48_000, 2, 192_000);
            aacAvailable = string.Equals(
                audio.Subtype,
                MediaEncodingSubtypes.Aac,
                StringComparison.OrdinalIgnoreCase);

            // Creating the transcoder validates that the media projection is
            // present. Actual encoders are still verified by the real start.
            _ = new MediaTranscoder
            {
                HardwareAccelerationEnabled = true,
            };
        }
        catch (Exception exception)
        {
            mediaError = DescribeProbeError(exception);
        }

        return new WindowsNativeRecordingSupport(
            true,
            graphicsCaptureSupported,
            defaultRenderEndpointPresent,
            h264Available,
            aacAvailable,
            graphicsError,
            audioError,
            mediaError);
    }

    private static string DescribeProbeError(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}

[SupportedOSPlatform("windows10.0.19041")]
public static class WindowsMediaEncodingProfileFactory
{
    public static MediaEncodingProfile Create(RecordingEncodingProfile requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
        profile.Video.Subtype = MediaEncodingSubtypes.H264;
        profile.Video.Width = (uint)requested.OutputSize.Width;
        profile.Video.Height = (uint)requested.OutputSize.Height;
        profile.Video.Bitrate = requested.VideoBitrate;
        profile.Video.FrameRate.Numerator = requested.FrameRateNumerator;
        profile.Video.FrameRate.Denominator = requested.FrameRateDenominator;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        profile.Audio = requested.AudioCodec is null
            ? null
            : AudioEncodingProperties.CreateAac(
                requested.AudioSampleRate,
                requested.AudioChannels,
                requested.AudioBitrate);
        return profile;
    }
}

/// <summary>
/// Boundary for the production Windows.Graphics.Capture, Media Foundation and
/// WASAPI implementation. Tests can inject a deterministic implementation;
/// production uses <see cref="ScreenRecorderWindowsSessionFactory"/>.
/// </summary>
public interface IWindowsRecordingSessionFactory
{
    ValueTask<IReadOnlyCollection<CapabilityStatus>> GetPipelineCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<IActiveRecording> StartAsync(
        PreparedRecordingRequest request,
        CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsGraphicsCaptureRecordingBackend : IRecordingBackend
{
    private static readonly TimeSpan CapabilityProbeTimeout = TimeSpan.FromSeconds(10);
    private readonly IWindowsRecordingSessionFactory _sessionFactory;
    private readonly Lazy<Task<WindowsNativeRecordingSupport>> _nativeCapabilities;

    public WindowsGraphicsCaptureRecordingBackend(IWindowsRecordingSessionFactory? sessionFactory = null)
    {
        _sessionFactory = sessionFactory ?? new ScreenRecorderWindowsSessionFactory();
        _nativeCapabilities = new Lazy<Task<WindowsNativeRecordingSupport>>(
            ProbeNativeCapabilitiesAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<RecordingCapabilityReport> CheckCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nativeTask = _nativeCapabilities.Value;
        var pipelineTask = _sessionFactory
            .GetPipelineCapabilitiesAsync(cancellationToken)
            .AsTask();
        var native = await nativeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pipeline = await pipelineTask.ConfigureAwait(false);
        var statuses = CreateBaseStatuses(native).ToDictionary(status => status.Capability);

        foreach (var status in pipeline)
        {
            if (status.IsSupported &&
                statuses.TryGetValue(status.Capability, out var nativeStatus) &&
                nativeStatus.Availability == CapabilityAvailability.Unsupported &&
                status.Capability is RecordingCapability.H264Encoding or RecordingCapability.AacEncoding)
            {
                // A connected native bridge cannot manufacture an encoder that
                // the OS probe says is absent (for example Windows N without
                // the Media Feature Pack).
                continue;
            }

            statuses[status.Capability] = status;
        }

        return new RecordingCapabilityReport(statuses.Values);
    }

    public async Task<IActiveRecording> StartAsync(
        PreparedRecordingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = await CheckCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var blockers = capabilities.FindBlockingIssues(request.Request);
        if (blockers.Count > 0)
        {
            throw new RecordingException(
                RecordingFailureCode.CapabilityUnavailable,
                string.Join(" ", blockers.Select(status => $"[{status.Code}] {status.Message}")));
        }

        // The exact Media Foundation profile is materialized on the recording
        // session's dedicated MTA worker immediately before CreateRecorder.
        return await _sessionFactory.StartAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WindowsNativeRecordingSupport> ProbeNativeCapabilitiesAsync()
    {
        using var worker = new DedicatedNativeCallWorker("Crosio Windows recording capability probe");
        var probe = worker.InvokeAsync(WindowsRecordingCapabilityProbe.Probe);
        worker.Complete();
        try
        {
            return await probe.WaitAsync(CapabilityProbeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLaterFault(probe);
            const string detail = "TimeoutException: the Windows recording capability probe exceeded 10 seconds.";
            return new WindowsNativeRecordingSupport(
                OperatingSystemSupported: true,
                GraphicsCaptureSupported: false,
                DefaultRenderEndpointPresent: false,
                H264EncodingTypeAvailable: false,
                AacEncodingTypeAvailable: false,
                GraphicsCaptureProbeError: detail,
                AudioEndpointProbeError: detail,
                MediaEncodingProbeError: detail);
        }
    }

    private static void ObserveLaterFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static IEnumerable<CapabilityStatus> CreateBaseStatuses(WindowsNativeRecordingSupport native)
    {
        var graphicsError = FormatProbeError(native.GraphicsCaptureProbeError);
        var audioError = FormatProbeError(native.AudioEndpointProbeError);
        var mediaError = FormatProbeError(native.MediaEncodingProbeError);
        var captureSupported = native.OperatingSystemSupported && native.GraphicsCaptureSupported;

        yield return new CapabilityStatus(
            RecordingCapability.WindowsGraphicsCapture,
            captureSupported ? CapabilityAvailability.Supported : CapabilityAvailability.Unsupported,
            captureSupported ? "wgc-available" : "wgc-unavailable",
            captureSupported
                ? "Windows.Graphics.Capture is available."
                : $"Windows.Graphics.Capture is unavailable.{graphicsError}");
        yield return new CapabilityStatus(
            RecordingCapability.DisplayCapture,
            captureSupported ? CapabilityAvailability.Supported : CapabilityAvailability.Unsupported,
            captureSupported ? "display-api-available" : "display-api-unavailable",
            captureSupported
                ? "The native API can create a display capture item."
                : "Display recording requires Windows.Graphics.Capture.");
        yield return new CapabilityStatus(
            RecordingCapability.WindowCapture,
            captureSupported ? CapabilityAvailability.Supported : CapabilityAvailability.Unsupported,
            captureSupported ? "window-api-available" : "window-api-unavailable",
            captureSupported
                ? "The native API can create a window capture item."
                : "Window recording requires Windows.Graphics.Capture.");
        yield return new CapabilityStatus(
            RecordingCapability.RegionCrop,
            CapabilityAvailability.Unsupported,
            "region-frame-crop-not-connected",
            "The production GPU frame-crop stage for fixed-region recording is not connected.");
        yield return new CapabilityStatus(
            RecordingCapability.H264Encoding,
            native.H264EncodingTypeAvailable
                ? CapabilityAvailability.UnknownUntilStart
                : CapabilityAvailability.Unsupported,
            native.H264EncodingTypeAvailable
                ? "h264-requires-transcoder-prepare"
                : "h264-profile-unavailable",
            native.H264EncodingTypeAvailable
                ? "H.264 is requested, but Windows must accept the real frame source before support is confirmed."
                : $"Windows did not expose the required H.264 encoding profile.{mediaError}");
        yield return new CapabilityStatus(
            RecordingCapability.AacEncoding,
            native.AacEncodingTypeAvailable
                ? CapabilityAvailability.UnknownUntilStart
                : CapabilityAvailability.Unsupported,
            native.AacEncodingTypeAvailable
                ? "aac-requires-transcoder-prepare"
                : "aac-profile-unavailable",
            native.AacEncodingTypeAvailable
                ? "AAC is requested, but Windows must accept the real audio source before support is confirmed."
                : $"Windows did not expose the required AAC encoding profile.{mediaError}");
        yield return new CapabilityStatus(
            RecordingCapability.SystemAudioLoopback,
            CapabilityAvailability.Unsupported,
            native.DefaultRenderEndpointPresent
                ? "wasapi-bridge-not-connected"
                : "default-render-endpoint-unavailable",
            native.DefaultRenderEndpointPresent
                ? "A render endpoint exists, but the WASAPI loopback-to-encoder bridge is not connected."
                : $"No default Windows audio render endpoint is available for loopback capture.{audioError}");
        yield return new CapabilityStatus(
            RecordingCapability.CursorToggle,
            captureSupported ? CapabilityAvailability.Supported : CapabilityAvailability.Unsupported,
            captureSupported ? "wgc-cursor-toggle-available" : "wgc-cursor-toggle-unavailable",
            captureSupported
                ? "Windows.Graphics.Capture can explicitly include or exclude the cursor."
                : "Cursor visibility cannot be controlled without Windows.Graphics.Capture.");
        yield return new CapabilityStatus(
            RecordingCapability.ProductionPipeline,
            CapabilityAvailability.Unsupported,
            "windows-media-pipeline-not-connected",
            "The production WGC/WASAPI frame-to-MediaTranscoder pipeline is not connected in this build.");
    }

    private static string FormatProbeError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $" Native probe: {error}";
}
#endif
