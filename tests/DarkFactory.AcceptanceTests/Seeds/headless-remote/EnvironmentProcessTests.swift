import Foundation
import XCTest
import Darwin

// S6 native-process environment fixture. Add this file only on the fixture's head to the existing macOS app-host
// test target. Both commits already contain probe-env and their own prepare recipe (REPO_MODE base/head).
final class EnvironmentProcessTests: XCTestCase {
    func testRepositoryEnvironmentReachesChild() throws {
        // Assertions execute inside the actual app-host test process, not in a collector receipt about that process.
        XCTAssertEqual(NSUserName(), "lwrunner")
        // The protected helper verified the real collector credential file exists,
        // is regular, collector-owned and 0600 before creating this binding. An
        // absent file must not masquerade as OS credential separation.
        let bindingPath = try XCTUnwrap(ProcessInfo.processInfo.environment["FACTORY_GUI_TEST_BINDING"])
        let binding = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: bindingPath))) as? [String: Any])
        let credential = try XCTUnwrap(binding["collectorCredentialProbe"] as? String)
        for privatePath in ["/etc/dark-factory/gui-executor.json", credential] {
            let descriptor = Darwin.open(privatePath, O_RDONLY | O_NONBLOCK)
            let refusal = errno
            if descriptor >= 0 { Darwin.close(descriptor) } // Never read its contents.
            XCTAssertEqual(descriptor, -1, "S6_COLLECTOR_CREDENTIAL_OS_BOUNDARY")
            XCTAssertTrue(refusal == EACCES || refusal == EPERM, "Missing files are not credential denial evidence")
        }
        for credential in ["GITHUB_TOKEN", "GH_TOKEN", "ACTIONS_RUNTIME_TOKEN"] {
            XCTAssertNil(ProcessInfo.processInfo.environment[credential], "Collector credentials must not reach source tests")
        }
        let configuredHelper = ProcessInfo.processInfo.environment["REPO_HELPER"]
        XCTAssertNotNil(configuredHelper, "The repository helper was not supplied to the native test process")
        // The negative arm still launches a real harmless child, rather than returning before the process boundary.
        let helper = configuredHelper ?? "/usr/bin/true"
        XCTAssertTrue(helper.hasSuffix("/.factory-e2e/probe-env"))
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: helper))
        let process = Process()
        process.executableURL = URL(fileURLWithPath: helper)
        let stdout = Pipe()
        process.standardOutput = stdout
        // Deliberately do not copy declarations into process.environment here. The real native test process must
        // already have received them from the sealed runner, and its actual child must inherit them from that process.
        try process.run()
        let bytes = stdout.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0, "S6_CHILD_EXIT_CONTRACT")
        XCTAssertEqual(String(decoding: bytes, as: UTF8.self), "head\n", "S6_ENV_CHILD_OUTPUT_CONTRACT")
    }
}
