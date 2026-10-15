import XCTest
@testable import Tally

final class TallyTests: XCTestCase {
    func testCounterStartsAtZero() {
        XCTAssertEqual(Counter.start, 0)
    }
}
