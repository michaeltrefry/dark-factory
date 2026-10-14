import SwiftUI

/// The iOS app the sc-26132 tests run on a simulator: a count and a button meant to add one to it.
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
        }
    }
}
