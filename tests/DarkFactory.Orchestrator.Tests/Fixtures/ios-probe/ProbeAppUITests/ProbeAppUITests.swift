import XCTest

final class ProbeAppUITests: XCTestCase {
    func testTappingIncrementUpdatesTheLabel() {
        let app = XCUIApplication()
        app.launch()
        let label = app.staticTexts["count"]
        XCTAssertTrue(label.waitForExistence(timeout: 30))
        XCTAssertEqual(label.label, "Count: 0")
        app.buttons["increment"].tap()
        XCTAssertEqual(label.label, "Count: 1")
    }
}
