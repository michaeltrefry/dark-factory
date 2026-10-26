import XCTest

// Add only to the negative-fixture head's pre-existing macOS app-host test target. It would pass if executed;
// the deliberately crashing host must instead produce native host-error evidence and the gate must return ERROR.
final class HostLaunchProbeTests: XCTestCase {
    func testHostMustActuallyLaunchBeforeThisCanCount() {
        XCTAssertTrue(true)
    }
}
