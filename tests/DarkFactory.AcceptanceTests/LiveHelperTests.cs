using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
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

    /// <summary>
    /// P3-AT3's scanning wrapper (sc-25710) finds a secret, by hash only, in an env value, an argument, the <c>--mcp-config</c> file
    /// and a worktree file, prints its counts as the first line, then hands over to the <c>claude</c> on PATH with the same arguments;
    /// with <c>--version</c> it hands over at once, printing nothing.
    /// </summary>
    [Fact]
    public async Task The_scan_wrapper_counts_the_secret_by_hash_and_hands_over_to_claude()
    {
        const string secret = "cg_e2e_scan_secret_4f9a";
        var root = Directory.CreateTempSubdirectory("df-e2e-scan-").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var fakeClaude = Path.Combine(bin, "claude");
            File.WriteAllText(fakeClaude, "#!/bin/sh\necho \"claude $*\"\n");
            File.SetUnixFileMode(fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var worktree = Directory.CreateDirectory(Path.Combine(root, "worktree")).FullName;
            File.WriteAllText(Path.Combine(worktree, "clean.txt"), "nothing here\n");
            File.WriteAllText(Path.Combine(worktree, "leak.txt"), $"token={secret}\n");
            var config = Path.Combine(root, "mcp.json");
            File.WriteAllText(config, """{"mcpServers":{"codegraph":{"headers":{"Authorization":"Bearer """ + secret + "\"}}}}");
            var wrapper = ScanWrapper.Write(root, [secret]);
            Assert.DoesNotContain(secret, File.ReadAllText(wrapper)); // only its hash is written

            ProcessStartInfo Start(params string[] args)
            {
                var psi = new ProcessStartInfo("/usr/bin/perl", [wrapper, .. args]) { WorkingDirectory = worktree };
                psi.Environment.Clear();
                psi.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
                psi.Environment["LEAKY"] = $"x{secret}y";
                return psi;
            }
            var (exitCode, stdout, stderr) = await RunAsync(Start("-p", $"prompt {secret}", "--mcp-config", config));

            Assert.True(exitCode == 0, stderr);
            var lines = stdout.TrimEnd('\n').Split('\n');
            Assert.Equal(2, lines.Length);
            var scan = Assert.IsType<ScanWrapper.Scan>(ScanWrapper.Result(lines));
            Assert.Equal((1, 1, 1, 1, true), (scan.Env, scan.Argv, scan.Worktree, scan.McpConfigHits, scan.McpConfig));
            Assert.Equal((2, 4), (scan.Files, scan.Args));
            Assert.Equal($"claude -p prompt {secret} --mcp-config {config}", lines[1]);

            var version = await RunAsync(Start("--version"));
            Assert.Equal("claude --version\n", version.Stdout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The live tests' MCP client of the factory's loopback proxy reaches loopback addresses only (sc-25710).</summary>
    [Fact]
    public void The_loopback_mcp_client_refuses_anything_but_a_loopback_address()
    {
        using var loopback = OutboundHttp.LoopbackMcp(new Uri("http://127.0.0.1:4711/mcp/codegraph"));
        Assert.Equal(new Uri("http://127.0.0.1:4711/mcp/codegraph"), loopback.BaseAddress);
        Assert.Throws<ArgumentException>(() => OutboundHttp.LoopbackMcp(new Uri("https://codegraph.example/mcp")));
        Assert.Throws<ArgumentException>(() => OutboundHttp.LoopbackMcp(new Uri("http://10.0.0.5:4711/mcp/codegraph")));
    }

    /// <summary>A repository whose base has no file and whose head has <paramref name="head"/>: what a pull request adding a package looks like.</summary>
    private sealed class AddedFiles(IReadOnlyDictionary<string, string> head) : IGateTestRunner
    {
        public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>([.. head.Keys.Order(StringComparer.Ordinal).Select(p => new ChangedFile('A', null, p))]);

        public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(sha == "head" ? [.. head.Keys.Order(StringComparer.Ordinal)] : []);

        public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) =>
            Task.FromResult(sha == "head" && head.TryGetValue(path, out var text) ? text : null);

        public Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct) =>
            throw new NotSupportedException("identification runs nothing");
    }

    /// <summary>
    /// P4-AT4's merging test (sc-25877) seeds a crate the cargo strategy can judge: exactly one new test, named as the live test
    /// looks it up, nothing unisolable — and a crate of its own per run, under the directory the runbook says the sandbox then holds.
    /// </summary>
    [Fact]
    public async Task The_crate_the_merging_cargo_test_seeds_has_one_new_test_the_cargo_strategy_names_as_the_live_test_expects()
    {
        var crate = GateNegativeTests.MergingCrate("1a2b3c4d");
        Assert.Equal($"{GateNegativeTests.MergedCrates}/p1a2b3c4d", crate.Directory);
        Assert.NotEqual(crate, GateNegativeTests.MergingCrate("5e6f7a8b"));
        var files = new Dictionary<string, string> { [$"{crate.Directory}/Cargo.toml"] = crate.Manifest, [$"{crate.Directory}/src/lib.rs"] = crate.Library };
        var runner = new AddedFiles(files);
        var ct = TestContext.Current.CancellationToken;
        var repo = new RepoRef("o", "r");

        var set = await new CargoNewTests().IdentifyAsync(new TestSource(runner, repo, "base", "head"), await runner.ChangesAsync(repo, "base", "head", ct), ct);

        Assert.Equal([crate.Test], set.Tests);
        Assert.Equal("df-e2e-p1a2b3c4d:lib:tests::doubles", crate.Test);
        Assert.Empty(set.Unisolable);
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
