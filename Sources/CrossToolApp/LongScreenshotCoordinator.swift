import AppKit
import CoreGraphics
import CrossToolCore
import Foundation

enum LongScreenshotCoordinatorError: LocalizedError, Equatable {
    case cancelled
    case alreadyRunning

    var errorDescription: String? {
        switch self {
        case .cancelled:
            return "已取消长截图"
        case .alreadyRunning:
            return "已有一项长截图正在进行"
        }
    }
}

/// Runs one fixed-region scrolling capture session.
///
/// Scroll events are observed without consuming them, so the target
/// application remains in control of scrolling. The coordinator samples while
/// the gesture is still moving and once more after it settles; captures are
/// strictly serialized and additional events are coalesced by the Core state
/// machine.
@MainActor
final class LongScreenshotCoordinator {
    typealias CaptureFrame = @MainActor () async throws -> CGImage

    private enum CaptureTrigger {
        case scheduled(LongScreenshotCaptureScheduler.CaptureRequest)
        case manual
        case finishing

        var label: String {
            switch self {
            case let .scheduled(request):
                switch request.reason {
                case .leading: return "已采集滚动起始帧"
                case .periodic: return "滚动中已连续拼接"
                case .trailing: return "已补齐滚动末帧"
                }
            case .manual:
                return "已手动补采并拼接"
            case .finishing:
                return "已补齐完成前的最后一帧"
            }
        }
    }

    private let limits: LongScreenshotLimits
    private var scheduler = LongScreenshotCaptureScheduler()
    private var schedulerSessionID: LongScreenshotCaptureScheduler.SessionID?
    private var accumulator: LongScreenshotAccumulator?
    private var captureFrame: CaptureFrame?
    private var completion: CheckedContinuation<CGImage, Error>?
    private var overlayController: LongScreenshotSessionOverlayController?
    private var overlayModel: LongScreenshotSessionOverlayModel?
    private var globalScrollMonitor: Any?
    private var localScrollMonitor: Any?
    private var deadlineTask: Task<Void, Never>?
    private var captureTask: Task<Void, Never>?
    private var activeSessionID: UUID?
    private var activeSelection: ScreenRegionSelection?
    private var isPreparing = false
    private var pendingScheduledRequest: LongScreenshotCaptureScheduler.CaptureRequest?
    private var pendingManualCapture = false
    private var finishRequested = false
    private var needsFinishingCapture = false
    private var preferredDirection: LongScreenshotScrollDirection?
    private var duplicateFrameCount = 0
    private var automaticCaptureGeneration: UInt64 = 0

    var isRunning: Bool {
        isPreparing || activeSessionID != nil
    }

    init(limits: LongScreenshotLimits = LongScreenshotLimits()) {
        self.limits = limits
    }

    func capture(
        selection: ScreenRegionSelection,
        captureFrame: @escaping CaptureFrame
    ) async throws -> CGImage {
        guard !isRunning else {
            throw LongScreenshotCoordinatorError.alreadyRunning
        }

        isPreparing = true
        let sessionID = UUID()
        let model = LongScreenshotSessionOverlayModel(
            frameCount: 0,
            outputHeight: 0,
            isCapturing: true,
            isWaitingForScroll: true,
            statusMessage: "正在锁定首屏；只框选可滚动内容，不要包含标题栏或固定区域。"
        )
        let overlay = LongScreenshotSessionOverlayController(
            selection: selection,
            model: model
        )
        activeSessionID = sessionID
        activeSelection = selection
        self.captureFrame = captureFrame
        overlayModel = model
        overlayController = overlay
        wireOverlayActions(model)

        // Install monitors before the first asynchronous capture. A user who
        // starts scrolling immediately after mouse-up no longer loses that
        // gesture; it becomes a pending scheduled capture.
        schedulerSessionID = scheduler.beginSession(at: monotonicNow())
        installScrollMonitors(for: selection, sessionID: sessionID)
        overlay.show()
        overlay.setSelectionInteractionBlocked(true)

        do {
            let firstFrame = try await captureFrame()
            try Task.checkCancellation()
            guard activeSessionID == sessionID else {
                throw CancellationError()
            }
            let newAccumulator = try LongScreenshotAccumulator(
                firstFrame: firstFrame,
                limits: limits
            )
            accumulator = newAccumulator
            isPreparing = false
            overlay.setSelectionInteractionBlocked(false)
            model.frameCount = 1
            model.outputHeight = newAccumulator.outputHeight
            model.isCapturing = false
            model.isWaitingForScroll = false
            model.statusMessage = "首屏已采集。请在蓝框内平稳滚动；滚动过程中会持续拼接。"
            scheduleDeadlineTimer()
            startPendingWorkIfNeeded()
        } catch {
            isPreparing = false
            resolve(.failure(error))
            throw error
        }

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                completion = continuation
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                guard let self, self.activeSessionID == sessionID else { return }
                self.resolve(.failure(CancellationError()))
            }
        }
    }

    private func wireOverlayActions(_ model: LongScreenshotSessionOverlayModel) {
        model.onCaptureNext = { [weak self] in
            self?.requestManualCapture()
        }
        model.onFinish = { [weak self] in
            self?.requestFinish()
        }
        model.onCancel = { [weak self] in
            self?.cancel()
        }
    }

    private func installScrollMonitors(
        for selection: ScreenRegionSelection,
        sessionID: UUID
    ) {
        removeScrollMonitors()
        globalScrollMonitor = NSEvent.addGlobalMonitorForEvents(
            matching: .scrollWheel
        ) { [weak self] event in
            let snapshot = ScrollEventSnapshot(event: event)
            Task { @MainActor [weak self] in
                self?.handleScroll(
                    snapshot,
                    mouseLocation: NSEvent.mouseLocation,
                    selection: selection,
                    sessionID: sessionID
                )
            }
        }
        localScrollMonitor = NSEvent.addLocalMonitorForEvents(
            matching: .scrollWheel
        ) { [weak self] event in
            let snapshot = ScrollEventSnapshot(event: event)
            Task { @MainActor [weak self] in
                self?.handleScroll(
                    snapshot,
                    mouseLocation: NSEvent.mouseLocation,
                    selection: selection,
                    sessionID: sessionID
                )
            }
            return event
        }

        if globalScrollMonitor == nil && localScrollMonitor == nil {
            overlayModel?.errorMessage = "系统无法监听滚轮；请滚动一小段后点击“补采”。"
        }
    }

    private struct ScrollEventSnapshot: Sendable {
        let deltaX: Double
        let deltaY: Double

        init(event: NSEvent) {
            deltaX = Double(event.scrollingDeltaX)
            deltaY = Double(event.scrollingDeltaY)
        }
    }

    private func handleScroll(
        _ event: ScrollEventSnapshot,
        mouseLocation: CGPoint,
        selection: ScreenRegionSelection,
        sessionID: UUID
    ) {
        guard activeSessionID == sessionID,
              !isPreparing,
              !finishRequested,
              selection.selectionRectInScreen.contains(mouseLocation),
              let schedulerSessionID else {
            return
        }

        // AppKit already delivers scrollingDelta in the user's configured
        // scrolling semantics. Do not invert it a second time for natural
        // scrolling devices.
        let normalizedY = event.deltaY
        if abs(normalizedY) >= 0.05 {
            let eventDirection: LongScreenshotScrollDirection = normalizedY < 0
                ? .downward
                : .upward
            if let preferredDirection, preferredDirection != eventDirection {
                let directionName = preferredDirection == .downward ? "向下" : "向上"
                overlayModel?.errorMessage = "已锁定\(directionName)拼接；反向滚动不会加入结果。"
            } else if preferredDirection == nil {
                preferredDirection = eventDirection
            }
        }

        let actions = scheduler.handleScroll(
            sessionID: schedulerSessionID,
            deltaX: event.deltaX,
            deltaY: normalizedY,
            at: monotonicNow()
        )
        handleSchedulerActions(actions)
        scheduleDeadlineTimer()
    }

    private func handleSchedulerActions(
        _ actions: [LongScreenshotCaptureScheduler.Action]
    ) {
        for action in actions {
            switch action {
            case let .requestCapture(request):
                startCapture(trigger: .scheduled(request))
            case .terminate(_, .horizontalScroll):
                automaticCaptureGeneration &+= 1
                pendingScheduledRequest = nil
                overlayModel?.isWaitingForScroll = false
                overlayModel?.errorMessage = "检测到明显横向滚动，已停止自动采集。当前已拼接内容仍可完成或手动补采。"
                overlayModel?.statusMessage = "长截图只拼接纵向内容。"
                deadlineTask?.cancel()
                deadlineTask = nil
            }
        }
    }

    private func scheduleDeadlineTimer() {
        deadlineTask?.cancel()
        deadlineTask = nil
        guard let deadline = scheduler.nextDeadline,
              let schedulerSessionID,
              activeSessionID != nil,
              !finishRequested else {
            return
        }

        overlayModel?.isWaitingForScroll = true
        let delay = max(0, deadline - monotonicNow())
        deadlineTask = Task { @MainActor [weak self] in
            do {
                try await Task.sleep(for: .seconds(delay))
            } catch {
                return
            }
            guard let self, self.activeSessionID != nil else { return }
            let actions = self.scheduler.advance(
                sessionID: schedulerSessionID,
                to: self.monotonicNow()
            )
            self.handleSchedulerActions(actions)
            self.scheduleDeadlineTimer()
        }
    }

    private func requestManualCapture() {
        guard activeSessionID != nil, !finishRequested else { return }
        if captureTask != nil || isPreparing {
            pendingManualCapture = true
            overlayModel?.isWaitingForScroll = true
            return
        }
        startCapture(trigger: .manual)
    }

    private func startCapture(trigger: CaptureTrigger) {
        if finishRequested {
            guard case .finishing = trigger else { return }
        }
        guard captureTask == nil,
              !isPreparing,
              activeSessionID != nil,
              let captureFrame else {
            if case let .scheduled(request) = trigger {
                pendingScheduledRequest = request
                overlayModel?.isWaitingForScroll = true
            }
            return
        }

        if case let .scheduled(request) = trigger {
            pendingScheduledRequest = nil
            guard request.sessionID == schedulerSessionID else { return }
        }

        overlayModel?.isCapturing = true
        overlayModel?.isWaitingForScroll = true
        overlayModel?.errorMessage = nil
        overlayModel?.statusMessage = "正在采集并匹配重叠区域…"

        let generation = automaticCaptureGeneration
        captureTask = Task { @MainActor [weak self] in
            guard let self else { return }
            await self.performCapture(
                trigger: trigger,
                captureFrame: captureFrame,
                generation: generation
            )
        }
    }

    private func performCapture(
        trigger: CaptureTrigger,
        captureFrame: CaptureFrame,
        generation: UInt64
    ) async {
        guard let sessionID = activeSessionID,
              let accumulator else {
            captureTask = nil
            return
        }

        do {
            let frame = try await captureFrame()
            guard activeSessionID == sessionID else { return }
            if case .scheduled = trigger,
               generation != automaticCaptureGeneration {
                throw CancellationError()
            }
            let result = try accumulator.append(
                frame,
                preferredDirection: preferredDirection
            )
            preferredDirection = result.direction
            duplicateFrameCount = 0
            overlayModel?.canFinishCurrentOutput = false
            overlayModel?.frameCount = accumulator.frameCount
            overlayModel?.outputHeight = accumulator.outputHeight
            overlayModel?.errorMessage = nil
            overlayModel?.statusMessage = "\(trigger.label)：重叠 \(result.overlapPixels) px，置信度 \(Int((result.confidence * 100).rounded()))%。"
        } catch is CancellationError {
            guard activeSessionID == sessionID else { return }
            if case .scheduled = trigger {
                overlayModel?.statusMessage = "横向滚动后的迟到帧已丢弃。"
            } else if case .finishing = trigger {
                finishRequested = false
                needsFinishingCapture = false
                overlayModel?.canFinishCurrentOutput = true
                resumeAutomaticSamplingAfterFailedFinish()
                overlayModel?.errorMessage = "最后一帧采集已取消；当前已拼接内容仍保留。"
            }
        } catch LongScreenshotStitchError.duplicateFrame {
            guard activeSessionID == sessionID else { return }
            duplicateFrameCount += 1
            overlayModel?.errorMessage = nil
            overlayModel?.statusMessage = duplicateFrameCount >= 2
                ? "连续两次没有新内容，可能已到达末尾；可点击“完成”。"
                : "没有检测到新内容。请继续平稳滚动。"
        } catch LongScreenshotStitchError.directionChanged {
            guard activeSessionID == sessionID else { return }
            if case .finishing = trigger {
                finishRequested = false
                needsFinishingCapture = false
                overlayModel?.canFinishCurrentOutput = true
                resumeAutomaticSamplingAfterFailedFinish()
                overlayModel?.errorMessage = "最后一帧方向与当前结果不一致；可保留当前内容完成，或沿原方向滚动后重试。"
            } else {
                overlayModel?.errorMessage = "滚动方向已改变，这一帧没有加入；请沿原方向继续。"
            }
        } catch {
            guard activeSessionID == sessionID else { return }
            overlayModel?.errorMessage = error.localizedDescription
            if case .finishing = trigger {
                finishRequested = false
                needsFinishingCapture = false
                overlayModel?.canFinishCurrentOutput = true
                resumeAutomaticSamplingAfterFailedFinish()
                overlayModel?.statusMessage = "最后一帧未能加入；可点击“保留当前并完成”，或继续滚动后重试。"
            } else {
                overlayModel?.statusMessage = "这一帧没有加入；少滚动一些后可继续。"
            }
        }

        guard activeSessionID == sessionID else { return }
        captureTask = nil
        overlayModel?.isCapturing = false

        if case let .scheduled(request) = trigger {
            let actions = scheduler.captureDidFinish(request, at: monotonicNow())
            if !finishRequested {
                handleSchedulerActions(actions)
            }
        }
        if pendingManualCapture, captureTask == nil {
            pendingManualCapture = false
            startCapture(trigger: .manual)
        }
        if case .finishing = trigger, finishRequested {
            needsFinishingCapture = false
        }
        scheduleDeadlineTimer()
        startPendingWorkIfNeeded()
    }

    private func startPendingWorkIfNeeded() {
        guard captureTask == nil, !isPreparing else { return }
        if finishRequested {
            if needsFinishingCapture {
                needsFinishingCapture = false
                startCapture(trigger: .finishing)
            } else {
                finishNow()
            }
            return
        }
        if let pendingScheduledRequest, !finishRequested {
            self.pendingScheduledRequest = nil
            startCapture(trigger: .scheduled(pendingScheduledRequest))
            return
        }
        if pendingManualCapture {
            pendingManualCapture = false
            startCapture(trigger: .manual)
            return
        }
        overlayModel?.isWaitingForScroll = scheduler.nextDeadline != nil
    }

    private func requestFinish() {
        guard activeSessionID != nil else { return }
        if overlayModel?.canFinishCurrentOutput == true {
            finishNow()
            return
        }
        finishRequested = true
        needsFinishingCapture = true
        if let schedulerSessionID {
            scheduler.cancelSession(schedulerSessionID)
            self.schedulerSessionID = nil
        }
        pendingManualCapture = false
        pendingScheduledRequest = nil
        deadlineTask?.cancel()
        deadlineTask = nil
        if captureTask != nil || isPreparing {
            overlayModel?.statusMessage = "正在完成最后一帧，随后生成长截图…"
            overlayModel?.isWaitingForScroll = true
            return
        }
        startPendingWorkIfNeeded()
    }

    private func finishNow() {
        guard let accumulator else { return }
        do {
            resolve(.success(try accumulator.render()))
        } catch {
            finishRequested = false
            resumeAutomaticSamplingAfterFailedFinish()
            overlayModel?.errorMessage = error.localizedDescription
            overlayModel?.statusMessage = "生成失败，当前内容仍保留，可重试或取消。"
        }
    }

    private func resumeAutomaticSamplingAfterFailedFinish() {
        guard activeSessionID != nil, schedulerSessionID == nil else { return }
        schedulerSessionID = scheduler.beginSession(at: monotonicNow())
    }

    private func cancel() {
        guard isRunning else { return }
        resolve(.failure(LongScreenshotCoordinatorError.cancelled))
    }

    private func resolve(_ result: Result<CGImage, Error>) {
        let continuation = completion
        completion = nil
        deadlineTask?.cancel()
        deadlineTask = nil
        captureTask?.cancel()
        captureTask = nil
        if let schedulerSessionID {
            scheduler.cancelSession(schedulerSessionID)
        }
        scheduler.reset()
        schedulerSessionID = nil
        removeScrollMonitors()
        overlayController?.close()
        overlayController = nil
        overlayModel = nil
        activeSessionID = nil
        activeSelection = nil
        accumulator = nil
        captureFrame = nil
        isPreparing = false
        pendingManualCapture = false
        pendingScheduledRequest = nil
        finishRequested = false
        needsFinishingCapture = false
        preferredDirection = nil
        duplicateFrameCount = 0
        automaticCaptureGeneration = 0
        continuation?.resume(with: result)
    }

    private func removeScrollMonitors() {
        if let globalScrollMonitor {
            NSEvent.removeMonitor(globalScrollMonitor)
            self.globalScrollMonitor = nil
        }
        if let localScrollMonitor {
            NSEvent.removeMonitor(localScrollMonitor)
            self.localScrollMonitor = nil
        }
    }

    private func monotonicNow() -> TimeInterval {
        ProcessInfo.processInfo.systemUptime
    }
}
