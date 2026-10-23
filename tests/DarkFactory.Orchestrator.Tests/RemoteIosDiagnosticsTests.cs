using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Prepare;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RemoteIosDiagnosticsTests
{
    [Theory]
    [InlineData("prepare-step-failed", "remote-prepare-step-failed: Prepare step 1: A declared prepare step exited unsuccessfully. Check its declared command, inputs and dependencies.")]
    [InlineData("prepare-step-timeout", "remote-prepare-step-timeout: Prepare step 1: A declared prepare step exceeded its execution budget. Reduce its work or increase the configured test timeout.")]
    [InlineData("artifact-fetch-failed", "remote-artifact-fetch-failed: A declared artifact could not be downloaded, verified or unpacked. Check its URL, digest and size limits.")]
    [InlineData("package-hydration-failed", "remote-package-hydration-failed: Package hydration failed on the hosted runner. Check dependency declarations and package availability.")]
    [InlineData("package-ipc-unavailable", "remote-package-ipc-unavailable: The hosted runner cannot provide the exact isolated package endpoints. Check loopback binding and sandbox support; no broader network access is permitted.")]
    [InlineData("package-storage-unavailable", "remote-package-storage-unavailable: The hosted runner cannot verify isolated temporary and cache directories owned by its fresh source role. Check the role's Darwin directory setup; owner directories are never granted.")]
    [InlineData("package-signature-refused", "remote-package-signature-refused: Native SwiftPM refused a package signature or its supplied trust policy. Check the package signing evidence and registry trust configuration; validation is never bypassed.")]
    [InlineData("package-auth-required", "remote-package-auth-required: A package requires credentials unavailable to the hosted runner. Use an anonymously accessible pinned dependency or a declared prepared artifact; owner credentials are never forwarded.")]
    [InlineData("package-unavailable", "remote-package-unavailable: A package was not found or requires credentials unavailable to the hosted runner. Check its URL and anonymous availability; owner credentials are never forwarded.")]
    public async Task Prepare_failure_is_a_bounded_code_written_diagnostic(string code, string expected)
    {
        var report = await Run(code);
        Assert.Equal(expected, report.Log);
    }

    [Fact]
    public async Task Arbitrary_prepare_failure_text_is_not_promoted_to_diagnostics()
    {
        var report = await Run("SECRET arbitrary repository failure");
        Assert.Equal("remote-invalid-evidence: The sealed iOS workflow or its executed evidence could not be validated. Verify the installed workflow and result artifacts.", report.Log);
    }

    private static Task<TestRunReport> Run(string code)
    {
        var strategy = new XcodeNewTests();
        var spec = new TestRunSpec("gate-sc-1-base", RemoteIosGateTests.Base, RemoteIosGateTests.Head, [], [], [], strategy, ".results");
        var prepare = new PrepareRecipe([], [new("generate", ["./generate"], ".", new Dictionary<string, string>(), null)]);
        return new RemoteIosNewTests(new Failure(code)) { PollInterval = TimeSpan.Zero }.RunExecutionAsync(new("acme", "ios"),
            RemoteIosGateTests.Base, RemoteIosGateTests.Head, spec, [XcodeIosFixture.UnitTestId],
            new Dictionary<string, string> { [XcodeIosFixture.UnitTestId] = new XcodeNewTests.Build("-project", "Tally.xcodeproj", "Tally", XcodeProject.IOS).ToString() }, prepare, CancellationToken.None);
    }

    private sealed class Failure(string code) : IIosWorkflowGitHub
    {
        private readonly RemoteIosGateTests.Dispatch inner = new();
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct) => inner.ReadDefaultWorkflowAsync(repo, ct);
        public Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct) => inner.DispatchAsync(repo, workflow, request, correlation, ct);
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct) => inner.RunsAsync(repo, ct);
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct) => inner.CancelAsync(repo, run, ct);
        public async Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
        {
            var result = JsonNode.Parse(await inner.ResultAsync(repo, run, correlation, ct))!;
            var execution = result["execution"]!;
            execution["status"] = "prepare-failed";
            execution["files"] = new JsonArray();
            execution["log"] = "";
            execution["failure"] = new JsonObject { ["code"] = code, ["step"] = code.StartsWith("prepare-step-", StringComparison.Ordinal) ? JsonValue.Create(0) : null };
            return result.ToJsonString();
        }
    }
}
