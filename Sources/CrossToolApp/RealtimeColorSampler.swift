import AppKit
import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import OSLog
@preconcurrency import ScreenCaptureKit

/// A low-bandwidth, real-time screen colour sampler.
///
/// Only a 512 x 512 native-pixel tile around the pointer is streamed. Pointer
/// movement inside that tile reads the latest BGRA frame directly; the stream
/// is moved only when the pointer approaches the tile edge. Crosio itself is
/// excluded by the display filter, so the loupe never samples its own pixels.
@MainActor
final class RealtimeScreenColorSampler: NSObject, ScreenColorSampling {
    private var activeSession: RealtimeColorSamplingSession?

    override init() {
        super.init()
    }

    func sampleColor() async -> NSColor? {
        guard activeSession == nil else { return nil }

        // NSColorSampler remains a useful compatibility path when Screen
        // Recording permission was declined or ScreenCaptureKit cannot start.
        guard CGPreflightScreenCaptureAccess() else {
            return await NSColorSampler().sample()
        }

        let session = RealtimeColorSamplingSession()
        activeSession = session
        defer { activeSession = nil }

        do {
            return try await session.run()
        } catch is CancellationError {
            return nil
        } catch {
            return await NSColorSampler().sample()
        }
    }
}

private enum RealtimeColorSamplerError: Error {
    case displayUnavailable
    case cannotAddStreamOutput
    case streamStopped(String)
}

@MainActor
private final class RealtimeColorSamplingSession {
    private static let tileSide = 512
    // Keep enough pixels for the 11 x 11 loupe while avoiding frequent
    // configuration changes during ordinary pointer movement.
    private static let recenterMargin = 32
    private static let logger = Logger(
        subsystem: "com.cross.crosstool",
        category: "color-sampler"
    )

    private var overlayWindows: [ColorSamplerOverlayWindow] = []
    private var previewPanel: NSPanel?
    private var previewView: ColorSamplerPreviewView?
    private var activeStream: ActiveColorStream?
    private var resultContinuation: CheckedContinuation<NSColor?, Error>?
    private var pendingPoint: CGPoint?
    private var streamUpdateTask: Task<Void, Never>?
    private var isFinished = false
    private var pendingConfirmationPoint: CGPoint?
    private var hasLoggedFirstPreview = false
    private var lastPointerLocation = NSEvent.mouseLocation
    private var confirmationReadyAt: TimeInterval = 0
    private var pendingPointerLocation: CGPoint?
    private var pointerUpdateTask: Task<Void, Never>?

    func run() async throws -> NSColor? {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                resultContinuation = continuation
                installWindows()
                pendingPoint = lastPointerLocation
                startStreamUpdateLoopIfNeeded()
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.finish(with: nil)
            }
        }
    }

    private func installWindows() {
        // The mouse-down that starts sampling from Crosio can still be in
        // flight when the transparent overlay is ordered front. Ignore that
        // initiating click so opening the picker never confirms a colour.
        confirmationReadyAt = ProcessInfo.processInfo.systemUptime + 0.35

        let previewView = ColorSamplerPreviewView(
            frame: CGRect(x: 0, y: 0, width: 250, height: 124)
        )
        self.previewView = previewView

        let previewPanel = NSPanel(
            contentRect: previewView.bounds,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        previewPanel.contentView = previewView
        previewPanel.isOpaque = false
        previewPanel.backgroundColor = .clear
        previewPanel.hasShadow = true
        previewPanel.level = NSWindow.Level(
            rawValue: NSWindow.Level.screenSaver.rawValue + 1
        )
        previewPanel.collectionBehavior = [
            .canJoinAllSpaces,
            .fullScreenAuxiliary,
            .stationary,
            .ignoresCycle
        ]
        previewPanel.ignoresMouseEvents = true
        previewPanel.sharingType = .none
        self.previewPanel = previewPanel

        overlayWindows = NSScreen.screens.map { screen in
            let window = ColorSamplerOverlayWindow(screen: screen)
            window.eventView.onMove = { [weak self] point in
                self?.pointerMoved(to: point)
            }
            window.eventView.onConfirm = { [weak self] point in
                self?.confirm(at: point)
            }
            window.eventView.onCancel = { [weak self] in
                self?.finish(with: nil)
            }
            window.orderFrontRegardless()
            return window
        }

        if let currentWindow = overlayWindows.first(where: {
            $0.screen?.frame.contains(lastPointerLocation) == true
        }) {
            currentWindow.makeKey()
            currentWindow.makeFirstResponder(currentWindow.eventView)
        }
        positionPreviewPanel(near: lastPointerLocation)
        previewPanel.orderFrontRegardless()
    }

    private func pointerMoved(to point: CGPoint) {
        guard !isFinished, pendingConfirmationPoint == nil else { return }
        lastPointerLocation = point
        pendingPointerLocation = point
        startPointerUpdateLoopIfNeeded()
    }

    /// Coalesce high-frequency AppKit mouse events to the display cadence.
    /// This keeps the cursor-side panel responsive without performing layout,
    /// a 121-pixel loupe rebuild and stream-boundary checks hundreds of times
    /// per second on high-polling-rate mice.
    private func startPointerUpdateLoopIfNeeded() {
        guard pointerUpdateTask == nil else { return }
        pointerUpdateTask = Task { @MainActor [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: .milliseconds(16))
                } catch {
                    break
                }
                guard !self.isFinished,
                      let point = self.pendingPointerLocation else {
                    break
                }
                self.pendingPointerLocation = nil
                self.positionPreviewPanel(near: point)
                self.refreshPreview(at: point)
                if self.needsStreamMove(for: point) {
                    self.pendingPoint = point
                    self.startStreamUpdateLoopIfNeeded()
                }
            }
            self.pointerUpdateTask = nil
            if self.pendingPointerLocation != nil, !self.isFinished {
                self.startPointerUpdateLoopIfNeeded()
            }
        }
    }

    private func startStreamUpdateLoopIfNeeded() {
        guard streamUpdateTask == nil else { return }
        streamUpdateTask = Task { @MainActor [weak self] in
            guard let self else { return }
            while !Task.isCancelled, let point = self.pendingPoint {
                self.pendingPoint = nil
                // The point may have been queued against the previous tile
                // while an async configuration update was still in flight.
                // Re-check it against the latest geometry so stale requests do
                // not cause back-to-back ScreenCaptureKit reconfiguration.
                guard self.needsStreamMove(for: point) else { continue }
                do {
                    try await self.moveStream(to: point)
                } catch {
                    self.fail(with: error)
                    return
                }
            }
            self.streamUpdateTask = nil
            if self.pendingPoint != nil, !self.isFinished {
                self.startStreamUpdateLoopIfNeeded()
            }
        }
    }

    private func needsStreamMove(for point: CGPoint) -> Bool {
        guard let activeStream,
              let location = pixelLocation(for: point, in: activeStream.screenInfo),
              location.displayID == activeStream.screenInfo.displayID else {
            return true
        }

        let tile = activeStream.geometry.pixelRect
        let safeRect = tile.insetBy(
            dx: CGFloat(Self.recenterMargin),
            dy: CGFloat(Self.recenterMargin)
        )
        return !safeRect.contains(CGPoint(x: location.x, y: location.y))
    }

    private func moveStream(to point: CGPoint) async throws {
        guard !isFinished,
              let screen = NSScreen.screens.first(where: { $0.frame.contains(point) }),
              let displayID = screen.displayID else {
            throw RealtimeColorSamplerError.displayUnavailable
        }

        if var current = activeStream,
           current.screenInfo.displayID == displayID,
           let location = pixelLocation(for: point, in: current.screenInfo) {
            let geometry = tileGeometry(
                centeredAtX: location.x,
                y: location.y,
                screenInfo: current.screenInfo
            )
            guard geometry.pixelRect != current.geometry.pixelRect else { return }

            current.output.pauseFrames()
            let configuration = Self.configuration(for: geometry)
            do {
                try await current.stream.updateConfiguration(configuration)
                try Task.checkCancellation()
                guard !isFinished else {
                    current.output.pauseFrames()
                    try? await current.stream.stopCapture()
                    return
                }
                current.geometry = geometry
                current.latestFrame = nil
                activeStream = current
                await current.output.resumeFramesAfterDraining(geometry: geometry)
                refreshPreview(at: lastPointerLocation)
                // The pointer can move back across the old/new tile boundary
                // while updateConfiguration is suspended. Re-evaluate the
                // latest position against the geometry that just became active
                // so the preview never waits for another mouse event to recover.
                if needsStreamMove(for: lastPointerLocation) {
                    pendingPoint = lastPointerLocation
                }
                return
            } catch {
                current.output.pauseFrames()
                try? await current.stream.stopCapture()
                activeStream = nil
                throw error
            }
        }

        if let oldStream = activeStream {
            oldStream.output.pauseFrames()
            try? await oldStream.stream.stopCapture()
            activeStream = nil
        }

        let filter = try await ScreenCaptureKitScreenshotBackend.contentFilter(
            forDisplayID: displayID,
            excludingCurrentApplication: true
        )
        try Task.checkCancellation()
        guard !isFinished else { return }
        let scale = CGFloat(max(1, filter.pointPixelScale))
        let pixelWidth = max(1, Int((filter.contentRect.width * scale).rounded()))
        let pixelHeight = max(1, Int((filter.contentRect.height * scale).rounded()))
        let screenInfo = ColorSamplerScreenInfo(
            screen: screen,
            displayID: displayID,
            pointPixelScale: scale,
            pixelWidth: pixelWidth,
            pixelHeight: pixelHeight
        )
        guard let location = pixelLocation(for: point, in: screenInfo) else {
            throw RealtimeColorSamplerError.displayUnavailable
        }
        let geometry = tileGeometry(
            centeredAtX: location.x,
            y: location.y,
            screenInfo: screenInfo
        )
        let output = ColorSamplerStreamOutput(geometry: geometry) { [weak self] frame in
            self?.received(frame)
        } failureHandler: { [weak self] message in
            Task { @MainActor [weak self] in
                self?.fail(with: RealtimeColorSamplerError.streamStopped(message))
            }
        }
        let configuration = Self.configuration(for: geometry)
        let stream = SCStream(filter: filter, configuration: configuration, delegate: output)
        do {
            try stream.addStreamOutput(
                output,
                type: SCStreamOutputType.screen,
                sampleHandlerQueue: output.sampleQueue
            )
        } catch {
            throw RealtimeColorSamplerError.cannotAddStreamOutput
        }
        activeStream = ActiveColorStream(
            screenInfo: screenInfo,
            geometry: geometry,
            stream: stream,
            output: output,
            latestFrame: nil
        )
        do {
            try await stream.startCapture()
            try Task.checkCancellation()
            guard !isFinished else {
                output.pauseFrames()
                try? await stream.stopCapture()
                if activeStream?.stream === stream {
                    activeStream = nil
                }
                return
            }
            if needsStreamMove(for: lastPointerLocation) {
                pendingPoint = lastPointerLocation
            }
        } catch {
            output.pauseFrames()
            try? await stream.stopCapture()
            activeStream = nil
            throw error
        }
    }

    private static func configuration(
        for geometry: ColorSamplerTileGeometry
    ) -> SCStreamConfiguration {
        let configuration = SCStreamConfiguration()
        configuration.sourceRect = geometry.sourceRect
        configuration.width = geometry.pixelWidth
        configuration.height = geometry.pixelHeight
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 30)
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        // A small tile does not need a deep IOSurface pool. Two buffers are
        // enough for capture + consumption and reduce steady-state memory.
        configuration.queueDepth = 2
        configuration.scalesToFit = false
        configuration.preservesAspectRatio = true
        configuration.showsCursor = false
        configuration.shouldBeOpaque = true
        configuration.captureResolution = .best
        configuration.colorSpaceName = CGColorSpace.sRGB
        return configuration
    }

    private func received(_ frame: ColorSamplerFrame) {
        guard !isFinished,
              var current = activeStream,
              current.screenInfo.displayID == frame.displayID,
              current.geometry.generation == frame.generation else {
            return
        }
        current.latestFrame = frame
        activeStream = current
        refreshPreview(at: lastPointerLocation)
        if ProcessInfo.processInfo.systemUptime >= confirmationReadyAt,
           let pendingConfirmationPoint,
           let location = pixelLocation(
            for: pendingConfirmationPoint,
            in: current.screenInfo
           ),
           let pixel = frame.pixel(atX: location.x, y: location.y) {
            self.pendingConfirmationPoint = nil
            finish(with: pixel.color)
        }
    }

    private func refreshPreview(at point: CGPoint) {
        guard let activeStream,
              let frame = activeStream.latestFrame,
              frame.generation == activeStream.geometry.generation,
              let location = pixelLocation(for: point, in: activeStream.screenInfo),
              let model = frame.previewModel(atX: location.x, y: location.y) else {
            previewView?.model = .waiting
            return
        }
        previewView?.model = model
        if !hasLoggedFirstPreview, let center = model.center {
            hasLoggedFirstPreview = true
            let panelOrigin = previewPanel?.frame.origin ?? .zero
            Self.logger.info(
                "Realtime preview ready color=\(center.hexText, privacy: .public) mouse=(\(point.x, privacy: .public),\(point.y, privacy: .public)) panel=(\(panelOrigin.x, privacy: .public),\(panelOrigin.y, privacy: .public))"
            )
        }
    }

    private func confirm(at point: CGPoint) {
        guard ProcessInfo.processInfo.systemUptime >= confirmationReadyAt else {
            return
        }
        guard !isFinished,
              let activeStream,
              let frame = activeStream.latestFrame,
              frame.generation == activeStream.geometry.generation,
              let location = pixelLocation(for: point, in: activeStream.screenInfo),
              let pixel = frame.pixel(atX: location.x, y: location.y) else {
            // Consume the click but keep sampling until the first valid frame.
            pendingConfirmationPoint = point
            pendingPoint = point
            startStreamUpdateLoopIfNeeded()
            return
        }
        Self.logger.info(
            "Realtime color confirmed \(pixel.hexText, privacy: .public)"
        )
        finish(with: pixel.color)
    }

    private func positionPreviewPanel(near point: CGPoint) {
        guard let previewPanel else { return }
        let panelSize = previewPanel.frame.size
        let screen = NSScreen.screens.first(where: { $0.frame.contains(point) })
            ?? NSScreen.main
        guard let screen else { return }
        let bounds = screen.visibleFrame
        var origin = CGPoint(x: point.x + 20, y: point.y - panelSize.height - 18)
        if origin.x + panelSize.width > bounds.maxX {
            origin.x = point.x - panelSize.width - 20
        }
        if origin.y < bounds.minY {
            origin.y = point.y + 18
        }
        origin.x = min(max(origin.x, bounds.minX), bounds.maxX - panelSize.width)
        origin.y = min(max(origin.y, bounds.minY), bounds.maxY - panelSize.height)
        previewPanel.setFrameOrigin(origin)
    }

    private func finish(with color: NSColor?) {
        guard !isFinished else { return }
        if color == nil {
            Self.logger.info("Realtime color sampling cancelled")
        }
        tearDown()
        let continuation = resultContinuation
        resultContinuation = nil
        continuation?.resume(returning: color)
    }

    private func fail(with error: Error) {
        guard !isFinished else { return }
        tearDown()
        let continuation = resultContinuation
        resultContinuation = nil
        continuation?.resume(throwing: error)
    }

    private func tearDown() {
        isFinished = true
        pendingPoint = nil
        pendingPointerLocation = nil
        pendingConfirmationPoint = nil
        pointerUpdateTask?.cancel()
        pointerUpdateTask = nil
        streamUpdateTask?.cancel()
        streamUpdateTask = nil

        overlayWindows.forEach { $0.orderOut(nil) }
        overlayWindows.removeAll()
        previewPanel?.orderOut(nil)
        previewPanel = nil
        previewView = nil

        if let activeStream {
            activeStream.output.pauseFrames()
            let stream = activeStream.stream
            Task.detached(priority: .utility) {
                try? await stream.stopCapture()
            }
        }
        activeStream = nil
    }

    private func pixelLocation(
        for point: CGPoint,
        in screenInfo: ColorSamplerScreenInfo
    ) -> ColorSamplerPixelLocation? {
        let frame = screenInfo.screen.frame
        guard frame.contains(point) else { return nil }
        let localX = point.x - frame.minX
        let localTopY = frame.maxY - point.y
        let scale = screenInfo.pointPixelScale
        let x = min(
            screenInfo.pixelWidth - 1,
            max(0, Int(floor(localX * scale)))
        )
        let y = min(
            screenInfo.pixelHeight - 1,
            max(0, Int(floor(localTopY * scale)))
        )
        return ColorSamplerPixelLocation(displayID: screenInfo.displayID, x: x, y: y)
    }

    private func tileGeometry(
        centeredAtX x: Int,
        y: Int,
        screenInfo: ColorSamplerScreenInfo
    ) -> ColorSamplerTileGeometry {
        let width = min(Self.tileSide, screenInfo.pixelWidth)
        let height = min(Self.tileSide, screenInfo.pixelHeight)
        let maxX = max(0, screenInfo.pixelWidth - width)
        let maxY = max(0, screenInfo.pixelHeight - height)
        let originX = min(max(0, x - width / 2), maxX)
        let originY = min(max(0, y - height / 2), maxY)
        let scale = screenInfo.pointPixelScale
        let pixelRect = CGRect(x: originX, y: originY, width: width, height: height)
        return ColorSamplerTileGeometry(
            displayID: screenInfo.displayID,
            generation: ColorSamplerTileGeometry.nextGeneration(),
            pixelRect: pixelRect,
            sourceRect: CGRect(
                x: CGFloat(originX) / scale,
                y: CGFloat(originY) / scale,
                width: CGFloat(width) / scale,
                height: CGFloat(height) / scale
            ),
            pixelWidth: width,
            pixelHeight: height
        )
    }
}

private struct ActiveColorStream {
    let screenInfo: ColorSamplerScreenInfo
    var geometry: ColorSamplerTileGeometry
    let stream: SCStream
    let output: ColorSamplerStreamOutput
    var latestFrame: ColorSamplerFrame?
}

private struct ColorSamplerScreenInfo {
    let screen: NSScreen
    let displayID: CGDirectDisplayID
    let pointPixelScale: CGFloat
    let pixelWidth: Int
    let pixelHeight: Int
}

private struct ColorSamplerPixelLocation {
    let displayID: CGDirectDisplayID
    let x: Int
    let y: Int
}

private struct ColorSamplerTileGeometry: Sendable {
    let displayID: CGDirectDisplayID
    let generation: UInt64
    let pixelRect: CGRect
    let sourceRect: CGRect
    let pixelWidth: Int
    let pixelHeight: Int

    private static let lock = NSLock()
    nonisolated(unsafe) private static var generation: UInt64 = 0

    static func nextGeneration() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        generation &+= 1
        return generation
    }
}

struct ColorSamplerPixel: Sendable {
    let red: UInt8
    let green: UInt8
    let blue: UInt8

    var color: NSColor {
        NSColor(
            srgbRed: CGFloat(red) / 255,
            green: CGFloat(green) / 255,
            blue: CGFloat(blue) / 255,
            alpha: 1
        )
    }

    var hexText: String {
        String(format: "#%02X%02X%02X", red, green, blue)
    }

    var rgbText: String {
        "RGB \(red), \(green), \(blue)"
    }
}

private struct ColorSamplerFrame: @unchecked Sendable {
    let displayID: CGDirectDisplayID
    let generation: UInt64
    let originX: Int
    let originY: Int
    let width: Int
    let height: Int
    let bytesPerRow: Int
    let bytes: Data

    func pixel(atX screenX: Int, y screenY: Int) -> ColorSamplerPixel? {
        let x = screenX - originX
        let y = screenY - originY
        guard x >= 0, x < width, y >= 0, y < height else { return nil }
        let offset = y * bytesPerRow + x * 4
        guard offset >= 0, offset + 2 < bytes.count else { return nil }
        return bytes.withUnsafeBytes { rawBuffer in
            let pointer = rawBuffer.bindMemory(to: UInt8.self)
            return ColorSamplerPixel(
                red: pointer[offset + 2],
                green: pointer[offset + 1],
                blue: pointer[offset]
            )
        }
    }

    func previewModel(atX x: Int, y: Int) -> ColorSamplerPreviewModel? {
        guard let center = pixel(atX: x, y: y) else { return nil }
        let radius = 5
        var pixels: [ColorSamplerPixel] = []
        pixels.reserveCapacity((radius * 2 + 1) * (radius * 2 + 1))
        for row in -radius ... radius {
            for column in -radius ... radius {
                let sampleX = min(originX + width - 1, max(originX, x + column))
                let sampleY = min(originY + height - 1, max(originY, y + row))
                pixels.append(pixel(atX: sampleX, y: sampleY) ?? center)
            }
        }
        return ColorSamplerPreviewModel(center: center, pixels: pixels)
    }
}

private final class ColorSamplerStreamOutput: NSObject, @unchecked Sendable,
    SCStreamOutput, SCStreamDelegate
{
    let sampleQueue = DispatchQueue(
        label: "com.cross.crosstool.color-sampler.frames",
        qos: .userInteractive
    )

    private let stateLock = NSLock()
    private var geometry: ColorSamplerTileGeometry
    private var acceptsFrames = true
    // Frame attachments use ScreenCaptureKit's global top-left coordinate
    // space, while sourceRect is display-local. Learn that stable per-display
    // offset from the first frame, then verify the full rect after every tile
    // move so a late frame from the previous configuration is never relabelled
    // as current.
    private var screenRectOffset: CGPoint?
    private let deliveryLock = NSLock()
    private var pendingFrame: ColorSamplerFrame?
    private var isFrameDeliveryScheduled = false
    private let frameHandler: @MainActor @Sendable (ColorSamplerFrame) -> Void
    private let failureHandler: @Sendable (String) -> Void

    init(
        geometry: ColorSamplerTileGeometry,
        frameHandler: @escaping @MainActor @Sendable (ColorSamplerFrame) -> Void,
        failureHandler: @escaping @Sendable (String) -> Void
    ) {
        self.geometry = geometry
        self.frameHandler = frameHandler
        self.failureHandler = failureHandler
    }

    func pauseFrames() {
        stateLock.lock()
        acceptsFrames = false
        stateLock.unlock()
    }

    func resumeFramesAfterDraining(geometry: ColorSamplerTileGeometry) async {
        await withCheckedContinuation { continuation in
            sampleQueue.async { [self] in
                stateLock.lock()
                self.geometry = geometry
                acceptsFrames = true
                stateLock.unlock()
                continuation.resume()
            }
        }
    }

    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of outputType: SCStreamOutputType
    ) {
        guard outputType == .screen,
              sampleBuffer.isValid,
              CMSampleBufferDataIsReady(sampleBuffer),
              isCompleteFrame(sampleBuffer),
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer),
              CVPixelBufferGetPixelFormatType(pixelBuffer)
                == kCVPixelFormatType_32BGRA else {
            return
        }

        let screenRect = frameScreenRect(sampleBuffer)
        stateLock.lock()
        let acceptsFrames = acceptsFrames
        let geometry = geometry
        var screenRectOffset = screenRectOffset
        if acceptsFrames,
           screenRectOffset == nil,
           screenRect.sizeApproximatelyEquals(
            geometry.sourceRect.size,
            tolerance: 1 / 8
           ) {
            let learnedOffset = CGPoint(
                x: screenRect.minX - geometry.sourceRect.minX,
                y: screenRect.minY - geometry.sourceRect.minY
            )
            self.screenRectOffset = learnedOffset
            screenRectOffset = learnedOffset
        }
        stateLock.unlock()
        guard acceptsFrames,
              let screenRectOffset,
              screenRect.approximatelyEquals(
                geometry.sourceRect.offsetBy(
                    dx: screenRectOffset.x,
                    dy: screenRectOffset.y
                ),
                tolerance: 1 / 8
              ) else {
            return
        }

        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }
        guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        guard width == geometry.pixelWidth, height == geometry.pixelHeight else { return }

        let bytes = Data(bytes: baseAddress, count: bytesPerRow * height)
        enqueueLatestFrame(
            ColorSamplerFrame(
                displayID: geometry.displayID,
                generation: geometry.generation,
                originX: Int(geometry.pixelRect.minX),
                originY: Int(geometry.pixelRect.minY),
                width: width,
                height: height,
                bytesPerRow: bytesPerRow,
                bytes: bytes
            )
        )
    }

    /// Keep only the newest captured tile while the main actor is busy. This
    /// bounds pending memory to one ~1 MiB frame instead of creating one
    /// MainActor task (and retaining one Data buffer) for every 30 fps sample.
    private func enqueueLatestFrame(_ frame: ColorSamplerFrame) {
        deliveryLock.lock()
        pendingFrame = frame
        let shouldSchedule = !isFrameDeliveryScheduled
        if shouldSchedule {
            isFrameDeliveryScheduled = true
        }
        deliveryLock.unlock()
        guard shouldSchedule else { return }

        Task { @MainActor [weak self] in
            self?.deliverLatestFrame()
        }
    }

    @MainActor
    private func deliverLatestFrame() {
        deliveryLock.lock()
        let frame = pendingFrame
        pendingFrame = nil
        isFrameDeliveryScheduled = false
        deliveryLock.unlock()
        if let frame {
            frameHandler(frame)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        stateLock.lock()
        let shouldReport = acceptsFrames
        acceptsFrames = false
        stateLock.unlock()
        if shouldReport {
            failureHandler(error.localizedDescription)
        }
    }

    private func isCompleteFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer,
            createIfNecessary: false
        ) as? [[SCStreamFrameInfo: Any]],
        let statusValue = attachments.first?[.status] as? Int,
        let status = SCFrameStatus(rawValue: statusValue) else {
            return true
        }
        // ScreenCaptureKit labels the first usable frame as `.started` on
        // some displays. A static desktop may not produce another frame, so
        // rejecting it leaves the colour preview waiting forever.
        return status == .started || status == .complete
    }

    private func frameScreenRect(_ sampleBuffer: CMSampleBuffer) -> CGRect {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer,
            createIfNecessary: false
        ) as? [[SCStreamFrameInfo: Any]],
        let rawValue = attachments.first?[.screenRect] else {
            return .null
        }
        if let value = rawValue as? NSValue {
            return value.rectValue
        }
        let coreFoundationValue = rawValue as CFTypeRef
        if CFGetTypeID(coreFoundationValue) == CFDictionaryGetTypeID() {
            let dictionary = unsafeBitCast(
                coreFoundationValue,
                to: CFDictionary.self
            )
            var rect = CGRect.zero
            if CGRectMakeWithDictionaryRepresentation(dictionary, &rect) {
                return rect
            }
        }
        return .null
    }
}

private extension CGRect {
    func sizeApproximatelyEquals(_ other: CGSize, tolerance: CGFloat) -> Bool {
        !isNull
            && abs(width - other.width) <= tolerance
            && abs(height - other.height) <= tolerance
    }

    func approximatelyEquals(_ other: CGRect, tolerance: CGFloat) -> Bool {
        !isNull
            && abs(minX - other.minX) <= tolerance
            && abs(minY - other.minY) <= tolerance
            && sizeApproximatelyEquals(other.size, tolerance: tolerance)
    }
}

@MainActor
final class ColorSamplerOverlayWindow: NSPanel {
    let eventView: ColorSamplerEventView

    override init(
        contentRect: NSRect,
        styleMask style: NSWindow.StyleMask,
        backing backingStoreType: NSWindow.BackingStoreType,
        defer flag: Bool
    ) {
        eventView = ColorSamplerEventView(
            frame: CGRect(origin: .zero, size: contentRect.size)
        )
        super.init(
            contentRect: contentRect,
            styleMask: style,
            backing: backingStoreType,
            defer: flag
        )
        contentView = eventView
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .screenSaver
        collectionBehavior = [
            .canJoinAllSpaces,
            .fullScreenAuxiliary,
            .stationary,
            .ignoresCycle
        ]
        acceptsMouseMovedEvents = true
        ignoresMouseEvents = false
        sharingType = .none
    }

    convenience init(screen: NSScreen) {
        // AppKit's five-argument `NSWindow` initializer (the overload with a
        // `screen:` parameter) calls the subclass's four-argument designated
        // initializer. If that initializer is only compiler-generated Swift
        // traps with "Use of unimplemented initializer". Keep the designated
        // path explicit and position the panel on the target screen afterward.
        self.init(
            contentRect: screen.frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        setFrame(screen.frame, display: false)
    }

    override var canBecomeKey: Bool { true }
}

@MainActor
final class ColorSamplerEventView: NSView {
    var onMove: ((CGPoint) -> Void)?
    var onConfirm: ((CGPoint) -> Void)?
    var onCancel: (() -> Void)?
    private var trackingAreaReference: NSTrackingArea?

    override var acceptsFirstResponder: Bool { true }

    override func updateTrackingAreas() {
        if let trackingAreaReference {
            removeTrackingArea(trackingAreaReference)
        }
        let trackingArea = NSTrackingArea(
            rect: bounds,
            options: [.activeAlways, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(trackingArea)
        trackingAreaReference = trackingArea
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) {
        window?.makeKey()
        window?.makeFirstResponder(self)
        onMove?(NSEvent.mouseLocation)
    }

    override func mouseMoved(with event: NSEvent) {
        onMove?(NSEvent.mouseLocation)
    }

    override func mouseDragged(with event: NSEvent) {
        onMove?(NSEvent.mouseLocation)
    }

    override func mouseDown(with event: NSEvent) {
        onConfirm?(NSEvent.mouseLocation)
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }
}

struct ColorSamplerPreviewModel {
    let center: ColorSamplerPixel?
    let pixels: [ColorSamplerPixel]

    static let waiting = ColorSamplerPreviewModel(center: nil, pixels: [])
}

@MainActor
final class ColorSamplerPreviewView: NSView {
    // Keep the fonts and complete attribute dictionaries strongly retained for
    // the lifetime of the process. On macOS 26, repeatedly creating
    // `monospacedSystemFont` during high-frequency redraw can intermittently
    // hand CoreText an invalid font attribute and abort the process. Reusing
    // these four immutable styles also removes font construction from the hot
    // mouse-movement path.
    private static let waitingTextAttributes = textAttributes(
        size: 13,
        weight: .medium
    )
    private static let hexTextAttributes = textAttributes(
        size: 15,
        weight: .semibold
    )
    private static let rgbTextAttributes = textAttributes(
        size: 11,
        weight: .regular
    )
    private static let hintTextAttributes = textAttributes(
        size: 9,
        weight: .regular
    )

    var model: ColorSamplerPreviewModel = .waiting {
        didSet {
            needsDisplay = true
            setAccessibilityValue(model.accessibilityValue)
        }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityLabel("实时取色预览")
        setAccessibilityHelp("移动鼠标查看实时颜色，点击确认，按 Escape 取消")
        setAccessibilityValue(model.accessibilityValue)
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityLabel("实时取色预览")
        setAccessibilityHelp("移动鼠标查看实时颜色，点击确认，按 Escape 取消")
        setAccessibilityValue(model.accessibilityValue)
    }

    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let panelPath = NSBezierPath(roundedRect: bounds, xRadius: 12, yRadius: 12)
        NSColor.windowBackgroundColor.withAlphaComponent(0.96).setFill()
        panelPath.fill()
        NSColor.separatorColor.setStroke()
        panelPath.lineWidth = 1
        panelPath.stroke()

        let loupeRect = CGRect(x: 10, y: 10, width: 104, height: 104)
        NSColor.controlBackgroundColor.setFill()
        NSBezierPath(roundedRect: loupeRect, xRadius: 7, yRadius: 7).fill()

        guard let center = model.center, model.pixels.count == 121 else {
            drawText(
                "正在读取屏幕…",
                at: CGPoint(x: 129, y: 48),
                attributes: Self.waitingTextAttributes
            )
            return
        }

        let cellSize = loupeRect.width / 11
        for row in 0 ..< 11 {
            for column in 0 ..< 11 {
                let pixel = model.pixels[row * 11 + column]
                pixel.color.setFill()
                CGRect(
                    x: loupeRect.minX + CGFloat(column) * cellSize,
                    y: loupeRect.minY + CGFloat(row) * cellSize,
                    width: ceil(cellSize),
                    height: ceil(cellSize)
                ).fill()
            }
        }
        let focusRect = CGRect(
            x: loupeRect.minX + 5 * cellSize,
            y: loupeRect.minY + 5 * cellSize,
            width: cellSize,
            height: cellSize
        )
        NSColor.white.setStroke()
        let whiteOutline = NSBezierPath(rect: focusRect.insetBy(dx: -1, dy: -1))
        whiteOutline.lineWidth = 2
        whiteOutline.stroke()
        NSColor.black.setStroke()
        let blackOutline = NSBezierPath(rect: focusRect.insetBy(dx: -2, dy: -2))
        blackOutline.lineWidth = 1
        blackOutline.stroke()

        let swatchRect = CGRect(x: 130, y: 14, width: 106, height: 34)
        center.color.setFill()
        NSBezierPath(roundedRect: swatchRect, xRadius: 7, yRadius: 7).fill()
        NSColor.separatorColor.setStroke()
        NSBezierPath(roundedRect: swatchRect, xRadius: 7, yRadius: 7).stroke()
        drawText(
            center.hexText,
            at: CGPoint(x: 130, y: 60),
            attributes: Self.hexTextAttributes
        )
        drawText(
            center.rgbText,
            at: CGPoint(x: 130, y: 85),
            attributes: Self.rgbTextAttributes
        )
        drawText(
            "点击取色 · Esc 取消",
            at: CGPoint(x: 130, y: 104),
            attributes: Self.hintTextAttributes
        )
    }

    private func drawText(
        _ text: String,
        at point: CGPoint,
        attributes: [NSAttributedString.Key: Any]
    ) {
        text.draw(at: point, withAttributes: attributes)
    }

    private static func textAttributes(
        size: CGFloat,
        weight: NSFont.Weight
    ) -> [NSAttributedString.Key: Any] {
        [
            .font: NSFont.monospacedSystemFont(ofSize: size, weight: weight),
            .foregroundColor: NSColor.labelColor
        ]
    }
}

private extension ColorSamplerPreviewModel {
    var accessibilityValue: String {
        guard let center else { return "正在读取屏幕颜色" }
        return "\(center.hexText)，\(center.rgbText)"
    }
}

private extension NSScreen {
    var displayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)
            .map { CGDirectDisplayID($0.uint32Value) }
    }
}
