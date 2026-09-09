#if WINDOWS
using System.Diagnostics;
using System.Runtime.Versioning;
using ScreenRecorderLib;

namespace Crosio.Windows.Media;

/// <summary>
/// Production WGC/Media Foundation recorder. ScreenRecorderLib is a thin
/// C++/CLI boundary over Windows.Graphics.Capture, Direct3D 11, Media
/// Foundation H.264/AAC and WASAPI loopback; no screenshot timer is used.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class ScreenRecorderWindowsSessionFactory : IWindowsRecordingSessionFactory
{
    private static readonly TimeSpan CapabilityProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NativeCreateTimeout = TimeSpan.FromSeconds(20);
    private readonly Lazy<Task<IReadOnlyCollection<CapabilityStatus>>> _capabilities;

    public ScreenRecorderWindowsSessionFactory()
    {
        _capabilities = new Lazy<Task<IReadOnlyCollection<CapabilityStatus>>>(
            ProbePipelineCapabilitiesAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<IReadOnlyCollection<CapabilityStatus>> GetPipelineCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        await _capabilities.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IActiveRecording> StartAsync(
        PreparedRecordingRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await StartNativeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RecordingException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RecordingException(
                RecordingFailureCode.CapabilityUnavailable,
                NativeRecordingFailureClassifier.Describe(exception),
                exception);
        }
    }

    private static async Task<IActiveRecording> StartNativeAsync(
        PreparedRecordingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupportedProfile(request.EncodingProfile);

        var draftDirectory = Path.GetDirectoryName(request.DraftPath)
            ?? throw new RecordingException(
                RecordingFailureCode.InvalidRequest,
                "The recording draft does not have a parent directory.");

        Directory.CreateDirectory(draftDirectory);
        if (File.Exists(request.DraftPath) || Directory.Exists(request.DraftPath))
        {
            throw new RecordingException(
                RecordingFailureCode.InvalidRequest,
                "The recording draft path is already in use.");
        }

        var plan = RecordingPipelinePlanner.Create(request);
        var worker = new NativeRecordingSessionWorker(
            $"Crosio recording session {request.SessionId:N}");
        var creation = worker.CreateAsync(() => CreateActiveRecording(
            request,
            plan,
            worker,
            cancellationToken));
        ScreenRecorderActiveRecording active;
        try
        {
            active = await creation
                .WaitAsync(NativeCreateTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            CleanupLateCreation(creation, worker);
            throw new RecordingException(
                RecordingFailureCode.BackendFailed,
                $"[recording-create-timeout] The native Windows recorder did not finish creating within 20 seconds. " +
                $"The draft was preserved because the recorder may still own it: {request.DraftPath}",
                exception,
                preserveDraft: true);
        }
        catch (OperationCanceledException exception)
        {
            CleanupLateCreation(creation, worker);
            throw new RecordingException(
                RecordingFailureCode.StartCancelled,
                $"Recording creation was cancelled while native cleanup remained in progress. " +
                $"The draft was preserved: {request.DraftPath}",
                exception,
                preserveDraft: true);
        }
        catch (RecordingException)
        {
            CleanupLateCreation(creation, worker);
            throw;
        }
        catch (Exception exception)
        {
            CleanupLateCreation(creation, worker);
            throw new RecordingException(
                RecordingFailureCode.CapabilityUnavailable,
                NativeRecordingFailureClassifier.Describe(exception),
                exception);
        }

        try
        {
            await active.BeginAsync(cancellationToken).ConfigureAwait(false);
            return active;
        }
        catch (OperationCanceledException exception)
        {
            await active.DisposeAsync().ConfigureAwait(false);
            if (RecordingStartDraftPolicy.ShouldPreserveAfterFailedStart(
                    active.NativeDisposalCompleted,
                    request.DraftPath))
            {
                throw new RecordingException(
                    RecordingFailureCode.StartCancelled,
                    $"Recording was cancelled after native capture may have written recoverable content. " +
                    $"The draft was preserved: {request.DraftPath}",
                    exception,
                    preserveDraft: true);
            }

            throw;
        }
        catch (RecordingException exception)
        {
            await active.DisposeAsync().ConfigureAwait(false);
            if (RecordingStartDraftPolicy.ShouldPreserveAfterFailedStart(
                    active.NativeDisposalCompleted,
                    request.DraftPath) &&
                !exception.PreserveDraft)
            {
                throw new RecordingException(
                    exception.Code,
                    $"{exception.Message} The draft was preserved because native cleanup was uncertain " +
                    $"or produced a finalized MP4: {request.DraftPath}",
                    exception,
                    preserveDraft: true);
            }

            throw;
        }
    }

    private static ScreenRecorderActiveRecording CreateActiveRecording(
        PreparedRecordingRequest request,
        RecordingPipelinePlan plan,
        NativeRecordingSessionWorker worker,
        CancellationToken cancellationToken)
    {
        Recorder? recorder = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = WindowsMediaEncodingProfileFactory.Create(request.EncodingProfile);
            var options = CreateRecorderOptions(plan);
            recorder = Recorder.CreateRecorder(options);
            cancellationToken.ThrowIfCancellationRequested();
            return new ScreenRecorderActiveRecording(recorder, request, worker);
        }
        catch
        {
            if (recorder is not null)
            {
                try
                {
                    recorder.Dispose();
                }
                catch
                {
                    // Preserve the actionable create/cancellation failure.
                }
            }

            throw;
        }
    }

    private static void CleanupLateCreation(
        Task<ScreenRecorderActiveRecording> creation,
        NativeRecordingSessionWorker worker) =>
        _ = CleanupLateCreationAsync(creation, worker);

    private static async Task CleanupLateCreationAsync(
        Task<ScreenRecorderActiveRecording> creation,
        NativeRecordingSessionWorker worker)
    {
        try
        {
            var active = await creation.ConfigureAwait(false);
            await active.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The start caller already owns the primary failure. This cleanup
            // remains on a background worker and must never mask that result.
        }
        finally
        {
            worker.Complete();
        }
    }

    private static async Task<IReadOnlyCollection<CapabilityStatus>> ProbePipelineCapabilitiesAsync()
    {
        using var worker = new DedicatedNativeCallWorker("Crosio ScreenRecorder capability probe");
        var probe = worker.InvokeAsync(ProbePipelineCapabilities);
        worker.Complete();
        try
        {
            return await probe.WaitAsync(CapabilityProbeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            ObserveLaterFault(probe);
            return CreateUnavailableCapabilities(new TimeoutException(
                "[screen-recorder-probe-timeout] The native ScreenRecorder probe exceeded 10 seconds.",
                exception));
        }
        catch (Exception exception)
        {
            return CreateUnavailableCapabilities(exception);
        }
    }

    private static IReadOnlyCollection<CapabilityStatus> ProbePipelineCapabilities()
    {
        try
        {
            // Creating the recorder loads the native C++/CLI bridge and the
            // core WGC/Media Foundation video pipeline. Audio is probed
            // separately so a broken/default-missing endpoint cannot disable
            // otherwise valid silent screen recording.
            using var recorder = Recorder.CreateRecorder(RecorderOptions.Default);
        }
        catch (Exception exception)
        {
            return CreateUnavailableCapabilities(exception);
        }

        var audioAvailable = false;
        string? audioFailure = null;
        try
        {
            var loopback = LoopbackAudioSource.Default;
            audioAvailable = loopback is not null && !string.IsNullOrWhiteSpace(loopback.DeviceName);
        }
        catch (Exception exception)
        {
            audioFailure = NativeRecordingFailureClassifier.Describe(exception);
        }

        var noAudioMessage = string.IsNullOrWhiteSpace(audioFailure)
            ? "No default WASAPI render endpoint is available."
            : $"The WASAPI loopback probe failed: {audioFailure}";
        return
        [
            Supported(
                RecordingCapability.RegionCrop,
                "wgc-gpu-region-crop-available",
                "Fixed-region GPU cropping is connected to the Windows Graphics Capture output."),
            Supported(
                RecordingCapability.H264Encoding,
                "media-foundation-h264-connected",
                "The Media Foundation H.264 encoder pipeline is connected and will be verified on start."),
            audioAvailable
                ? Supported(
                    RecordingCapability.AacEncoding,
                    "media-foundation-aac-connected",
                    "The Media Foundation AAC encoder pipeline is connected and will be verified on start.")
                : Unsupported(
                    RecordingCapability.AacEncoding,
                    "default-loopback-endpoint-unavailable",
                    $"{noAudioMessage} An AAC system-audio track cannot be created."),
            audioAvailable
                ? Supported(
                    RecordingCapability.SystemAudioLoopback,
                    "wasapi-loopback-connected",
                    "The default WASAPI render endpoint is available for system-audio loopback.")
                : Unsupported(
                    RecordingCapability.SystemAudioLoopback,
                    "default-loopback-endpoint-unavailable",
                    noAudioMessage),
            Supported(
                RecordingCapability.ProductionPipeline,
                "wgc-media-foundation-pipeline-connected",
                "The production Windows Graphics Capture to Media Foundation pipeline is connected."),
        ];
    }

    private static void ObserveLaterFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static IReadOnlyCollection<CapabilityStatus> CreateUnavailableCapabilities(
        Exception exception)
    {
        var detail = NativeRecordingFailureClassifier.Describe(exception);
        return
        [
            Unsupported(RecordingCapability.RegionCrop, "screen-recorder-native-load-failed", detail),
            Unsupported(RecordingCapability.H264Encoding, "screen-recorder-native-load-failed", detail),
            Unsupported(RecordingCapability.AacEncoding, "screen-recorder-native-load-failed", detail),
            Unsupported(RecordingCapability.SystemAudioLoopback, "screen-recorder-native-load-failed", detail),
            Unsupported(RecordingCapability.ProductionPipeline, "screen-recorder-native-load-failed", detail),
        ];
    }

    private static RecorderOptions CreateRecorderOptions(RecordingPipelinePlan plan)
    {
        var source = CreateSource(plan);
        var sourceOptions = new SourceOptions();
        sourceOptions.RecordingSources.Add(source);

        var audioOptions = new AudioOptions
        {
            IsAudioEnabled = plan.IncludeSystemAudio,
            Channels = AudioChannels.Stereo,
            Bitrate = AudioBitrate.bitrate_192kbps,
        };

        if (plan.IncludeSystemAudio)
        {
            var loopback = LoopbackAudioSource.Default;
            if (loopback is null || string.IsNullOrWhiteSpace(loopback.DeviceName))
            {
                throw new RecordingException(
                    RecordingFailureCode.CapabilityUnavailable,
                    "[default-loopback-endpoint-unavailable] No default WASAPI render endpoint is available.");
            }

            audioOptions.AudioSources.Add(loopback);
        }

        return new RecorderOptions
        {
            SourceOptions = sourceOptions,
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(plan.OutputSize.Width, plan.OutputSize.Height),
                Stretch = StretchMode.Fill,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    EncoderProfile = H264Profile.High,
                    BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
                },
                Framerate = checked((int)plan.FrameRate),
                Bitrate = checked((int)plan.VideoBitrate),
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true,
                IsMp4FastStartEnabled = true,
                IsFragmentedMp4Enabled = false,
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
            },
            AudioOptions = audioOptions,
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = plan.IncludeCursor,
                IsMouseClicksDetected = false,
            },
            SnapshotOptions = new SnapshotOptions
            {
                SnapshotsWithVideo = false,
            },
            OverlayOptions = new OverLayOptions(),
            LogOptions = new LogOptions
            {
                IsLogEnabled = false,
            },
        };
    }

    private static RecordingSourceBase CreateSource(RecordingPipelinePlan plan)
    {
        switch (plan.TargetKind)
        {
            case RecordingTargetKind.Display:
                return CreateDisplaySource(plan, crop: null);

            case RecordingTargetKind.Region:
                return CreateDisplaySource(
                    plan,
                    plan.SourceCrop ?? throw new RecordingException(
                        RecordingFailureCode.InvalidRequest,
                        "A fixed-region recording is missing its display-relative crop."));

            case RecordingTargetKind.Window:
                return new WindowRecordingSource(plan.WindowHandle)
                {
                    IsCursorCaptureEnabled = plan.IncludeCursor,
                };

            default:
                throw new RecordingException(
                    RecordingFailureCode.InvalidRequest,
                    "The requested Windows recording source is unsupported.");
        }
    }

    private static DisplayRecordingSource CreateDisplaySource(
        RecordingPipelinePlan plan,
        PixelRectangle? crop)
    {
        var source = new DisplayRecordingSource(
            plan.DisplayDeviceName ?? throw new RecordingException(
                RecordingFailureCode.InvalidRequest,
                "A display recording is missing its Windows device name."))
        {
            RecorderApi = RecorderApi.WindowsGraphicsCapture,
            IsCursorCaptureEnabled = plan.IncludeCursor,
        };

        if (crop is PixelRectangle rectangle)
        {
            source.SourceRect = new ScreenRect(
                rectangle.Left,
                rectangle.Top,
                rectangle.Width,
                rectangle.Height);
        }

        return source;
    }

    private static void EnsureSupportedProfile(RecordingEncodingProfile profile)
    {
        if (!string.Equals(profile.VideoCodec, "H264", StringComparison.Ordinal) ||
            !string.Equals(profile.Container, "MP4", StringComparison.Ordinal) ||
            profile.FrameRateNumerator != RecordingProfilePlanner.TargetFrameRate ||
            profile.FrameRateDenominator != 1 ||
            (profile.AudioCodec is not null &&
             !string.Equals(profile.AudioCodec, "AAC", StringComparison.Ordinal)))
        {
            throw new RecordingException(
                RecordingFailureCode.InvalidRequest,
                "The native Windows recorder requires 30 fps H.264 MP4 with optional AAC audio.");
        }
    }

    private static CapabilityStatus Supported(
        RecordingCapability capability,
        string code,
        string message) =>
        new(capability, CapabilityAvailability.Supported, code, message);

    private static CapabilityStatus Unsupported(
        RecordingCapability capability,
        string code,
        string message) =>
        new(capability, CapabilityAvailability.Unsupported, code, message);
}

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class ScreenRecorderActiveRecording :
    IActiveRecording,
    IRecordingRuntimeLimitSource,
    IRecordingRuntimeEndSource
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RuntimePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DurationStopLead = TimeSpan.FromSeconds(2);
    private const long FileStopHeadroom = 64L * 1024 * 1024;
    private const long DiskStopHeadroom = 64L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Recorder _recorder;
    private readonly PreparedRecordingRequest _request;
    private readonly NativeRecordingSessionWorker _nativeWorker;
    private readonly TaskCompletionSource<bool> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecorderCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecordingRuntimeLimit> _runtimeLimit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _runtimeEnded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _monitorCancellation = new();
    private Stopwatch? _elapsed;
    private DateTimeOffset _startedAt;
    private TimeSpan? _durationAtStop;
    private bool _stopInitiated;
    private bool _disposed;
    private int _nativeDisposalCompleted;

    public ScreenRecorderActiveRecording(
        Recorder recorder,
        PreparedRecordingRequest request,
        NativeRecordingSessionWorker nativeWorker)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _nativeWorker = nativeWorker ?? throw new ArgumentNullException(nameof(nativeWorker));
        _recorder.OnStatusChanged += RecorderOnStatusChanged;
        _recorder.OnRecordingComplete += RecorderOnRecordingComplete;
        _recorder.OnRecordingFailed += RecorderOnRecordingFailed;
    }

    public DateTimeOffset StartedAt
    {
        get
        {
            lock (_sync)
            {
                return _startedAt;
            }
        }
    }

    public string DraftPath => _request.DraftPath;

    public Task<RecordingRuntimeLimit> RuntimeLimitReached => _runtimeLimit.Task;

    public Task RuntimeEnded => _runtimeEnded.Task;

    internal bool NativeDisposalCompleted => Volatile.Read(ref _nativeDisposalCompleted) != 0;

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        var nativeStart = _nativeWorker.StartAsync(() =>
        {
            _recorder.Record(DraftPath);
            if (_recorder.Status == RecorderStatus.Recording)
            {
                MarkStarted();
            }
        });

        try
        {
            await AwaitNativeStartAndRecordingStateAsync(nativeStart)
                .WaitAsync(StartTimeout, cancellationToken)
                .ConfigureAwait(false);
            _ = Task.Run(MonitorRuntimeLimitsAsync);
        }
        catch (TimeoutException exception)
        {
            RequestSafeStop();
            throw new RecordingException(
                RecordingFailureCode.BackendFailed,
                "[recording-start-timeout] The Windows recorder did not enter the recording state within 20 seconds.",
                exception);
        }
        catch (OperationCanceledException)
        {
            RequestSafeStop();
            throw;
        }
        catch (RecordingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RequestSafeStop();
            throw new RecordingException(
                RecordingFailureCode.BackendFailed,
                "[recording-start-failed] Windows Graphics Capture or the Media Foundation encoder rejected the recording.",
                exception);
        }
    }

    private async Task AwaitNativeStartAndRecordingStateAsync(Task nativeStart)
    {
        await nativeStart.ConfigureAwait(false);
        await _started.Task.ConfigureAwait(false);
    }

    public async Task<RecordingBackendResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        RequestSafeStop();

        RecorderCompletion completion;
        try
        {
            completion = await _completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The native recorder keeps finalizing after a caller cancels its
            // wait. RecordingSessionController always waits without cancelling.
            throw;
        }

        var duration = _durationAtStop ?? completion.CompletedAt - StartedAt;
        var fileBytes = File.Exists(completion.Path)
            ? new FileInfo(completion.Path).Length
            : 0;
        var finalized = fileBytes > 0 && Mp4ContainerInspector.IsFinalized(completion.Path);

        return new RecordingBackendResult(
            completion.Path,
            duration,
            fileBytes,
            finalized);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        RequestSafeStop();
        try
        {
            await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RecordingException)
        {
            // Cancellation deliberately discards the draft; an encoder failure
            // is not a second actionable outcome for the caller.
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cleanupElapsed = Stopwatch.StartNew();
        var cleanupTimeout = TimeSpan.FromSeconds(10);
        bool stopRequired;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stopRequired = !_completion.Task.IsCompleted;
        }

        _monitorCancellation.Cancel();
        if (stopRequired)
        {
            try
            {
                RequestSafeStop();
                await _completion.Task
                    .WaitAsync(cleanupTimeout)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The controller owns reporting and removal of failed drafts.
            }
        }

        var nativeDisposal = _nativeWorker.DisposeNativeAsync(() =>
        {
            _recorder.OnStatusChanged -= RecorderOnStatusChanged;
            _recorder.OnRecordingComplete -= RecorderOnRecordingComplete;
            _recorder.OnRecordingFailed -= RecorderOnRecordingFailed;
            _recorder.Dispose();
        });
        try
        {
            var remaining = cleanupTimeout - cleanupElapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ObserveLaterFault(nativeDisposal);
            }
            else
            {
                await nativeDisposal.WaitAsync(remaining).ConfigureAwait(false);
                Volatile.Write(ref _nativeDisposalCompleted, 1);
            }
        }
        catch
        {
            ObserveLaterFault(nativeDisposal);
        }

        _monitorCancellation.Dispose();
    }

    private void RecorderOnStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        if (args.Status == RecorderStatus.Recording)
        {
            MarkStarted();
        }
    }

    private void RecorderOnRecordingComplete(object? sender, RecordingCompleteEventArgs args)
    {
        var path = string.IsNullOrWhiteSpace(args.FilePath) ? DraftPath : args.FilePath;
        _completion.TrySetResult(new RecorderCompletion(path, DateTimeOffset.UtcNow));
        _runtimeEnded.TrySetResult(true);
        if (!_started.Task.IsCompleted)
        {
            _started.TrySetException(new RecordingException(
                RecordingFailureCode.BackendFailed,
                "[recording-ended-before-start] The native recorder ended before producing a recording stream."));
        }
    }

    private void RecorderOnRecordingFailed(object? sender, RecordingFailedEventArgs args)
    {
        var detail = string.IsNullOrWhiteSpace(args.Error)
            ? "The native Windows recorder failed without an error message."
            : args.Error;
        var exception = new RecordingException(
            RecordingFailureCode.BackendFailed,
            $"[native-recording-failed] {detail}");
        _started.TrySetException(exception);
        _completion.TrySetException(exception);
        _runtimeEnded.TrySetResult(true);
    }

    private void MarkStarted()
    {
        lock (_sync)
        {
            if (_elapsed is null)
            {
                _startedAt = DateTimeOffset.UtcNow;
                _elapsed = Stopwatch.StartNew();
            }
        }

        _started.TrySetResult(true);
    }

    private void RequestSafeStop()
    {
        Task nativeStop;
        lock (_sync)
        {
            if (_stopInitiated || _completion.Task.IsCompleted)
            {
                return;
            }

            _stopInitiated = true;
            _durationAtStop = _elapsed?.Elapsed;
            _monitorCancellation.Cancel();
            nativeStop = _nativeWorker.StopAsync(() =>
            {
                try
                {
                    _recorder.Stop();
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(new RecordingException(
                        RecordingFailureCode.FinalizationFailed,
                        "[native-stop-failed] The Windows recorder could not begin MP4 finalization.",
                        exception));
                }
            });
        }

        ObserveLaterFault(nativeStop);
    }

    private static void ObserveLaterFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task MonitorRuntimeLimitsAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(RuntimePollInterval);
            while (await timer.WaitForNextTickAsync(_monitorCancellation.Token).ConfigureAwait(false))
            {
                var elapsed = _elapsed?.Elapsed ?? TimeSpan.Zero;
                var fileBytes = TryGetFileBytes(DraftPath);
                var availableBytes = TryGetAvailableBytes(DraftPath);
                var limit = EvaluateWithStopHeadroom(
                    new RecordingRuntimeSnapshot(elapsed, fileBytes, availableBytes),
                    _request.SafetyLimits);
                if (limit is RecordingRuntimeLimit reached)
                {
                    _runtimeLimit.TrySetResult(reached);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_monitorCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Losing limit telemetry must never be represented as a healthy
            // unbounded session. Force a disk-reserve stop through the same
            // controller finalization path.
            _runtimeLimit.TrySetResult(RecordingRuntimeLimit.DiskReserve);
        }
    }

    private static RecordingRuntimeLimit? EvaluateWithStopHeadroom(
        RecordingRuntimeSnapshot snapshot,
        RecordingSafetyLimits limits)
    {
        var durationLead = limits.MaximumDuration > DurationStopLead
            ? DurationStopLead
            : TimeSpan.FromTicks(Math.Max(1, limits.MaximumDuration.Ticks / 10));
        var durationThreshold = limits.MaximumDuration - durationLead;
        var fileHeadroom = Math.Min(FileStopHeadroom, Math.Max(1, limits.MaximumFileBytes / 100));
        var fileThreshold = limits.MaximumFileBytes - fileHeadroom;
        var diskThreshold = checked(limits.ReservedFreeBytesWhileRecording + DiskStopHeadroom);

        if (snapshot.Elapsed >= durationThreshold)
        {
            return RecordingRuntimeLimit.MaximumDuration;
        }

        if (snapshot.FileBytes >= fileThreshold)
        {
            return RecordingRuntimeLimit.MaximumFileSize;
        }

        if (snapshot.AvailableBytes <= diskThreshold)
        {
            return RecordingRuntimeLimit.DiskReserve;
        }

        return null;
    }

    private static long TryGetFileBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private static long TryGetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    private sealed record RecorderCompletion(string Path, DateTimeOffset CompletedAt);
}
#endif
