using System.Diagnostics;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// The live tests' own parsing and setup helpers (not live, sc-25391): what a live test can only pass with must work in-process,
/// so a live run never fails on the harness itself.
/// </summary>
public class LiveHelperTests
{
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, await stdout, await stderr);
    }

    [Theory]
    [InlineData("/tmp/df-pause/factory-sc-9.pause")]
    [InlineData("/Users/someone/dark factory/flags & <more>+/factory-sc-9 é.pause")] // what the default encoder escapes
    public async Task The_stub_cli_reads_the_pause_flag_path_from_the_real_pause_settings(string flag)
    {
        using var stub = new StubClaude("stream-json-stuck-loop.jsonl");
        var settings = ClaudeWorker.BuildPauseSettings(flag);
        Assert.DoesNotContain($"'{flag}'", settings); // the serialized hook command escapes the quotes: no text match finds it

        var (exitCode, stdout, stderr) = await RunAsync(new ProcessStartInfo("/usr/bin/perl", [stub.Extractor, settings]));

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(flag, stdout);
        Assert.Equal("", (await RunAsync(new ProcessStartInfo("/usr/bin/perl", [stub.Extractor, """{"hooks":{}}"""]))).Stdout);
    }

    [Fact]
    public async Task The_stub_cli_stops_at_its_first_tool_call_once_the_real_pause_flag_exists()
    {
        using var stub = new StubClaude("stream-json-stuck-loop.jsonl");
        var flags = Directory.CreateTempSubdirectory("df-e2e-flags-").FullName;
        try
        {
            var flag = Path.Combine(flags, "factory-sc-9.pause");
            File.WriteAllText(flag, "paused\n");
            var args = ClaudeWorker.BuildArguments("do the story", settings: ClaudeWorker.BuildPauseSettings(flag));

            var (exitCode, stdout, stderr) = await RunAsync(new ProcessStartInfo("/bin/bash", [stub.Script, .. args]));

            Assert.True(exitCode == 0, stderr);
            var run = Assert.Single(stub.Runs());
            Assert.Equal(StubClaude.Stopped, run.Ending);
            var last = stdout.TrimEnd('\n').Split('\n')[^1];
            Assert.Contains("\"terminal_reason\": \"hook_stopped\"", last);
            Assert.Contains(run.Session, last);
        }
        finally
        {
            Directory.Delete(flags, recursive: true);
        }
    }

    private const string Policy = """
        version: 2
        tiers:
          sealed:
            paths: [factory/gate.yaml, .github/workflows/, CODEOWNERS, .github/CODEOWNERS, docs/CODEOWNERS, factory/prompts/]
            checks: [ci-green, review-pass, security-review]
          protected:
            paths: ["**/auth/", "**/migrations/", infra/, "**/secrets/", "**/payments/"]
            checks: [ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base]
          normal:
            checks: [ci-green, review-pass, risk-threshold, new-tests-fail-on-base]
          free:
            paths: [docs/, tests/]
            checks: [ci-green, review-pass]
        risk:
          max_changed_lines: 400
          max_changed_files: 20
          max_fix_rounds: ROUNDS
        """;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)] // the sandbox's policy (dark-factory-sandbox#6)
    [InlineData(3, 3)]
    public void The_rounds_test_expects_the_cap_the_bases_policy_sets(int policyRounds, int expected)
    {
        Assert.Equal(expected, GateNegativeTests.ExpectedFixCap(Policy.Replace("ROUNDS", policyRounds.ToString())));
        Assert.Equal(Lifecycle.MaxFixRounds, GateNegativeTests.ExpectedFixCap(null));
    }
}
