import XCTest
@testable import DfE2eSwift

/// [dark-factory e2e] The package's original test: it passes, so the package builds and tests on every base.
final class DfE2eSwiftTests: XCTestCase {
    func testIdentity() {
        XCTAssertEqual(dfE2eIdentity(3), 3)
    }
}
