import XCTest

final class TallyUITests: XCTestCase {
    func testLaunchShowsZero() {
        let app = XCUIApplication()
        app.launch()
        let label = app.staticTexts["count"]
        XCTAssertTrue(label.waitForExistence(timeout: 30))
        XCTAssertEqual(label.label, "Count: 0")
    }
}
