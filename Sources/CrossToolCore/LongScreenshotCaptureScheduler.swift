import Foundation

/// A deterministic state machine that decides when a scrolling-screenshot
/// coordinator should capture frames.
///
/// The scheduler owns no timers and performs no capture work. Its caller feeds
/// scroll events and monotonic timestamps into the state machine, executes any
/// returned capture request, and calls ``captureDidFinish(_:at:)`` when that
/// request completes. While a request is in flight, additional scroll events
/// are coalesced into one latest pending capture.
public struct LongScreenshotCaptureScheduler: Sendable {
    public struct Configuration: Equatable, Sendable {
        /// Minimum time between captures during one continuous scroll burst.
        public var throttleInterval: TimeInterval

        /// Time after the final scroll event at which a settled trailing frame
        /// must be captured.
        public var trailingInterval: TimeInterval

        /// Deltas below this magnitude are treated as trackpad noise.
        public var minimumScrollDelta: Double

        /// A horizontal delta must be materially large before a vertical
        /// session is terminated. Tiny diagonal momentum tails are ignored.
        public var minimumHorizontalTerminationDelta: Double

        /// Horizontal motion terminates the session when it is larger than the
        /// vertical motion by this factor.
        public var horizontalDominanceRatio: Double

        public init(
            throttleInterval: TimeInterval = 0.14,
            trailingInterval: TimeInterval = 0.22,
            minimumScrollDelta: Double = 0.05,
            minimumHorizontalTerminationDelta: Double = 3,
            horizontalDominanceRatio: Double = 1.2
        ) {
            self.throttleInterval = max(0, throttleInterval)
            self.trailingInterval = max(0, trailingInterval)
            self.minimumScrollDelta = max(0, minimumScrollDelta)
            self.minimumHorizontalTerminationDelta = max(
                self.minimumScrollDelta,
                minimumHorizontalTerminationDelta
            )
            self.horizontalDominanceRatio = max(1, horizontalDominanceRatio)
        }
    }

    public struct SessionID: Hashable, Sendable {
        public let rawValue: UInt64

        public init(rawValue: UInt64) {
            self.rawValue = rawValue
        }
    }

    public enum CaptureReason: Equatable, Sendable {
        /// First capture requested for a new scroll burst.
        case leading
        /// Throttled capture requested while scrolling continues.
        case periodic
        /// Settled capture requested after scrolling stops.
        case trailing
    }

    public struct CaptureRequest: Equatable, Sendable {
        public let sessionID: SessionID
        public let sequence: UInt64
        public let reason: CaptureReason

        public init(sessionID: SessionID, sequence: UInt64, reason: CaptureReason) {
            self.sessionID = sessionID
            self.sequence = sequence
            self.reason = reason
        }
    }

    public enum TerminationReason: Equatable, Sendable {
        /// A predominantly horizontal gesture cannot be stitched into the
        /// current vertical long screenshot safely.
        case horizontalScroll
    }

    public enum Action: Equatable, Sendable {
        case requestCapture(CaptureRequest)
        case terminate(sessionID: SessionID, reason: TerminationReason)
    }

    public enum State: Equatable, Sendable {
        case idle
        case active(SessionID)
        case cancelled(SessionID)
        case terminated(SessionID, reason: TerminationReason)
    }

    public private(set) var state: State = .idle

    /// The next monotonic timestamp at which the caller should invoke
    /// ``advance(sessionID:to:)``. It is `nil` while a capture is in flight;
    /// completion of that capture will return any overdue action immediately.
    public var nextDeadline: TimeInterval? {
        guard case .active = state, inFlightRequest == nil else { return nil }

        var deadlines = [TimeInterval]()
        if pendingCapture == .periodic, let lastCaptureRequestedAt {
            deadlines.append(lastCaptureRequestedAt + configuration.throttleInterval)
        } else if pendingCapture == .leading {
            deadlines.append(lastProcessedAt)
        }
        if trailingOutstanding, let trailingDeadline {
            deadlines.append(trailingDeadline)
        }
        return deadlines.min()
    }

    private enum PendingCapture: Equatable, Sendable {
        case leading
        case periodic
    }

    private let configuration: Configuration
    private var nextSessionValue: UInt64 = 0
    private var nextCaptureSequence: UInt64 = 0
    private var lastProcessedAt: TimeInterval = 0
    private var lastCaptureRequestedAt: TimeInterval?
    private var trailingDeadline: TimeInterval?
    private var trailingOutstanding = false
    private var burstHasCapture = false
    private var pendingCapture: PendingCapture?
    private var inFlightRequest: CaptureRequest?

    public init(configuration: Configuration = Configuration()) {
        self.configuration = configuration
    }

    /// Starts a fresh session. Any callbacks carrying an older session or
    /// capture request are ignored after this call.
    @discardableResult
    public mutating func beginSession(at timestamp: TimeInterval) -> SessionID {
        clearSessionData()
        nextSessionValue &+= 1
        if nextSessionValue == 0 {
            nextSessionValue = 1
        }
        let sessionID = SessionID(rawValue: nextSessionValue)
        state = .active(sessionID)
        lastProcessedAt = timestamp
        return sessionID
    }

    /// Returns the scheduler to its reusable idle state without reusing the
    /// previous session identifier.
    public mutating func reset() {
        clearSessionData()
        state = .idle
    }

    public mutating func cancelSession(_ sessionID: SessionID) {
        guard case .active(sessionID) = state else { return }
        clearSessionData()
        state = .cancelled(sessionID)
    }

    /// Feeds one scroll event into the active session.
    ///
    /// Vertical events produce a leading or throttled capture. Predominantly
    /// horizontal events terminate the vertical stitching session explicitly.
    public mutating func handleScroll(
        sessionID: SessionID,
        deltaX: Double,
        deltaY: Double,
        at timestamp: TimeInterval
    ) -> [Action] {
        guard case .active(sessionID) = state else { return [] }
        let now = normalizedTimestamp(timestamp)
        let horizontal = abs(deltaX)
        let vertical = abs(deltaY)

        if horizontal >= configuration.minimumHorizontalTerminationDelta,
           horizontal > vertical * configuration.horizontalDominanceRatio {
            clearSessionData()
            state = .terminated(sessionID, reason: .horizontalScroll)
            return [.terminate(sessionID: sessionID, reason: .horizontalScroll)]
        }

        guard vertical >= configuration.minimumScrollDelta else { return [] }

        trailingDeadline = now + configuration.trailingInterval
        trailingOutstanding = true

        if !burstHasCapture {
            pendingCapture = .leading
        } else if pendingCapture != .leading {
            pendingCapture = .periodic
        }

        guard inFlightRequest == nil else { return [] }

        if pendingCapture == .leading {
            return [makeCaptureRequest(reason: .leading, at: now)]
        }

        if periodicCaptureIsDue(at: now) {
            return [makeCaptureRequest(reason: .periodic, at: now)]
        }

        return []
    }

    /// Advances timer-driven work to `timestamp`.
    public mutating func advance(sessionID: SessionID, to timestamp: TimeInterval) -> [Action] {
        guard case .active(sessionID) = state else { return [] }
        let now = normalizedTimestamp(timestamp)
        guard inFlightRequest == nil else { return [] }
        return dueActions(at: now)
    }

    /// Marks exactly one scheduler-issued capture as complete. Duplicate and
    /// stale completions are ignored.
    public mutating func captureDidFinish(
        _ request: CaptureRequest,
        at timestamp: TimeInterval
    ) -> [Action] {
        guard case .active(request.sessionID) = state,
              inFlightRequest == request else {
            return []
        }

        let now = normalizedTimestamp(timestamp)
        inFlightRequest = nil
        return dueActions(at: now)
    }

    private mutating func dueActions(at timestamp: TimeInterval) -> [Action] {
        if trailingOutstanding,
           let trailingDeadline,
           timestamp >= trailingDeadline {
            return [makeCaptureRequest(reason: .trailing, at: timestamp)]
        }

        if pendingCapture == .leading {
            return [makeCaptureRequest(reason: .leading, at: timestamp)]
        }

        if pendingCapture == .periodic, periodicCaptureIsDue(at: timestamp) {
            return [makeCaptureRequest(reason: .periodic, at: timestamp)]
        }

        return []
    }

    private func periodicCaptureIsDue(at timestamp: TimeInterval) -> Bool {
        guard let lastCaptureRequestedAt else { return true }
        return timestamp >= lastCaptureRequestedAt + configuration.throttleInterval
    }

    private mutating func makeCaptureRequest(
        reason: CaptureReason,
        at timestamp: TimeInterval
    ) -> Action {
        nextCaptureSequence &+= 1
        if nextCaptureSequence == 0 {
            nextCaptureSequence = 1
        }
        guard case let .active(sessionID) = state else {
            preconditionFailure("Capture requests require an active long-screenshot session")
        }

        let request = CaptureRequest(
            sessionID: sessionID,
            sequence: nextCaptureSequence,
            reason: reason
        )
        inFlightRequest = request
        lastCaptureRequestedAt = timestamp
        pendingCapture = nil

        switch reason {
        case .leading, .periodic:
            burstHasCapture = true
        case .trailing:
            trailingOutstanding = false
            trailingDeadline = nil
            burstHasCapture = false
        }

        return .requestCapture(request)
    }

    private mutating func normalizedTimestamp(_ timestamp: TimeInterval) -> TimeInterval {
        lastProcessedAt = max(lastProcessedAt, timestamp)
        return lastProcessedAt
    }

    private mutating func clearSessionData() {
        lastCaptureRequestedAt = nil
        trailingDeadline = nil
        trailingOutstanding = false
        burstHasCapture = false
        pendingCapture = nil
        inFlightRequest = nil
    }
}
