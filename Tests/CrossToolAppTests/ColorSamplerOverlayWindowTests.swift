import AppKit
@testable import CrossToolApp
import Testing

@MainActor
@Suite("Real-time color sampler overlay")
struct ColorSamplerOverlayWindowTests {
    @Test("Production overlay uses AppKit's designated initializer safely")
    func createsAndReleasesOverlayOnEveryDisplay() throws {
        let screens = try #require(NSScreen.screens.isEmpty ? nil : NSScreen.screens)

        for screen in screens {
            let window = ColorSamplerOverlayWindow(screen: screen)
            #expect(window.contentView === window.eventView)
            #expect(window.frame == screen.frame)
            #expect(window.styleMask.contains(.borderless))
            #expect(window.styleMask.contains(.nonactivatingPanel))
            window.orderOut(nil)
        }
    }

    @Test("Production preview survives repeated offscreen text redraws")
    func repeatedlyDrawsWaitingAndSampledColor() throws {
        let width = 250
        let height = 124
        let view = ColorSamplerPreviewView(
            frame: CGRect(x: 0, y: 0, width: width, height: height)
        )
        let bitmap = try #require(
            NSBitmapImageRep(
                bitmapDataPlanes: nil,
                pixelsWide: width,
                pixelsHigh: height,
                bitsPerSample: 8,
                samplesPerPixel: 4,
                hasAlpha: true,
                isPlanar: false,
                colorSpaceName: .deviceRGB,
                bytesPerRow: 0,
                bitsPerPixel: 0
            )
        )
        let context = try #require(NSGraphicsContext(bitmapImageRep: bitmap))

        draw(view, in: context, iterations: 100)

        let pixel = ColorSamplerPixel(red: 236, green: 236, blue: 236)
        view.model = ColorSamplerPreviewModel(
            center: pixel,
            pixels: Array(repeating: pixel, count: 121)
        )
        // The former implementation rebuilt monospacedSystemFont on every
        // draw and reproduced the production CoreText abort within ~100
        // iterations on the affected OS. Exercise the exact production view
        // well beyond that threshold.
        draw(view, in: context, iterations: 5_000)

        #expect(bitmap.bitmapData != nil)
    }

    private func draw(
        _ view: ColorSamplerPreviewView,
        in context: NSGraphicsContext,
        iterations: Int
    ) {
        for _ in 0 ..< iterations {
            autoreleasepool {
                NSGraphicsContext.saveGraphicsState()
                NSGraphicsContext.current = context
                view.draw(view.bounds)
                NSGraphicsContext.restoreGraphicsState()
            }
        }
    }
}
