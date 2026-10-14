import Testing
@testable import Words

struct WordsTests {
    @Test func notBlank() {
        #expect(!isBlank("a"))
    }
}
