import SwiftUI

// Dedicated negative-fixture application's entry point, installed in BOTH immutable commits.
// Do not put this in an operational application. Its macOS unit-test target must use this app as TEST_HOST.
@main
struct HostLaunchFailure: App {
    init() {
        fatalError("[dark-factory fixture] deliberate test-host launch failure, not a failing test")
    }
    var body: some Scene {
        WindowGroup { Text("This negative fixture must never reach a window") }
    }
}
