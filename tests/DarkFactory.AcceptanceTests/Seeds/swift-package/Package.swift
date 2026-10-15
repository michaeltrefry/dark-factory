// swift-tools-version:6.0
// [dark-factory e2e] The SwiftPM package the Phase 5 live acceptance tests P5-AT2, P5-AT5 and P5-AT6 seed tests into
// (docs/acceptance.md, "Phase 5"). Each merging run adds one source file and one XCTest test of it.
import PackageDescription

let package = Package(
    name: "DfE2eSwift",
    targets: [
        .target(name: "DfE2eSwift"),
        .testTarget(name: "DfE2eSwiftTests", dependencies: ["DfE2eSwift"]),
    ]
)
