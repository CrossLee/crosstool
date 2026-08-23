import AppKit
import CoreGraphics
import Foundation
import ImageIO
@preconcurrency import ScreenCaptureKit
import UniformTypeIdentifiers

enum ScreenCaptureSelectionMode: Sendable {
    case singleWindow
    case multipleWindows
    case singleDisplay

    fileprivate var pickerMode: SCContentSharingPickerMode {
        switch self {
        case .singleWindow:
            return .singleWindow
        case .multipleWindows:
            return .multipleWindows
        case .singleDisplay:
            return .singleDisplay
        }
    }

    fileprivate var contentStyle: SCShareableContentStyle {
        switch self {
        case .singleWindow, .multipleWindows:
            return .window
        case .singleDisplay:
            return .display
        }
    }
}

enum ScreenCaptureDisplayTarget: Sendable {
    case main
    case underMouse
}

enum ScreenCaptureKitBackendError: LocalizedError {
    case pickerAlreadyPresented
    case pickerCancelled
    case pickerFailed(String)
    case displayUnavailable
    case invalidCaptureGeometry
    case captureTooLarge(maximumPixels: Int)
    case captureReturnedNoImage
    case permissionDenied
    case captureCancelled
    case captureFailed(String)
    case invalidPNGDestination
    case pngEncoderUnavailable
    case pngEncodingFailed
    case pngWriteFailed(String)

    var errorDescription: String? {
        switch self {
        case .pickerAlreadyPresented:
            return "已有一个系统内容选择器正在等待操作"
        case .pickerCancelled:
            return "已取消选择截图内容"
        case .pickerFailed(let message):
            return "无法打开系统内容选择器：\(message)"
        case .displayUnavailable:
            return "找不到要截取的显示器"
        case .invalidCaptureGeometry:
            return "系统返回的截图尺寸无效"
        case .captureTooLarge(let maximumPixels):
            return "截图范围超过 \(maximumPixels / 1_000_000) 百万像素上限，请缩小范围后重试"
        case .captureReturnedNoImage:
            return "系统没有返回截图图像"
        case .permissionDenied:
            return "系统尚未允许截图"
        case .captureCancelled:
            return "截图已取消"
        case .captureFailed(let message):
            return "截图失败：\(message)"
        case .invalidPNGDestination:
            return "PNG 保存位置无效"
        case .pngEncoderUnavailable:
            return "无法创建 PNG 编码器"
        case .pngEncodingFailed:
            return "PNG 编码失败"
        case .pngWriteFailed(let message):
            return "PNG 写入失败：\(message)"
        }
    }
}

/// Presents macOS' system content picker and returns the resulting capture
/// filter. The picker is a process-wide singleton, so this coordinator keeps
/// one in-flight request and restores all singleton state it changes.
@MainActor
final class ScreenCaptureContentPicker: NSObject, @preconcurrency SCContentSharingPickerObserver {
    static let shared = ScreenCaptureContentPicker()

    private struct PickerState {
        let isActive: Bool
        let defaultConfiguration: SCContentSharingPickerConfiguration
        let maximumStreamCount: Int?
    }

    private let picker = SCContentSharingPicker.shared
    private var continuation: CheckedContinuation<SCContentFilter, Error>?
    private var savedState: PickerState?

    private override init() {
        super.init()
    }

    func selectContent(_ mode: ScreenCaptureSelectionMode) async throws -> SCContentFilter {
        guard continuation == nil else {
            throw ScreenCaptureKitBackendError.pickerAlreadyPresented
        }
        try Task.checkCancellation()

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                beginSelection(mode, continuation: continuation)
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.finishSelection(with: .failure(CancellationError()))
            }
        }
    }

    private func beginSelection(
        _ mode: ScreenCaptureSelectionMode,
        continuation: CheckedContinuation<SCContentFilter, Error>
    ) {
        let state = PickerState(
            isActive: picker.isActive,
            defaultConfiguration: picker.defaultConfiguration,
            maximumStreamCount: picker.maximumStreamCount
        )

        var configuration = SCContentSharingPickerConfiguration()
        configuration.allowedPickerModes = mode.pickerMode
        configuration.allowsChangingSelectedContent = false
        if let bundleIdentifier = Bundle.main.bundleIdentifier,
           !bundleIdentifier.isEmpty {
            configuration.excludedBundleIDs = [bundleIdentifier]
        }

        savedState = state
        self.continuation = continuation
        picker.defaultConfiguration = configuration
        picker.maximumStreamCount = 1
        picker.add(self)
        picker.isActive = true
        picker.present(using: mode.contentStyle)
    }

    private func finishSelection(with result: sending Result<SCContentFilter, Error>) {
        guard let continuation else { return }

        self.continuation = nil
        restorePickerState()
        continuation.resume(with: result)
    }

    private func restorePickerState() {
        picker.remove(self)

        guard let savedState else { return }
        self.savedState = nil
        picker.defaultConfiguration = savedState.defaultConfiguration
        picker.maximumStreamCount = savedState.maximumStreamCount
        picker.isActive = savedState.isActive
    }

    func contentSharingPicker(
        _ picker: SCContentSharingPicker,
        didCancelFor stream: SCStream?
    ) {
        guard stream == nil else { return }
        finishSelection(with: .failure(ScreenCaptureKitBackendError.pickerCancelled))
    }

    func contentSharingPicker(
        _ picker: SCContentSharingPicker,
        didUpdateWith filter: SCContentFilter,
        for stream: SCStream?
    ) {
        guard stream == nil else { return }
        finishSelection(with: .success(filter))
    }

    func contentSharingPickerStartDidFailWithError(_ error: Error) {
        finishSelection(
            with: .failure(ScreenCaptureKitBackendError.pickerFailed(error.localizedDescription))
        )
    }
}

enum ScreenCaptureKitScreenshotBackend {
    private static let maximumCaptureDimension = 32_768
    private static let maximumCapturePixelCount = 50_000_000

    /// A prepared, fixed display-region capture source.
    ///
    /// Long screenshots take many frames from exactly the same region. Keeping
    /// the resolved content filter and native-pixel configuration here avoids
    /// enumerating all shareable screen content and recalculating geometry for
    /// every frame.
    @MainActor
    final class RegionCaptureSession {
        private let contentFilter: SCContentFilter
        private let configuration: SCStreamConfiguration

        fileprivate init(
            contentFilter: SCContentFilter,
            configuration: SCStreamConfiguration
        ) {
            self.contentFilter = contentFilter
            self.configuration = configuration
        }

        /// Captures the prepared region at its native pixel size. Cancellation
        /// is checked on both sides of ScreenCaptureKit's callback API so a
        /// caller cancelled while a frame is in flight never receives it.
        func capture() async throws -> CGImage {
            try Task.checkCancellation()
            let image = try await ScreenCaptureKitScreenshotBackend.captureImage(
                using: contentFilter,
                configuration: configuration
            )
            try Task.checkCancellation()
            return image
        }
    }

    /// Captures a picker- or app-created filter at its native pixel scale.
    @MainActor
    static func captureImage(
        using filter: SCContentFilter,
        showsCursor: Bool = false
    ) async throws -> CGImage {
        let contentRect = filter.contentRect.standardized
        let scale = CGFloat(filter.pointPixelScale)
        let scaledWidth = contentRect.width * scale
        let scaledHeight = contentRect.height * scale

        guard contentRect.width.isFinite,
              contentRect.height.isFinite,
              scale.isFinite,
              scale > 0,
              scaledWidth.isFinite,
              scaledHeight.isFinite,
              scaledWidth > 0,
              scaledHeight > 0,
              scaledWidth <= CGFloat(maximumCaptureDimension),
              scaledHeight <= CGFloat(maximumCaptureDimension) else {
            throw ScreenCaptureKitBackendError.invalidCaptureGeometry
        }

        let pixelWidth = Int(scaledWidth.rounded(.up))
        let pixelHeight = Int(scaledHeight.rounded(.up))
        try validateCaptureSize(width: pixelWidth, height: pixelHeight)

        let configuration = SCStreamConfiguration()
        configuration.width = pixelWidth
        configuration.height = pixelHeight
        configuration.captureResolution = .best
        configuration.scalesToFit = false
        configuration.preservesAspectRatio = true
        configuration.showsCursor = showsCursor
        configuration.ignoreShadowsDisplay = false
        configuration.ignoreShadowsSingleWindow = false
        configuration.shouldBeOpaque = false

        return try await withCheckedThrowingContinuation { continuation in
            SCScreenshotManager.captureImage(
                contentFilter: filter,
                configuration: configuration
            ) { image, error in
                if let error {
                    continuation.resume(
                        throwing: mappedCaptureError(error)
                    )
                } else if let image {
                    continuation.resume(returning: image)
                } else {
                    continuation.resume(
                        throwing: ScreenCaptureKitBackendError.captureReturnedNoImage
                    )
                }
            }
        }
    }

    /// Resolves a display-bound filter. By default every Crosio window is
    /// excluded, including floating utility panels that may be visible while
    /// a long-screenshot session is running.
    @MainActor
    static func contentFilter(
        forDisplayID displayID: CGDirectDisplayID,
        excludingCurrentApplication: Bool = true
    ) async throws -> SCContentFilter {
        try Task.checkCancellation()
        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: false
        )
        try Task.checkCancellation()

        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw ScreenCaptureKitBackendError.displayUnavailable
        }

        guard excludingCurrentApplication else {
            return SCContentFilter(display: display, excludingWindows: [])
        }

        let processID = ProcessInfo.processInfo.processIdentifier
        let bundleIdentifier = Bundle.main.bundleIdentifier
        let ownApplications = content.applications.filter { application in
            application.processID == processID
                || (bundleIdentifier?.isEmpty == false
                    && application.bundleIdentifier == bundleIdentifier)
        }

        if !ownApplications.isEmpty {
            return SCContentFilter(
                display: display,
                excludingApplications: ownApplications,
                exceptingWindows: []
            )
        }

        // The running-application list can be redacted in unusual permission
        // states. Excluding every window whose owner has this PID still keeps
        // the selector and floating panels out of the captured pixels.
        let ownWindows = content.windows.filter { window in
            window.owningApplication?.processID == processID
        }
        return SCContentFilter(display: display, excludingWindows: ownWindows)
    }

    /// Captures a display-local source rectangle at its native pixel scale.
    /// The source rectangle is expressed in points with a top-left origin.
    @MainActor
    static func captureRegion(
        displayID: CGDirectDisplayID,
        sourceRect: CGRect,
        showsCursor: Bool = false
    ) async throws -> CGImage {
        let session = try await makeRegionCaptureSession(
            displayID: displayID,
            sourceRect: sourceRect,
            showsCursor: showsCursor
        )
        return try await session.capture()
    }

    @MainActor
    static func captureRegion(
        _ selection: ScreenRegionSelection,
        showsCursor: Bool = false
    ) async throws -> CGImage {
        try await captureRegion(
            displayID: selection.displayID,
            sourceRect: selection.sourceRect,
            showsCursor: showsCursor
        )
    }

    /// Prepares a fixed-region source from the selection overlay result. The
    /// shareable-content lookup and native-pixel geometry are resolved exactly
    /// once; callers can then invoke `capture()` repeatedly.
    @MainActor
    static func makeRegionCaptureSession(
        _ selection: ScreenRegionSelection,
        showsCursor: Bool = false
    ) async throws -> RegionCaptureSession {
        try await makeRegionCaptureSession(
            displayID: selection.displayID,
            sourceRect: selection.sourceRect,
            showsCursor: showsCursor
        )
    }

    /// Prepares a fixed-region source expressed in display-local logical
    /// points with a top-left origin.
    @MainActor
    static func makeRegionCaptureSession(
        displayID: CGDirectDisplayID,
        sourceRect: CGRect,
        showsCursor: Bool = false
    ) async throws -> RegionCaptureSession {
        try Task.checkCancellation()
        let filter = try await contentFilter(forDisplayID: displayID)
        try Task.checkCancellation()
        let geometry = try nativeRegionGeometry(sourceRect, filter: filter)

        let configuration = SCStreamConfiguration()
        configuration.sourceRect = geometry.sourceRect
        configuration.width = geometry.pixelWidth
        configuration.height = geometry.pixelHeight
        configuration.captureResolution = .best
        configuration.scalesToFit = false
        configuration.preservesAspectRatio = true
        configuration.showsCursor = showsCursor
        configuration.ignoreShadowsDisplay = true
        configuration.shouldBeOpaque = false

        try Task.checkCancellation()
        return RegionCaptureSession(
            contentFilter: filter,
            configuration: configuration
        )
    }

    /// Encodes and atomically replaces the file at `destinationURL` with PNG data.
    static func writePNG(_ image: CGImage, to destinationURL: URL) throws {
        guard destinationURL.isFileURL, !destinationURL.hasDirectoryPath else {
            throw ScreenCaptureKitBackendError.invalidPNGDestination
        }

        let pngData = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            pngData,
            UTType.png.identifier as CFString,
            1,
            nil
        ) else {
            throw ScreenCaptureKitBackendError.pngEncoderUnavailable
        }

        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else {
            throw ScreenCaptureKitBackendError.pngEncodingFailed
        }

        do {
            try (pngData as Data).write(to: destinationURL, options: .atomic)
        } catch {
            throw ScreenCaptureKitBackendError.pngWriteFailed(error.localizedDescription)
        }
    }

    @MainActor
    static func captureDisplay(
        _ target: ScreenCaptureDisplayTarget,
        showsCursor: Bool = false
    ) async throws -> CGImage {
        let displayID = try displayID(for: target)
        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw ScreenCaptureKitBackendError.displayUnavailable
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])
        return try await captureImage(using: filter, showsCursor: showsCursor)
    }

    @MainActor
    static func captureMainDisplay(showsCursor: Bool = false) async throws -> CGImage {
        try await captureDisplay(.main, showsCursor: showsCursor)
    }

    @MainActor
    static func captureDisplayUnderMouse(showsCursor: Bool = false) async throws -> CGImage {
        try await captureDisplay(.underMouse, showsCursor: showsCursor)
    }

    @MainActor
    private static func displayID(
        for target: ScreenCaptureDisplayTarget
    ) throws -> CGDirectDisplayID {
        switch target {
        case .main:
            return CGMainDisplayID()
        case .underMouse:
            let mouseLocation = NSEvent.mouseLocation
            guard let screen = NSScreen.screens.first(where: { screen in
                NSMouseInRect(mouseLocation, screen.frame, false)
            }),
            let screenNumber = screen.deviceDescription[
                NSDeviceDescriptionKey("NSScreenNumber")
            ] as? NSNumber else {
                throw ScreenCaptureKitBackendError.displayUnavailable
            }
            return CGDirectDisplayID(screenNumber.uint32Value)
        }
    }

    @MainActor
    private static func captureImage(
        using filter: SCContentFilter,
        configuration: SCStreamConfiguration
    ) async throws -> CGImage {
        try await withCheckedThrowingContinuation { continuation in
            SCScreenshotManager.captureImage(
                contentFilter: filter,
                configuration: configuration
            ) { image, error in
                if let error {
                    continuation.resume(
                        throwing: mappedCaptureError(error)
                    )
                } else if let image {
                    continuation.resume(returning: image)
                } else {
                    continuation.resume(
                        throwing: ScreenCaptureKitBackendError.captureReturnedNoImage
                    )
                }
            }
        }
    }

    private struct NativeRegionGeometry {
        let sourceRect: CGRect
        let pixelWidth: Int
        let pixelHeight: Int
    }

    @MainActor
    private static func nativeRegionGeometry(
        _ requestedRect: CGRect,
        filter: SCContentFilter
    ) throws -> NativeRegionGeometry {
        let contentSize = filter.contentRect.standardized.size
        let scale = CGFloat(filter.pointPixelScale)

        guard requestedRect.origin.x.isFinite,
              requestedRect.origin.y.isFinite,
              requestedRect.width.isFinite,
              requestedRect.height.isFinite,
              contentSize.width.isFinite,
              contentSize.height.isFinite,
              scale.isFinite,
              scale > 0,
              contentSize.width > 0,
              contentSize.height > 0 else {
            throw ScreenCaptureKitBackendError.invalidCaptureGeometry
        }

        let displayBounds = CGRect(origin: .zero, size: contentSize)
        let clippedRect = requestedRect.standardized.intersection(displayBounds)
        guard !clippedRect.isNull,
              !clippedRect.isEmpty,
              clippedRect.width > 0,
              clippedRect.height > 0 else {
            throw ScreenCaptureKitBackendError.invalidCaptureGeometry
        }

        // Expand fractional point edges to the nearest native pixel so the
        // configured output never rescales or drops a selected edge.
        let minimumPixelX = (clippedRect.minX * scale).rounded(.down)
        let minimumPixelY = (clippedRect.minY * scale).rounded(.down)
        let maximumDisplayPixelX = (contentSize.width * scale).rounded(.down)
        let maximumDisplayPixelY = (contentSize.height * scale).rounded(.down)
        let maximumPixelX = min(
            (clippedRect.maxX * scale).rounded(.up),
            maximumDisplayPixelX
        )
        let maximumPixelY = min(
            (clippedRect.maxY * scale).rounded(.up),
            maximumDisplayPixelY
        )
        let pixelWidth = maximumPixelX - minimumPixelX
        let pixelHeight = maximumPixelY - minimumPixelY

        guard pixelWidth.isFinite,
              pixelHeight.isFinite,
              pixelWidth >= 1,
              pixelHeight >= 1,
              pixelWidth <= CGFloat(maximumCaptureDimension),
              pixelHeight <= CGFloat(maximumCaptureDimension) else {
            throw ScreenCaptureKitBackendError.invalidCaptureGeometry
        }

        let integerWidth = Int(pixelWidth)
        let integerHeight = Int(pixelHeight)
        try validateCaptureSize(width: integerWidth, height: integerHeight)

        return NativeRegionGeometry(
            sourceRect: CGRect(
                x: minimumPixelX / scale,
                y: minimumPixelY / scale,
                width: pixelWidth / scale,
                height: pixelHeight / scale
            ),
            pixelWidth: integerWidth,
            pixelHeight: integerHeight
        )
    }

    private static func validateCaptureSize(width: Int, height: Int) throws {
        guard width > 0, height > 0 else {
            throw ScreenCaptureKitBackendError.invalidCaptureGeometry
        }
        let (pixelCount, overflow) = width.multipliedReportingOverflow(by: height)
        guard !overflow, pixelCount <= maximumCapturePixelCount else {
            throw ScreenCaptureKitBackendError.captureTooLarge(
                maximumPixels: maximumCapturePixelCount
            )
        }
    }

    private static func mappedCaptureError(_ error: Error) -> Error {
        let nsError = error as NSError
        guard nsError.domain == SCStreamErrorDomain else {
            return ScreenCaptureKitBackendError.captureFailed(error.localizedDescription)
        }
        switch nsError.code {
        case SCStreamError.Code.userDeclined.rawValue:
            return ScreenCaptureKitBackendError.permissionDenied
        case SCStreamError.Code.userStopped.rawValue:
            return ScreenCaptureKitBackendError.captureCancelled
        default:
            return ScreenCaptureKitBackendError.captureFailed(error.localizedDescription)
        }
    }
}
