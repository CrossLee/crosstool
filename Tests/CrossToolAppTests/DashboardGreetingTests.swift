import Foundation
@testable import CrossToolApp
import Testing

@Suite("Dashboard greeting")
struct DashboardGreetingTests {
    @Test("Greeting follows the local hour at every boundary")
    func timeBoundaries() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try #require(TimeZone(secondsFromGMT: 0))

        #expect(DashboardGreeting.text(for: date(hour: 0, calendar: calendar), calendar: calendar) == "晚上好")
        #expect(DashboardGreeting.text(for: date(hour: 4, calendar: calendar), calendar: calendar) == "晚上好")
        #expect(DashboardGreeting.text(for: date(hour: 5, calendar: calendar), calendar: calendar) == "早上好")
        #expect(DashboardGreeting.text(for: date(hour: 11, calendar: calendar), calendar: calendar) == "早上好")
        #expect(DashboardGreeting.text(for: date(hour: 12, calendar: calendar), calendar: calendar) == "下午好")
        #expect(DashboardGreeting.text(for: date(hour: 17, calendar: calendar), calendar: calendar) == "下午好")
        #expect(DashboardGreeting.text(for: date(hour: 18, calendar: calendar), calendar: calendar) == "晚上好")
        #expect(DashboardGreeting.text(for: date(hour: 23, calendar: calendar), calendar: calendar) == "晚上好")
    }

    private func date(hour: Int, calendar: Calendar) -> Date {
        calendar.date(from: DateComponents(
            year: 2026,
            month: 8,
            day: 26,
            hour: hour
        ))!
    }
}
