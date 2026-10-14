import XCTest
@testable import ProbeApp

final class ProbeAppTests: XCTestCase {
    func testCounterIncrements() {
        XCTAssertEqual(Counter.next(41), 42)
    }
}
