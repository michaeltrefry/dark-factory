import SwiftUI

/// The smallest app the sandbox probe (sc-26126) can unit-test and UI-test on a simulator.
@main
struct ProbeApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
        }
    }
}

struct ContentView: View {
    @State private var count = 0

    var body: some View {
        VStack(spacing: 16) {
            Text("Count: \(count)")
                .accessibilityIdentifier("count")
            Button("Increment") { count = Counter.next(count) }
                .accessibilityIdentifier("increment")
        }
    }
}

enum Counter {
    static func next(_ value: Int) -> Int { value + 1 }
}
