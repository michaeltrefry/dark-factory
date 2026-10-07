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

    [Fact]
    public async Task Remove_worker_login_deletes_only_the_credentials_and_never_runs_the_helper_or_the_setup()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--remove-worker-login");

        Assert.True(exitCode == 0, output);
        Assert.Contains("ok    _dftest holds no Claude login", output);
        Assert.False(File.Exists(run.CredentialsFile), "the credentials file was not removed");
        Assert.Equal(0, run.KeychainItems);
        var calls = run.Calls;
        Assert.DoesNotContain(calls, c => c.Contains("factory-worker-launch")); // the helper (whose exit kills every worker process) never runs
        Assert.DoesNotContain(calls, c => c.StartsWith("KILL ")); // pkill, killall and pgrep are fakes that only record
        Assert.DoesNotContain(calls, c => !c.StartsWith("sudo ") && !c.StartsWith("security ")); // no installs, dscl, ACLs, sudoers
        Assert.All(calls.Where(c => c.StartsWith("sudo ")), c =>
            Assert.Matches(@"^sudo -u _dftest env -i HOME=/Users/_dftest PATH=\S+ (/bin/rm|/bin/test|/usr/bin/security) ", c));
    }

    [Fact]
    public async Task A_normal_run_warns_that_it_kills_every_worker_process_before_the_helper_runs_and_keeps_the_login()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync();

        Assert.True(exitCode == 0, output);
        var warning = output.IndexOf("KILLS EVERY", StringComparison.Ordinal);
        var firstHelperResult = output.IndexOf("    ok    ", StringComparison.Ordinal);
        Assert.True(warning >= 0 && firstHelperResult > warning, output);
        Assert.Contains(run.Calls, c => c.Contains("factory-worker-launch"));
        Assert.True(File.Exists(run.CredentialsFile), "a normal run removed the worker's credentials file");
        Assert.Equal(2, run.KeychainItems);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("security "));
    }

    /// <summary>
    /// The whole <c>setup-worker-user.sh</c>, run as the current user (never root, never real sudo) with every privileged
    /// or system-changing command replaced on PATH by a fake that records its call. Worker <c>_dftest</c>, whose home
    /// <c>/Users/_dftest</c> the fake sudo maps to a temp dir holding a Claude credentials file and a keychain with two
    /// <c>Claude Code-credentials</c> items. The fake sudo answers a helper invocation itself; nothing ever execs a helper.
    /// </summary>
    private sealed class WholeScriptRun
    {
        private readonly string _dir;
        private readonly string _log;
        private readonly string _items;

        public WholeScriptRun(string dir)
        {
            _dir = dir;
            var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
            var home = Path.Combine(dir, "home");
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(home, "Library", "Keychains"));
            File.WriteAllText(CredentialsFile = Path.Combine(home, ".claude", ".credentials.json"), "{}");
            File.WriteAllText(Path.Combine(home, "Library", "Keychains", "login.keychain-db"), "");
            _log = Path.Combine(dir, "calls.log");
            _items = Path.Combine(dir, "keychain-items");
            File.WriteAllText(_items, "2");
            File.WriteAllText(_log, "");

            SandboxSupport.Executable(bin, "sudo", $$"""
                #!/bin/bash
                echo "sudo $*" >>'{{_log}}'
                for a in "$@"; do
                    case "$a" in
                        */factory-worker-launch) IFS= read -r _; echo "fake 1.0"; exit 0 ;;
                    esac
                done
                [ "$1" = -u ] && [ "$2" = _dftest ] && [ "$3" = env ] || exit 0
                shift 3
                while [ "$1" = -i ] || [[ $1 == *=* ]]; do shift; done
                args=()
                for a in "$@"; do args+=("${a//\/Users\/_dftest/{{home}}}"); done
                case "${args[0]}" in
                    /bin/rm) args[0]=rm ;;
                    /bin/test) args[0]=test ;;
                    /usr/bin/security) args[0]='{{bin}}/security' ;;
                    *) exit 0 ;; # e.g. the Claude Code installer: recorded, not run
                esac
                exec "${args[@]}"
                """);
            SandboxSupport.Executable(bin, "security", $$"""
                #!/bin/bash
                echo "security $*" >>'{{_log}}'
                [ "$1" = delete-generic-password ] || exit 1
                n=$(cat '{{_items}}')
                [ "$n" -gt 0 ] || exit 44
                echo $((n - 1)) >'{{_items}}'
                """);
            SandboxSupport.Executable(bin, "uname", "#!/bin/sh\necho Darwin\n");
            SandboxSupport.Executable(bin, "id", "#!/bin/sh\n[ \"$1\" = -u ] && echo 0\nexit 0\n");
            SandboxSupport.Executable(bin, "stat", "#!/bin/sh\necho root\n");
            SandboxSupport.Executable(bin, "dscl", $$"""
                #!/bin/sh
                echo "dscl $*" >>'{{_log}}'
                case "$*" in
                    *NFSHomeDirectory*) echo "NFSHomeDirectory: {{dir}}/ownerhome" ;;
                    *PrimaryGroupID*) echo "PrimaryGroupID: 450" ;;
                esac
                """);
            foreach (var name in new[] { "chown", "chmod", "install", "visudo", "mkdir", "curl" })
            {
                SandboxSupport.Executable(bin, name, $"#!/bin/sh\necho \"{name} $*\" >>'{_log}'\n");
            }
            foreach (var name in new[] { "pkill", "killall", "pgrep" })
            {
                SandboxSupport.Executable(bin, name, $"#!/bin/sh\necho \"KILL {name} $*\" >>'{_log}'\n");
            }
        }

        public string CredentialsFile { get; }

        public int KeychainItems => int.Parse(File.ReadAllText(_items).Trim());

        public string[] Calls => File.ReadAllLines(_log);

        public async Task<(int ExitCode, string Output)> RunAsync(params string[] flags)
        {
            var script = Path.Combine(SandboxSupport.RepoRoot, "scripts", "setup-worker-user.sh");
            var bin = Path.Combine(_dir, "bin");
            var args = string.Join(' ', flags.Select(f => $"'{f}'"));
            // Refuse to run unless every privileged command resolves to its fake.
            var (exitCode, stdout, stderr) = await BashAsync(_dir, $$"""
                export PATH='{{bin}}':/usr/bin:/bin
                for c in sudo id uname dscl chown chmod install visudo mkdir stat security pkill killall pgrep; do
                    [ "$(command -v "$c")" = '{{bin}}'/"$c" ] || { echo "fake $c not first on PATH" >&2; exit 99; }
                done
                /bin/bash '{{script}}' --owner owner --worker _dftest --work-root '{{_dir}}/work/root' {{args}} 2>&1
                """);
            return (exitCode, stdout + stderr);
        }
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

    private Task<(int ExitCode, string Stdout, string Stderr)> BashAsync(string script) => BashAsync(_dir, script);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> BashAsync(string dir, string script)
    {
        var psi = new ProcessStartInfo("/bin/bash", ["-c", script])
        {
            WorkingDirectory = dir,
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
