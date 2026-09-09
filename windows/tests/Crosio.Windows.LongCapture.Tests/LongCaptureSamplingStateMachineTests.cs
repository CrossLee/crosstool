namespace Crosio.Windows.LongCapture.Tests;

public sealed class LongCaptureSamplingStateMachineTests
{
    [Fact]
    public void FirstScrollSamplesImmediately()
    {
        var state = new LongCaptureSamplingStateMachine();

        var decision = state.OnScroll(
            TimeSpan.Zero,
            VerticalScrollDirection.Down);

        Assert.Equal(LongCaptureTrigger.FirstScroll, decision.CaptureNow);
        Assert.Equal(VerticalScrollDirection.Down, decision.Direction);
        Assert.True(state.CaptureInFlight);
    }

    [Fact]
    public void BurstIsThrottledAndBusyEventsCoalesceToLatestDirection()
    {
        var state = new LongCaptureSamplingStateMachine();
        _ = state.OnScroll(TimeSpan.Zero, VerticalScrollDirection.Down);
        var queued = state.OnScroll(
            TimeSpan.FromMilliseconds(60),
            VerticalScrollDirection.Up);

        Assert.False(queued.ShouldCapture);
        Assert.Null(queued.WakeAt);

        var stillWaiting = state.OnCaptureCompleted(TimeSpan.FromMilliseconds(80));
        Assert.False(stillWaiting.ShouldCapture);
        Assert.Equal(TimeSpan.FromMilliseconds(140), stillWaiting.WakeAt);

        var sampled = state.OnTimer(TimeSpan.FromMilliseconds(140));
        Assert.Equal(LongCaptureTrigger.ThrottledScroll, sampled.CaptureNow);
        Assert.Equal(VerticalScrollDirection.Up, sampled.Direction);
    }

    [Fact]
    public void TailFrameIsCapturedTwoHundredTwentyMillisecondsAfterLastScroll()
    {
        var state = new LongCaptureSamplingStateMachine();
        _ = state.OnScroll(TimeSpan.Zero, VerticalScrollDirection.Down);
        var afterFirst = state.OnCaptureCompleted(TimeSpan.FromMilliseconds(20));

        Assert.Equal(TimeSpan.FromMilliseconds(220), afterFirst.WakeAt);
        Assert.False(state.OnTimer(TimeSpan.FromMilliseconds(219)).ShouldCapture);

        var tail = state.OnTimer(TimeSpan.FromMilliseconds(220));
        Assert.Equal(LongCaptureTrigger.Tail, tail.CaptureNow);
    }

    [Fact]
    public void NewSequenceAfterTailAgainSamplesImmediately()
    {
        var state = new LongCaptureSamplingStateMachine();
        _ = state.OnScroll(TimeSpan.Zero, VerticalScrollDirection.Down);
        _ = state.OnCaptureCompleted(TimeSpan.FromMilliseconds(10));
        _ = state.OnTimer(TimeSpan.FromMilliseconds(220));
        _ = state.OnCaptureCompleted(TimeSpan.FromMilliseconds(230));

        var next = state.OnScroll(
            TimeSpan.FromMilliseconds(250),
            VerticalScrollDirection.Down);

        Assert.Equal(LongCaptureTrigger.FirstScroll, next.CaptureNow);
    }

    [Fact]
    public void FinishDuringCaptureRunsOneFinalFrameAfterItCompletes()
    {
        var state = new LongCaptureSamplingStateMachine();
        _ = state.OnScroll(TimeSpan.Zero, VerticalScrollDirection.Down);

        var waiting = state.RequestFinish(TimeSpan.FromMilliseconds(20));
        Assert.False(waiting.ShouldCapture);

        var final = state.OnCaptureCompleted(TimeSpan.FromMilliseconds(40));
        Assert.Equal(LongCaptureTrigger.Final, final.CaptureNow);
    }

    [Fact]
    public void DominantHorizontalGestureStopsAutomaticCapture()
    {
        var state = new LongCaptureSamplingStateMachine();

        var decision = state.OnScroll(
            TimeSpan.Zero,
            VerticalScrollDirection.Down,
            horizontalMagnitude: 120,
            verticalMagnitude: 0);

        Assert.True(decision.StopAutomaticCapture);
        Assert.False(state.AutomaticCaptureEnabled);
        Assert.False(state.OnScroll(
            TimeSpan.FromMilliseconds(10),
            VerticalScrollDirection.Down).ShouldCapture);
    }

    [Fact]
    public void TinyHorizontalNoiseDoesNotStopAutomaticCapture()
    {
        var state = new LongCaptureSamplingStateMachine();

        var ignored = state.OnScroll(
            TimeSpan.Zero,
            VerticalScrollDirection.Down,
            horizontalMagnitude: 8,
            verticalMagnitude: 0);
        var vertical = state.OnScroll(
            TimeSpan.FromMilliseconds(1),
            VerticalScrollDirection.Down);

        Assert.False(ignored.StopAutomaticCapture);
        Assert.True(state.AutomaticCaptureEnabled);
        Assert.Equal(LongCaptureTrigger.FirstScroll, vertical.CaptureNow);
    }
}
