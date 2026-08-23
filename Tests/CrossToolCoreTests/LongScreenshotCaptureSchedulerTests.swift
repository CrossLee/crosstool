import CrossToolCore
import Testing

@Test
func longScreenshotSchedulerCapturesFirstScrollImmediately() {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 10)

    let actions = scheduler.handleScroll(
        sessionID: session,
        deltaX: 0.1,
        deltaY: 3,
        at: 10.01
    )

    let request = captureRequest(from: actions)
    #expect(request?.reason == .leading)
    #expect(request?.sessionID == session)
    #expect(scheduler.nextDeadline == nil)
}

@Test
func longScreenshotSchedulerThrottlesContinuousScrollInsteadOfDebouncingIt() throws {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)
    let leading = try #require(captureRequest(from: scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 2,
        at: 0
    )))
    #expect(scheduler.captureDidFinish(leading, at: 0.01).isEmpty)

    #expect(scheduler.handleScroll(
        sessionID: session,
        deltaX: 0.2,
        deltaY: 2,
        at: 0.05
    ).isEmpty)
    #expect(scheduler.handleScroll(
        sessionID: session,
        deltaX: 0.1,
        deltaY: 2,
        at: 0.13
    ).isEmpty)
    #expect(abs(try #require(scheduler.nextDeadline) - 0.14) < 0.000_001)
    #expect(scheduler.advance(sessionID: session, to: 0.139).isEmpty)

    let periodic = try #require(captureRequest(from: scheduler.advance(
        sessionID: session,
        to: 0.14
    )))
    #expect(periodic.reason == .periodic)

    #expect(scheduler.captureDidFinish(periodic, at: 0.15).isEmpty)
    #expect(scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 1,
        at: 0.20
    ).isEmpty)
    #expect(scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 1,
        at: 0.27
    ).isEmpty)

    let secondPeriodic = try #require(captureRequest(from: scheduler.advance(
        sessionID: session,
        to: 0.28
    )))
    #expect(secondPeriodic.reason == .periodic)
}

@Test
func longScreenshotSchedulerCoalescesBusyScrollsIntoLatestPendingCapture() throws {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)
    let leading = try #require(captureRequest(from: scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 1,
        at: 0
    )))

    for timestamp in [0.05, 0.10, 0.15] {
        #expect(scheduler.handleScroll(
            sessionID: session,
            deltaX: 0.1,
            deltaY: 1,
            at: timestamp
        ).isEmpty)
    }
    #expect(scheduler.nextDeadline == nil)

    let pending = try #require(captureRequest(from: scheduler.captureDidFinish(
        leading,
        at: 0.16
    )))
    #expect(pending.reason == .periodic)
    #expect(pending.sequence == leading.sequence + 1)
}

@Test
func longScreenshotSchedulerAlwaysRequestsSettledTrailingFrame() throws {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)
    let leading = try #require(captureRequest(from: scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 1,
        at: 0
    )))
    #expect(scheduler.captureDidFinish(leading, at: 0.01).isEmpty)
    #expect(abs(try #require(scheduler.nextDeadline) - 0.22) < 0.000_001)
    #expect(scheduler.advance(sessionID: session, to: 0.219).isEmpty)

    let trailing = try #require(captureRequest(from: scheduler.advance(
        sessionID: session,
        to: 0.22
    )))
    #expect(trailing.reason == .trailing)
}

@Test
func longScreenshotSchedulerRequestsOverdueTrailingFrameAfterBusyCapture() throws {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)
    let leading = try #require(captureRequest(from: scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 1,
        at: 0
    )))

    #expect(scheduler.advance(sessionID: session, to: 0.22).isEmpty)
    let trailing = try #require(captureRequest(from: scheduler.captureDidFinish(
        leading,
        at: 0.30
    )))
    #expect(trailing.reason == .trailing)
}

@Test
func longScreenshotSchedulerTerminatesHorizontalDominantGesture() {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)

    let actions = scheduler.handleScroll(
        sessionID: session,
        deltaX: 3,
        deltaY: 1,
        at: 0.1
    )

    #expect(actions == [.terminate(sessionID: session, reason: .horizontalScroll)])
    #expect(scheduler.state == .terminated(session, reason: .horizontalScroll))
    #expect(scheduler.nextDeadline == nil)
    #expect(scheduler.handleScroll(
        sessionID: session,
        deltaX: 0,
        deltaY: 4,
        at: 0.2
    ).isEmpty)
}

@Test
func longScreenshotSchedulerAllowsSmallHorizontalDrift() {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)

    let request = captureRequest(from: scheduler.handleScroll(
        sessionID: session,
        deltaX: 0.8,
        deltaY: 2,
        at: 0.01
    ))

    #expect(request?.reason == .leading)
}

@Test
func longScreenshotSchedulerIgnoresTinyDiagonalMomentumTail() {
    var scheduler = LongScreenshotCaptureScheduler()
    let session = scheduler.beginSession(at: 0)

    let actions = scheduler.handleScroll(
        sessionID: session,
        deltaX: 0.06,
        deltaY: 0.04,
        at: 0.1
    )

    #expect(actions.isEmpty)
    #expect(scheduler.state == .active(session))
}

@Test
func longScreenshotSchedulerIgnoresStaleCompletionAfterResetAndNewSession() throws {
    var scheduler = LongScreenshotCaptureScheduler()
    let firstSession = scheduler.beginSession(at: 0)
    let staleRequest = try #require(captureRequest(from: scheduler.handleScroll(
        sessionID: firstSession,
        deltaX: 0,
        deltaY: 1,
        at: 0
    )))

    scheduler.reset()
    #expect(scheduler.state == .idle)
    let secondSession = scheduler.beginSession(at: 1)
    #expect(firstSession != secondSession)
    #expect(scheduler.captureDidFinish(staleRequest, at: 1.1).isEmpty)
    #expect(scheduler.handleScroll(
        sessionID: firstSession,
        deltaX: 0,
        deltaY: 2,
        at: 1.2
    ).isEmpty)

    scheduler.cancelSession(firstSession)
    #expect(scheduler.state == .active(secondSession))
    scheduler.cancelSession(secondSession)
    #expect(scheduler.state == .cancelled(secondSession))
    #expect(scheduler.nextDeadline == nil)
}

private func captureRequest(
    from actions: [LongScreenshotCaptureScheduler.Action]
) -> LongScreenshotCaptureScheduler.CaptureRequest? {
    guard actions.count == 1,
          case let .requestCapture(request) = actions[0] else {
        return nil
    }
    return request
}
