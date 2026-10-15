import SwiftUI

/// [dark-factory e2e] The iOS app the Phase 5 live acceptance tests P5-AT3 and P5-AT4 merge into (docs/acceptance.md, "Phase 5"):
/// a count and a button, and the number of e2e runs merged so far (E2eRun.count), shown for the UI test.
@main
struct Tally: App {
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
            Text("Runs: \(E2eRun.count)")
                .accessibilityIdentifier("e2e-runs")
        }
    }
}
