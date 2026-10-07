using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <see cref="WorkerSandbox.EnsureReadyAsync"/> against a fake launch helper (never the installed one, never sudo): the
/// helper reads the variable block with the given allowlist, refusing anything else the way
/// <c>scripts/factory-worker-launch</c> does, then runs the program as the current user.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SandboxReadinessTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-ready-").FullName;

    private static readonly string[] CurrentAllowlist = ["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>The helper installed before router-key auth: it allowed ANTHROPIC_API_KEY, not ANTHROPIC_AUTH_TOKEN.</summary>
    private static readonly string[] StaleAllowlist = ["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_API_KEY"];

    private WorkerSandbox Sandbox(string[] allowlist)
    {
        var patterns = string.Join(" | ", allowlist.Select(k => $"{k}=*"));
        var helper = SandboxSupport.Executable(_dir, "fake-helper.sh", $$"""
            #!/bin/bash
            while IFS= read -r line; do
                [ -z "$line" ] && break
                case "$line" in
                    {{patterns}}) ;;
                    *) echo "factory-worker-launch: refusing non-allowlisted variable '${line%%=*}'" >&2; exit 64 ;;
                esac
            done
            exec "$@"
            """);
        // The probe checks the worker's user name; here the fake runs everything as the current user.
        return new WorkerSandbox(Environment.UserName, helper, SandboxSupport.FakeSudo(_dir));
    }

    [Fact]
    public async Task A_stale_helper_that_refuses_a_router_variable_fails_the_factory_naming_the_setup_rerun()
    {
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(Sandbox(StaleAllowlist), WorkerAuth.RouterKey, CancellationToken.None));

        Assert.Contains("re-run `sudo scripts/setup-worker-user.sh`", ex.Message);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN", ex.Message);
    }

    [Fact]
    public async Task The_current_helper_passes_the_probe_with_the_router_key_variables() =>
        await FactoryRunner.EnsureSandboxReadyAsync(Sandbox(CurrentAllowlist), WorkerAuth.RouterKey, CancellationToken.None);

    [Fact]
    public async Task Claude_login_mode_probes_only_the_variables_it_sends() =>
        await FactoryRunner.EnsureSandboxReadyAsync(Sandbox(StaleAllowlist), WorkerAuth.ClaudeLogin, CancellationToken.None);
}
