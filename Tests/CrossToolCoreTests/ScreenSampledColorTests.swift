import XCTest
@testable import CrossToolCore

final class ScreenSampledColorTests: XCTestCase {
    func testFormatsPrimaryRed() {
        let color = ScreenSampledColor(red: 255, green: 0, blue: 0)

        XCTAssertEqual(color.hexText, "#FF0000")
        XCTAssertEqual(color.rgbText, "rgb(255, 0, 0)")
        XCTAssertEqual(color.hslText, "hsl(0, 100%, 50%)")
    }

    func testFormatsAchromaticColor() {
        let color = ScreenSampledColor(red: 128, green: 128, blue: 128)

        XCTAssertEqual(color.hslText, "hsl(0, 0%, 50%)")
    }

    func testNormalizedComponentsClampAndRound() {
        let color = ScreenSampledColor(
            sRGBRed: 1.5,
            green: 0.5,
            blue: -1,
            alpha: .infinity
        )

        XCTAssertEqual(color, ScreenSampledColor(red: 255, green: 128, blue: 0, alpha: 0))
    }

    func testCodableRoundTripPreservesStableIdentity() throws {
        let color = ScreenSampledColor(red: 18, green: 52, blue: 86, alpha: 120)

        let data = try JSONEncoder().encode(color)
        let decoded = try JSONDecoder().decode(ScreenSampledColor.self, from: data)

        XCTAssertEqual(decoded, color)
        XCTAssertEqual(decoded.id, "12345678")
    }
}
