import Foundation

/// A compact, persistence-safe representation of a sampled screen color.
///
/// Components are stored as 8-bit sRGB values so copied values, recent-color
/// de-duplication, and persistence remain stable across launches.
public struct ScreenSampledColor: Codable, Hashable, Identifiable, Sendable {
    public let red: UInt8
    public let green: UInt8
    public let blue: UInt8
    public let alpha: UInt8

    public var id: String {
        String(format: "%02X%02X%02X%02X", red, green, blue, alpha)
    }

    public init(red: UInt8, green: UInt8, blue: UInt8, alpha: UInt8 = 255) {
        self.red = red
        self.green = green
        self.blue = blue
        self.alpha = alpha
    }

    /// Creates an 8-bit value from normalized sRGB components.
    public init(sRGBRed: Double, green: Double, blue: Double, alpha: Double = 1) {
        red = Self.component(from: sRGBRed)
        self.green = Self.component(from: green)
        self.blue = Self.component(from: blue)
        self.alpha = Self.component(from: alpha)
    }

    public var hexText: String {
        String(format: "#%02X%02X%02X", red, green, blue)
    }

    public var rgbText: String {
        "rgb(\(red), \(green), \(blue))"
    }

    public var hslText: String {
        let red = Double(red) / 255
        let green = Double(green) / 255
        let blue = Double(blue) / 255
        let maximum = max(red, green, blue)
        let minimum = min(red, green, blue)
        let delta = maximum - minimum
        let lightness = (maximum + minimum) / 2

        guard delta > 0 else {
            return "hsl(0, 0%, \(Int((lightness * 100).rounded()))%)"
        }

        let saturation = delta / (1 - abs((2 * lightness) - 1))
        let rawHue: Double
        if maximum == red {
            rawHue = 60 * ((green - blue) / delta).truncatingRemainder(dividingBy: 6)
        } else if maximum == green {
            rawHue = 60 * (((blue - red) / delta) + 2)
        } else {
            rawHue = 60 * (((red - green) / delta) + 4)
        }

        let hue = Int(((rawHue < 0 ? rawHue + 360 : rawHue).rounded())) % 360
        let saturationPercent = Int((saturation * 100).rounded())
        let lightnessPercent = Int((lightness * 100).rounded())
        return "hsl(\(hue), \(saturationPercent)%, \(lightnessPercent)%)"
    }

    public func text(for format: ScreenColorTextFormat) -> String {
        switch format {
        case .hex: return hexText
        case .rgb: return rgbText
        case .hsl: return hslText
        }
    }

    private static func component(from value: Double) -> UInt8 {
        let finiteValue = value.isFinite ? value : 0
        return UInt8((min(max(finiteValue, 0), 1) * 255).rounded())
    }
}

public enum ScreenColorTextFormat: String, Codable, CaseIterable, Identifiable, Sendable {
    case hex
    case rgb
    case hsl

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .hex: return "HEX"
        case .rgb: return "RGB"
        case .hsl: return "HSL"
        }
    }
}
