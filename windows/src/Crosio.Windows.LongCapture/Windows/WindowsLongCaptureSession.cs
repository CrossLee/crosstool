#if WINDOWS
using System.Diagnostics;
using System.Runtime.Versioning;
using Crosio.Windows.Capture;

namespace Crosio.Windows.LongCapture;

public sealed record LongCaptureFrameProcessedEventArgs(
    LongCaptureTrigger Trigger,
    LongCaptureAddFrameResult Result,
    string? Warning = null);

/// <summary>
/// Captures one fixed virtual-desktop region and listens to real Windows wheel
/// messages. The selected region may have negative coordinates. This initial
/// backend uses the Capture project's desktop compositor source for each frame;
/// a persistent Windows.Graphics.Capture session can replace that source later
/// without changing the stitcher or scheduler.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsLongCaptureSession : IAsyncDisposable
{
    private readonly PixelRect _region;
    private readonly IScreenshotCaptureService _captureService;
    private readonly LongCaptureStitcher _stitcher;
    private readonly LongCaptureSamplingStateMachine _scheduler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly WindowsMouseWheelMonitor _wheelMonitor;
    private readonly System.Threading.Timer _timer;
    private VerticalScrollDirection _latestDirection = VerticalScrollDirection.Down;
    private string? _lastWarning;
    private int _stopping;
    private int _disposed;

    private WindowsLongCaptureSession(
        PixelRect region,
        IScreenshotCaptureService captureService,
        LongCaptureStitcher stitcher)
    {
        _region = region;
        _captureService = captureService;
        _stitcher = stitcher;
        _timer = new System.Threading.Timer(
            OnTimer,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _wheelMonitor = new WindowsMouseWheelMonitor();
        _wheelMonitor.WheelObserved += OnWheelObserved;
    }

    public event EventHandler<LongCaptureFrameProcessedEventArgs>? FrameProcessed;

    public PixelRect Region => _region;

    public string? LastWarning => _lastWarning;

    public int OutputWidth => _stitcher.OutputWidth;

    public int OutputHeight => _stitcher.OutputHeight;

    public static async Task<WindowsLongCaptureSession> StartAsync(
        PixelRect region,
        IScreenshotCaptureService? captureService = null,
        LongCaptureStitcher? stitcher = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("Long capture requires Windows 10 version 2004 or newer.");
        }

        if (region.PixelCount > LongCaptureStitcher.DefaultMaximumPixelCount
            || region.Width > LongCaptureStitcher.DefaultMaximumEdgePixels
            || region.Height > LongCaptureStitcher.DefaultMaximumEdgePixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                "The selected region already exceeds the 20-million-pixel output limit.");
        }

        var session = new WindowsLongCaptureSession(
            region,
            captureService ?? new WindowsScreenshotCaptureService(),
            stitcher ?? new LongCaptureStitcher());
        try
        {
            await session._captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var first = await session.CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
                var result = session._stitcher.AddInitialFrame(first);
                if (!result.Accepted)
                {
                    throw new InvalidOperationException("The first frame exceeds the long-capture safety limits.");
                }
            }
            finally
            {
                session._captureGate.Release();
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CaptureManuallyAsync(
        VerticalScrollDirection direction,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_stateLock)
        {
            _latestDirection = direction;
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = await CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
            var result = _stitcher.AddFrame(frame, direction);
            ProcessResult(LongCaptureTrigger.Manual, result);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>
    /// Stops wheel observation, waits for any active sample, then captures one
    /// final frame. A failed final capture or overlap keeps and returns the
    /// already stitched result.
    /// </summary>
    public async Task<CapturedImage> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _wheelMonitor.WheelObserved -= OnWheelObserved;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _wheelMonitor.Dispose();
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var frame = await CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
                var result = _stitcher.AddFrame(frame, _latestDirection);
                ProcessResult(LongCaptureTrigger.Final, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                _lastWarning = $"末帧捕获失败，已保留当前拼接结果：{error.Message}";
            }

            return WindowsLongCaptureFrameCodec.Encode(_stitcher.BuildFrame());
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _stopping, 1);
        _wheelMonitor.WheelObserved -= OnWheelObserved;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _wheelMonitor.Dispose();
        await _captureGate.WaitAsync().ConfigureAwait(false);
        _captureGate.Release();
        _timer.Dispose();
        _captureGate.Dispose();
    }

    private void OnWheelObserved(object? sender, MouseWheelObservation observation)
    {
        if (Volatile.Read(ref _stopping) != 0
            || observation.ScreenX < _region.Left
            || observation.ScreenX >= _region.Right
            || observation.ScreenY < _region.Top
            || observation.ScreenY >= _region.Bottom)
        {
            return;
        }

        LongCaptureSchedulingDecision decision;
        lock (_stateLock)
        {
            if (observation.HorizontalDelta != 0)
            {
                decision = _scheduler.OnScroll(
                    _clock.Elapsed,
                    _latestDirection,
                    Math.Abs(observation.HorizontalDelta),
                    Math.Abs(observation.VerticalDelta));
            }
            else if (observation.VerticalDelta != 0)
            {
                _latestDirection = observation.VerticalDelta < 0
                    ? VerticalScrollDirection.Down
                    : VerticalScrollDirection.Up;
                decision = _scheduler.OnScroll(
                    _clock.Elapsed,
                    _latestDirection,
                    verticalMagnitude: Math.Abs(observation.VerticalDelta));
            }
            else
            {
                return;
            }
        }

        if (decision.StopAutomaticCapture)
        {
            _lastWarning = "检测到横向滚动，已停止自动采样；当前结果仍可完成或手动补采。";
        }

        ApplyDecision(decision, CancellationToken.None);
    }

    private void OnTimer(object? state)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        LongCaptureSchedulingDecision decision;
        lock (_stateLock)
        {
            decision = _scheduler.OnTimer(_clock.Elapsed);
        }

        ApplyDecision(decision, CancellationToken.None);
    }

    private void ApplyDecision(
        LongCaptureSchedulingDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.CaptureNow is { } trigger && decision.Direction is { } direction)
        {
            _ = CaptureScheduledAsync(trigger, direction, cancellationToken);
            return;
        }

        if (decision.WakeAt is { } wakeAt && Volatile.Read(ref _stopping) == 0)
        {
            var due = wakeAt - _clock.Elapsed;
            _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CaptureScheduledAsync(
        LongCaptureTrigger trigger,
        VerticalScrollDirection direction,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            try
            {
                var frame = await CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
                var result = _stitcher.AddFrame(frame, direction);
                ProcessResult(trigger, result);
                if (result.Status is LongCaptureAddFrameStatus.AmbiguousOverlap
                    or LongCaptureAddFrameStatus.LimitExceeded)
                {
                    Interlocked.Exchange(ref _stopping, 1);
                    _wheelMonitor.WheelObserved -= OnWheelObserved;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _lastWarning = "自动采样被中断，当前已拼接结果仍然保留。";
            }
            catch (Exception error)
            {
                _lastWarning = $"本次采样失败，当前已拼接结果仍然保留：{error.Message}";
            }
        }
        finally
        {
            _captureGate.Release();
            LongCaptureSchedulingDecision next;
            lock (_stateLock)
            {
                next = _scheduler.OnCaptureCompleted(_clock.Elapsed);
            }

            ApplyDecision(next, CancellationToken.None);
        }
    }

    private async Task<LongCaptureFrame> CaptureFrameAsync(CancellationToken cancellationToken)
    {
        var image = await _captureService.CaptureAsync(
            new ScreenshotCaptureRequest(
                new RegionCaptureTarget(_region),
                IncludeCursor: false,
                MaximumPixelCount: LongCaptureStitcher.DefaultMaximumPixelCount),
            cancellationToken).ConfigureAwait(false);
        return WindowsLongCaptureFrameCodec.Decode(image);
    }

    private void ProcessResult(LongCaptureTrigger trigger, LongCaptureAddFrameResult result)
    {
        _lastWarning = result.Status switch
        {
            LongCaptureAddFrameStatus.AmbiguousOverlap =>
                "页面含重复纹理，检测到多个可能重叠位置；已拒绝该帧，避免生成错位长图。",
            LongCaptureAddFrameStatus.NoOverlap =>
                "本次画面没有可靠重叠区域，已忽略该帧。",
            LongCaptureAddFrameStatus.IncompatibleFrame =>
                "选区尺寸发生变化，已忽略该帧。",
            LongCaptureAddFrameStatus.LimitExceeded =>
                "已达到 2000 万像素或 20000 像素最长边限制，自动采样已停止。",
            _ => null,
        };
        FrameProcessed?.Invoke(
            this,
            new LongCaptureFrameProcessedEventArgs(trigger, result, _lastWarning));
    }

}
#endif
