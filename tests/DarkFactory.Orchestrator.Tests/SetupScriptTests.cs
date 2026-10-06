using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The toolchain check in <c>scripts/setup-worker-user.sh</c>, run without root: its functions are
/// lifted from the script and run as the current user against the repo's helper, with sudo replaced by
/// a pass-through function. The helper's <c>sandbox_user</c> is not us, so it never kills by uid.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SetupScriptTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-setup-").FullName;

    private string SlowTool => SandboxSupport.Executable(_dir, "slow-tool.sh", "#!/bin/sh\nsleep 1 && echo ok\n");

    [Fact]
    public async Task Piping_the_variable_block_in_closes_stdin_and_the_helper_kills_the_tool()
    {
        var (exitCode, stdout, _) = await BashAsync($"printf '\\n' | '{SandboxSupport.Helper}' '{SlowTool}'");

        Assert.Equal(137, exitCode);
        Assert.DoesNotContain("ok", stdout);
    }

    [Fact]
    public async Task Toolchain_check_holds_stdin_open_until_the_tool_answers()
    {
        var tool = SlowTool;

        var (exitCode, stdout, stderr) = await CheckToolchainAsync(tool);

        Assert.True(exitCode == 0, stderr);
        Assert.Contains($"ok    {tool}: ok", stdout);
    }

    [Fact]
    public async Task Toolchain_check_reports_each_failure_and_how_to_fix_it()
    {
        var good = SlowTool;
        var bad = SandboxSupport.Executable(_dir, "missing-tool.sh", "#!/bin/sh\necho 'not installed' >&2\nexit 127\n");

        var (exitCode, stdout, stderr) = await CheckToolchainAsync(good, bad);

        Assert.Equal(1, exitCode);
        Assert.Contains($"ok    {good}: ok", stdout);
        Assert.Contains($"FAIL  {bad} (exit 127): not installed", stderr);
        Assert.Contains("re-run this script", stderr);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> CheckToolchainAsync(params string[] tools)
    {
        var script = File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "scripts", "setup-worker-user.sh"));
        var functions = string.Join("\n", new[] { "with_open_stdin", "check_toolchain" }.Select(name =>
        {
            var match = Regex.Match(script, $@"^{name}\(\) \{{.*?^\}}$", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(match.Success, $"{name} not found in setup-worker-user.sh");
            return match.Value;
        }));
        var harness = $$"""
            sudo() { while :; do case "$1" in -n) shift ;; -u) shift 2 ;; *) break ;; esac; done; "$@"; }
            owner=owner worker=_factory sudoers=/nonexistent worker_home=/nonexistent
            helper='{{SandboxSupport.Helper}}'
            {{functions}}
            check_toolchain {{string.Join(' ', tools.Select(t => $"'{t}'"))}}
            """;
        return BashAsync(harness);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> BashAsync(string script)
    {
        var psi = new ProcessStartInfo("/bin/bash", ["-c", script])
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout, await stderr);
    }
}
