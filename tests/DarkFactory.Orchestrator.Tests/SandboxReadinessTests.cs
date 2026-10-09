using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <see cref="WorkerSandbox.EnsureReadyAsync"/> against a fake launch helper (never the installed one, never sudo): the
/// helper reads the variable block with the given allowlist, refusing anything else the way
/// <c>scripts/factory-worker-launch</c> does, then runs the program as the current user. Its "repo" text (what the installed
/// file must match, <see cref="WorkerSandbox.HelperSource"/>) is the fake itself before setup's two edits.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SandboxReadinessTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-ready-").FullName;

    private static readonly string[] CurrentAllowlist = ["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>The helper installed before router-key auth: it allowed ANTHROPIC_API_KEY, not ANTHROPIC_AUTH_TOKEN.</summary>
    private static readonly string[] StaleAllowlist = ["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_API_KEY"];

    private string Ran => Path.Combine(_dir, "helper-ran");

    /// <summary>The fake as the repo would have it (<c>sandbox_user=_factory</c>, no uid pinned).</summary>
    private string RepoText(string[] allowlist, string stderr = "")
    {
        var patterns = string.Join(" | ", allowlist.Select(k => $"{k}=*"));
        return $$"""
            #!/bin/bash
            sandbox_user=_factory
            sandbox_uid=
            helper_version={{WorkerSandbox.HelperVersion}}
            touch '{{Ran}}'
            while IFS= read -r line; do
                [ -z "$line" ] && break
                case "$line" in
                    {{patterns}}) ;;
                    *) echo "factory-worker-launch: refusing non-allowlisted variable '${line%%=*}'" >&2; exit 64 ;;
                esac
            done
            {{stderr}}
            exec "$@"
            """;
    }

    /// <summary>What setup installs from <paramref name="repoText"/>: the same two edits as its sed.</summary>
    private static string Installed(string repoText) => repoText
        .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={Environment.UserName}\n")
        .Replace("\nsandbox_uid=\n", "\nsandbox_uid=450\n");

    /// <param name="installed">The installed file's text (default: what setup makes of the repo text).</param>
    private WorkerSandbox Sandbox(string[] allowlist, string stderr = "", Func<string, string>? installed = null)
    {
        var repoText = RepoText(allowlist, stderr);
        var helper = SandboxSupport.Executable(_dir, "fake-helper.sh", (installed ?? Installed)(repoText));
        // The probe checks the worker's user name; here the fake runs everything as the current user.
        return new WorkerSandbox(Environment.UserName, helper, SandboxSupport.FakeSudo(_dir), repoText);
    }

    /// <summary>A fake worker-user Claude Code whose <c>--version</c> prints <paramref name="version"/> (and exits <paramref name="exit"/>).</summary>
    private string Claude(string version = "2.1.291 (Claude Code)", int exit = 0) =>
        SandboxSupport.Executable(_dir, $"fake-claude-{Guid.NewGuid():N}.sh",
            $"#!/bin/sh\n[ \"$1\" = --version ] || exit 2\necho '{version}'\nexit {exit}\n");

    [Theory]
    [InlineData("2.1.290 (Claude Code)")]
    [InlineData("2.0.999 (Claude Code)")]
    [InlineData("1.9.400")]
    [InlineData("Claude Code")]
    [InlineData("2.1.291-beta")]
    [InlineData("2.1.291-rc.1 (Claude Code)")]
    public async Task A_worker_claude_older_than_the_minimum_fails_the_factory_naming_the_upgrade(string version)
    {
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist), WorkerAuth.RouterKey, Claude(version), CancellationToken.None));

        Assert.Contains($"Upgrade {Environment.UserName}'s claude", ex.Message);
        Assert.Contains(WorkerSandbox.MinClaudeVersion, ex.Message);
        Assert.Contains("claude update", ex.Message);
    }

    [Theory]
    [InlineData("2.1.291 (Claude Code)")]
    [InlineData("2.1.300 (Claude Code)")]
    [InlineData("2.2.0")]
    [InlineData("3.0.0 (Claude Code)")]
    [InlineData("2.1.291+abc")]
    public async Task A_worker_claude_at_or_above_the_minimum_passes(string version) =>
        await FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist), WorkerAuth.RouterKey, Claude(version), CancellationToken.None);

    [Fact]
    public async Task A_worker_claude_that_cannot_run_fails_the_factory_naming_the_setup()
    {
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist), WorkerAuth.RouterKey, Claude("2.1.291", exit: 127), CancellationToken.None));

        Assert.Contains("Cannot run", ex.Message);
        Assert.Contains("exit 127", ex.Message);
        Assert.Contains("sudo scripts/setup-worker-user.sh", ex.Message);
    }

    [Fact]
    public async Task A_stale_helper_that_refuses_a_router_variable_fails_the_factory_naming_the_setup_rerun()
    {
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(Sandbox(StaleAllowlist), WorkerAuth.RouterKey, Claude(), CancellationToken.None));

        Assert.Contains("re-run `sudo scripts/setup-worker-user.sh`", ex.Message);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN", ex.Message);
    }

    [Fact]
    public async Task The_current_helper_passes_the_probe_with_the_router_key_variables()
    {
        await FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist), WorkerAuth.RouterKey, Claude(), CancellationToken.None);
        Assert.True(File.Exists(Ran));
    }

    [Fact]
    public async Task Claude_login_mode_probes_only_the_variables_it_sends() =>
        await FactoryRunner.EnsureSandboxReadyAsync(Sandbox(StaleAllowlist), WorkerAuth.ClaudeLogin, Claude(), CancellationToken.None);

    /// <summary>
    /// An installed helper that is not the repo's helper after setup's two edits fails start-up before it ever runs (an older
    /// one may lack the uid sweep's guards), naming the reinstall command.
    /// </summary>
    [Theory]
    [InlineData("no-version", "no helper_version line")] // installed before this check existed
    [InlineData("old-version", "helper_version 1 is older than 3")]
    [InlineData("edited", "its text differs from the repo")]
    [InlineData("other-user", "sandbox_user is not")]
    [InlineData("unpinned", "no pinned sandbox_uid")]
    public async Task A_helper_that_is_not_the_current_one_fails_the_factory_before_it_runs(string installed, string reason)
    {
        Func<string, string> edit = installed switch
        {
            "no-version" => t => Installed(t).Replace($"helper_version={WorkerSandbox.HelperVersion}\n", ""),
            "old-version" => t => Installed(t).Replace($"helper_version={WorkerSandbox.HelperVersion}\n", "helper_version=1\n"),
            "edited" => t => Installed(t).Replace("exec \"$@\"", "echo extra\nexec \"$@\""),
            "other-user" => t => Installed(t).Replace($"\nsandbox_user={Environment.UserName}\n", "\nsandbox_user=_other\n"),
            "unpinned" => t => Installed(t).Replace("\nsandbox_uid=450\n", "\nsandbox_uid=\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(installed)),
        };

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist, installed: edit), WorkerAuth.RouterKey, Claude(), CancellationToken.None));

        Assert.Contains("Stale helper", ex.Message);
        Assert.Contains(reason, ex.Message);
        Assert.Contains("re-run `sudo scripts/setup-worker-user.sh`", ex.Message);
        Assert.False(File.Exists(Ran), "a stale helper ran");
    }

    [Fact]
    public async Task A_helper_that_cannot_be_read_fails_the_factory_naming_the_setup()
    {
        var sandbox = new WorkerSandbox(Environment.UserName, Path.Combine(_dir, "no-such-helper"), SandboxSupport.FakeSudo(_dir), "x");

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(sandbox, WorkerAuth.RouterKey, Claude(), CancellationToken.None));

        Assert.Contains("cannot read the launch helper", ex.Message);
        Assert.Contains("sudo scripts/setup-worker-user.sh", ex.Message);
    }

    [Fact]
    public async Task A_helper_that_refuses_its_uid_sweep_fails_the_factory()
    {
        // As the installed helper reports it when the pinned uid is not the worker user's (or outside 400-499).
        var refusal = "echo \"factory-worker-launch: refusing the _factory uid sweep: pinned uid 89 is outside 400-499\" >&2";

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist, refusal), WorkerAuth.RouterKey, Claude(), CancellationToken.None));

        Assert.Contains("refuses its", ex.Message);
        Assert.Contains("pinned uid 89 is outside 400-499", ex.Message);
        Assert.Contains("re-run `sudo scripts/setup-worker-user.sh`", ex.Message);
    }

    [Fact]
    public void The_repo_helper_carries_the_version_the_orchestrator_requires() =>
        Assert.True(SafeHelper.SourceContains($"\nhelper_version={WorkerSandbox.HelperVersion}\n"));
}
