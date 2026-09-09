namespace Crosio.Windows.LongCapture;

public enum LongCaptureTrigger
{
    FirstScroll,
    ThrottledScroll,
    Tail,
    Manual,
    Final,
}

public sealed record LongCaptureSchedulingDecision(
    LongCaptureTrigger? CaptureNow = null,
    VerticalScrollDirection? Direction = null,
    TimeSpan? WakeAt = null,
    bool StopAutomaticCapture = false)
{
    public bool ShouldCapture => CaptureNow is not null;
}

/// <summary>
/// Deterministic scheduling model for scroll capture. The first vertical event
/// samples immediately, a burst is capped at one sample per 140 ms, an in-flight
/// capture coalesces to the latest direction, and a final tail sample is due
/// 220 ms after the latest scroll.
/// </summary>
public sealed class LongCaptureSamplingStateMachine
{
    public static readonly TimeSpan DefaultThrottleInterval = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan DefaultTailInterval = TimeSpan.FromMilliseconds(220);

    private readonly TimeSpan _throttleInterval;
    private readonly TimeSpan _tailInterval;
    private TimeSpan? _lastTimestamp;
    private TimeSpan? _lastSampleAt;
    private TimeSpan? _tailDueAt;
    private VerticalScrollDirection _latestDirection = VerticalScrollDirection.Down;
    private bool _captureInFlight;
    private bool _pendingScroll;
    private bool _tailIssued;
    private bool _sequenceClosed = true;
    private bool _finishPending;
    private bool _automaticEnabled = true;

    public LongCaptureSamplingStateMachine(
        TimeSpan? throttleInterval = null,
        TimeSpan? tailInterval = null)
    {
        _throttleInterval = throttleInterval ?? DefaultThrottleInterval;
        _tailInterval = tailInterval ?? DefaultTailInterval;
        if (_throttleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(throttleInterval));
        }

        if (_tailInterval <= _throttleInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tailInterval),
                "The tail interval must be longer than the throttle interval.");
        }
    }

    public bool AutomaticCaptureEnabled => _automaticEnabled;

    public bool CaptureInFlight => _captureInFlight;

    public LongCaptureSchedulingDecision OnScroll(
        TimeSpan timestamp,
        VerticalScrollDirection direction,
        double horizontalMagnitude = 0,
        double verticalMagnitude = 1)
    {
        EnsureMonotonic(timestamp);
        if (!_automaticEnabled)
        {
            return new LongCaptureSchedulingDecision();
        }

        if (double.IsFinite(horizontalMagnitude)
            && double.IsFinite(verticalMagnitude)
            && Math.Abs(horizontalMagnitude) > Math.Abs(verticalMagnitude)
            && Math.Abs(horizontalMagnitude) >= 30)
        {
            _automaticEnabled = false;
            _pendingScroll = false;
            _tailDueAt = null;
            return new LongCaptureSchedulingDecision(StopAutomaticCapture: true);
        }

        if (!double.IsFinite(verticalMagnitude) || Math.Abs(verticalMagnitude) < double.Epsilon)
        {
            return NextWakeDecision();
        }

        _latestDirection = direction;
        _tailDueAt = timestamp + _tailInterval;
        _tailIssued = false;
        var isFirstEventInSequence = _sequenceClosed;
        var shouldSampleImmediately = isFirstEventInSequence
            || _lastSampleAt is null
            || timestamp - _lastSampleAt.Value >= _throttleInterval;
        _sequenceClosed = false;
        if (!_captureInFlight && shouldSampleImmediately)
        {
            return BeginCapture(
                _lastSampleAt is null || isFirstEventInSequence
                    ? LongCaptureTrigger.FirstScroll
                    : LongCaptureTrigger.ThrottledScroll,
                direction,
                timestamp);
        }

        _pendingScroll = true;
        return NextWakeDecision();
    }

    public LongCaptureSchedulingDecision OnTimer(TimeSpan timestamp)
    {
        EnsureMonotonic(timestamp);
        if (_captureInFlight)
        {
            return NextWakeDecision();
        }

        if (_finishPending)
        {
            _finishPending = false;
            return BeginCapture(LongCaptureTrigger.Final, _latestDirection, timestamp);
        }

        if (_pendingScroll
            && (_lastSampleAt is null
                || timestamp - _lastSampleAt.Value >= _throttleInterval))
        {
            _pendingScroll = false;
            return BeginCapture(LongCaptureTrigger.ThrottledScroll, _latestDirection, timestamp);
        }

        if (_automaticEnabled
            && !_tailIssued
            && _tailDueAt is { } tailDue
            && timestamp >= tailDue)
        {
            _tailIssued = true;
            _pendingScroll = false;
            return BeginCapture(LongCaptureTrigger.Tail, _latestDirection, timestamp);
        }

        return NextWakeDecision();
    }

    public LongCaptureSchedulingDecision OnCaptureCompleted(TimeSpan timestamp)
    {
        EnsureMonotonic(timestamp);
        if (!_captureInFlight)
        {
            throw new InvalidOperationException("There is no in-flight long-capture sample.");
        }

        _captureInFlight = false;
        if (_finishPending)
        {
            _finishPending = false;
            return BeginCapture(LongCaptureTrigger.Final, _latestDirection, timestamp);
        }

        if (_pendingScroll
            && (_lastSampleAt is null
                || timestamp - _lastSampleAt.Value >= _throttleInterval))
        {
            _pendingScroll = false;
            return BeginCapture(LongCaptureTrigger.ThrottledScroll, _latestDirection, timestamp);
        }

        if (_tailIssued && _tailDueAt is { } due && timestamp >= due)
        {
            _sequenceClosed = true;
        }

        return OnTimer(timestamp);
    }

    public LongCaptureSchedulingDecision RequestManualCapture(
        TimeSpan timestamp,
        VerticalScrollDirection direction)
    {
        EnsureMonotonic(timestamp);
        _latestDirection = direction;
        if (_captureInFlight)
        {
            _pendingScroll = true;
            return NextWakeDecision();
        }

        return BeginCapture(LongCaptureTrigger.Manual, direction, timestamp);
    }

    public LongCaptureSchedulingDecision RequestFinish(TimeSpan timestamp)
    {
        EnsureMonotonic(timestamp);
        _automaticEnabled = false;
        _pendingScroll = false;
        _tailDueAt = null;
        if (_captureInFlight)
        {
            _finishPending = true;
            return new LongCaptureSchedulingDecision();
        }

        return BeginCapture(LongCaptureTrigger.Final, _latestDirection, timestamp);
    }

    private LongCaptureSchedulingDecision BeginCapture(
        LongCaptureTrigger trigger,
        VerticalScrollDirection direction,
        TimeSpan timestamp)
    {
        _captureInFlight = true;
        _lastSampleAt = timestamp;
        return new LongCaptureSchedulingDecision(trigger, direction);
    }

    private LongCaptureSchedulingDecision NextWakeDecision()
    {
        if (_captureInFlight)
        {
            // Completion re-evaluates both the coalesced scroll and tail. A
            // timer whose deadline has already elapsed would otherwise spin
            // while a slow capture is still in progress.
            return new LongCaptureSchedulingDecision();
        }

        TimeSpan? wakeAt = null;
        if (_pendingScroll && _lastSampleAt is { } lastSample)
        {
            wakeAt = lastSample + _throttleInterval;
        }

        if (!_tailIssued && _tailDueAt is { } tailDue)
        {
            wakeAt = wakeAt is null || tailDue < wakeAt ? tailDue : wakeAt;
        }

        return new LongCaptureSchedulingDecision(WakeAt: wakeAt);
    }

    private void EnsureMonotonic(TimeSpan timestamp)
    {
        if (timestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        if (_lastTimestamp is { } last && timestamp < last)
        {
            throw new ArgumentException("Long-capture timestamps must be monotonic.", nameof(timestamp));
        }

        _lastTimestamp = timestamp;
    }
}
