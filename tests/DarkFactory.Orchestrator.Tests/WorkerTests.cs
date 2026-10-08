using System.Collections;
using System.Diagnostics;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

public class StreamJsonTests
{
    [Fact]
    public void Captures_the_terminal_reason_of_a_session_a_hook_ended()
    {
        var state = new StreamJsonState();
        state.Accept("""{"type":"result","subtype":"success","is_error":false,"terminal_reason":"hook_stopped","session_id":"s"}""");

        Assert.Equal("hook_stopped", state.TerminalReason);
        Assert.True(new WorkerResult("s", 0, false, "success", null, "", state.TerminalReason).HookStopped);
        Assert.False(new WorkerResult("s", 0, false, "success", null, "", "completed").HookStopped);
    }

    [Fact]
    public void Captures_session_id_from_init_and_successful_result()
    {
        var state = new StreamJsonState();
        state.Accept("""{"type":"system","subtype":"init","session_id":"0b7c6a3e-1111-2222-3333-444455556666","tools":[]}""");
        state.Accept("""{"type":"assistant","message":{"content":[]},"session_id":"0b7c6a3e-1111-2222-3333-444455556666"}""");
        state.Accept("""{"type":"result","subtype":"success","is_error":false,"session_id":"0b7c6a3e-1111-2222-3333-444455556666","total_cost_usd":0.01}""");

        Assert.Equal("0b7c6a3e-1111-2222-3333-444455556666", state.SessionId);
        Assert.True(state.SawResult);
        Assert.False(state.ResultIsError);
        Assert.Equal("success", state.ResultSubtype);
    }

    [Fact]
    public void Flags_error_results_and_ignores_noise()
    {
        var state = new StreamJsonState();
        state.Accept("");
        state.Accept("not json");
        state.Accept("{broken");
        state.Accept("[1,2]");
        state.Accept("""{"type":"result","subtype":"error_max_turns","is_error":true,"result":"Not logged in","session_id":"s-1"}""");

        Assert.Equal("s-1", state.SessionId);
        Assert.True(state.ResultIsError);
        Assert.Equal("error_max_turns", state.ResultSubtype);
        Assert.Equal("Not logged in", state.ResultText);
    }

    [Fact]
    public void Without_result_line_nothing_is_reported_as_seen()
    {
        var state = new StreamJsonState();
        state.Accept("""{"type":"system","subtype":"init","session_id":"s-2"}""");
        Assert.False(state.SawResult);
    }
}

/// <summary>
/// Tests that set process-wide environment variables run alone, so no parallel test reads
/// (or launches a process inheriting) a value they temporarily planted.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

[Collection(ProcessEnvironmentCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class ClaudeWorkerTests
{
    private static readonly Uri Router = new("http://localhost:8080/");

    private static Hashtable ParentEnvironment() => new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = "/Users/someone",
        ["USER"] = "someone",
        ["ANTHROPIC_API_KEY"] = "sk-ant-provider",
        ["ANTHROPIC_AUTH_TOKEN"] = "provider-token",
        ["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com",
        ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Other: leak",
        ["OPENAI_API_KEY"] = "sk-openai",
        ["AWS_SECRET_ACCESS_KEY"] = "aws",
        ["GH_TOKEN"] = "ghp_owner",
        ["GITHUB_TOKEN"] = "ghp_owner",
        ["SHORTCUT_API_TOKEN"] = "sc",
        ["FACTORY_ROUTER_KEY"] = "rk_parent",
        ["CLAUDECODE"] = "1",
        ["CLAUDE_CODE_SESSION_ID"] = "parent-session",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth",
    };

    [Fact]
    public void Environment_carries_only_router_url_and_router_key_header()
    {
        var env = ClaudeWorker.BuildEnvironment(ParentEnvironment(), Router, "rk_worker", WorkerAuth.ClaudeLogin);

        Assert.Equal("http://localhost:8080", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("X-Weave-Router-Key: rk_worker", env["ANTHROPIC_CUSTOM_HEADERS"]);
        Assert.Equal("/usr/bin:/bin", env["PATH"]);
        Assert.Equal("/Users/someone", env["HOME"]);

        Assert.Equal(["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "HOME", "PATH", "USER"], env.Keys.Order());
        Assert.DoesNotContain(env.Values, v => v.Contains("sk-") || v.Contains("ghp_") || v.Contains("oauth") || v.Contains("leak"));
    }

    [Fact]
    public void Router_key_auth_mode_uses_the_router_key_as_bearer_auth_token_and_nothing_else()
    {
        var env = ClaudeWorker.BuildEnvironment(ParentEnvironment(), Router, "rk_worker", WorkerAuth.RouterKey);

        // Bearer (ANTHROPIC_AUTH_TOKEN), not x-api-key (ANTHROPIC_API_KEY): the router strips an rk_ bearer before
        // any upstream relay, while its pass-through tier forwards x-api-key as is.
        Assert.Equal("rk_worker", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("X-Weave-Router-Key: rk_worker", env["ANTHROPIC_CUSTOM_HEADERS"]);
        Assert.Equal(["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "HOME", "PATH", "USER"], env.Keys.Order());
        Assert.DoesNotContain(env.Values, v => v.Contains("sk-") || v.Contains("ghp_") || v.Contains("oauth") || v.Contains("leak"));
    }

    [Fact]
    public void Arguments_request_stream_json_and_exclude_git_and_gh_tools()
    {
        var args = ClaudeWorker.BuildArguments("do the story");

        Assert.Equal(["-p", "do the story", "--output-format", "stream-json", "--verbose"], args.Take(5));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("project,local", args[args.ToList().IndexOf("--setting-sources") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("git") || a.Contains("gh ") || a == "--dangerously-skip-permissions");
    }

    [Fact]
    public async Task Runs_process_in_worktree_and_reports_session_from_stream()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var envDump = Path.Combine(dir, "env.txt");
        var script = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            env > "{{envDump}}"
            pwd >> "{{envDump}}"
            echo '{"type":"system","subtype":"init","session_id":"sess-abc"}'
            echo 'progress noise'
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"sess-abc"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-should-not-leak");
        try
        {
            var worker = new ClaudeWorker(script, Router, "rk_worker", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1));
            var lines = new List<string>();
            var result = await worker.RunAsync(dir, "prompt", null,
                new WorkerCallbacks(OnLine: (line, _) => { lines.Add(line); return ValueTask.CompletedTask; }), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("sess-abc", result.SessionId);
            // Every stdout line reaches the tap, in order, non-JSON noise included.
            Assert.Equal(3, lines.Count);
            Assert.Equal("progress noise", lines[1]);
            var dumped = File.ReadAllText(envDump);
            Assert.Contains("ANTHROPIC_CUSTOM_HEADERS=X-Weave-Router-Key: rk_worker", dumped);
            Assert.DoesNotContain("sk-ant-should-not-leak", dumped);
            Assert.Contains(new DirectoryInfo(dir).Name, dumped);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    /// <summary>
    /// S5 probe: even when the orchestrator holds the App private key (here via the env-var form of
    /// <c>GitHub:PrivateKeyPem</c>), the launched worker sees it in neither its environment, its
    /// argv nor its worktree.
    /// </summary>
    [Fact]
    public async Task Worker_never_receives_the_app_private_key_in_env_args_or_worktree()
    {
        var pem = System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem();
        var keyBody = pem.Split('\n')[1];
        var worktree = Directory.CreateTempSubdirectory("df-worker-wt-").FullName;
        File.WriteAllText(Path.Combine(worktree, "Program.cs"), "// worker code\n");
        var probeDir = Directory.CreateTempSubdirectory("df-worker-probe-").FullName;
        var dump = Path.Combine(probeDir, "probe.txt");
        var script = Path.Combine(probeDir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            env > "{{dump}}"
            printf '%s\n' "$@" >> "{{dump}}"
            grep -rIl -e "PRIVATE KEY" -e "{{keyBody[..32]}}" . > "{{dump}}.files"
            echo '{"type":"system","subtype":"init","session_id":"sess-key"}'
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"sess-key"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("GitHub__PrivateKeyPem", pem);
        try
        {
            var result = await new ClaudeWorker(script, Router, "rk_worker", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1))
                .RunAsync(worktree, "implement the story", null, null, CancellationToken.None);

            Assert.True(result.Succeeded);
            var probe = File.ReadAllText(dump);
            Assert.Contains("ANTHROPIC_BASE_URL=", probe); // the dump ran
            Assert.Contains("implement the story", probe);
            Assert.DoesNotContain("PRIVATE KEY", probe);
            Assert.DoesNotContain(keyBody, probe);
            Assert.DoesNotContain("GitHub__", probe);
            Assert.Equal("", File.ReadAllText($"{dump}.files")); // no file in the worktree carries the key
        }
        finally
        {
            Environment.SetEnvironmentVariable("GitHub__PrivateKeyPem", null);
        }
    }

    [Fact]
    public async Task Nonzero_exit_is_not_success()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var script = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(script, "#!/bin/sh\necho '{\"type\":\"system\",\"session_id\":\"s\"}'\necho boom >&2\nexit 3\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1)).RunAsync(dir, "p", null, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("boom", result.StderrTail);
    }

    [Fact]
    public async Task Pause_hook_denies_the_next_tool_call_and_ends_the_session_only_while_the_flag_exists()
    {
        var dir = Directory.CreateTempSubdirectory("df-pause-").FullName;
        var flag = ClaudeWorker.PauseFlagPath(dir, "/work/worktrees/acme/widgets/factory-sc-7/");
        Assert.Equal(Path.Combine(dir, "factory-sc-7.pause"), flag);
        using var settings = System.Text.Json.JsonDocument.Parse(ClaudeWorker.BuildPauseSettings(flag));
        var hook = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0];
        Assert.Equal("*", hook.GetProperty("matcher").GetString());
        var command = hook.GetProperty("hooks")[0].GetProperty("command").GetString()!;

        Assert.Equal("", await Sh(command)); // no flag: the call proceeds
        File.WriteAllText(flag, "");
        using var decision = System.Text.Json.JsonDocument.Parse(await Sh(command));
        Assert.False(decision.RootElement.GetProperty("continue").GetBoolean());
        Assert.Equal("deny", decision.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
        Assert.Equal("PreToolUse", decision.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());

        Assert.Throws<ArgumentException>(() => ClaudeWorker.BuildPauseSettings("/tmp/it's"));

        static async Task<string> Sh(string command)
        {
            using var p = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", command]) { RedirectStandardOutput = true })!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.Equal(0, p.ExitCode);
            return output;
        }
    }

    [Fact]
    public async Task Worker_gets_the_pause_hook_and_request_pause_raises_its_flag_which_the_run_clears()
    {
        var dir = Directory.CreateTempSubdirectory("df-pause-").FullName;
        var flags = Path.Combine(dir, "controls");
        var worktree = Directory.CreateDirectory(Path.Combine(dir, "factory-sc-9")).FullName;
        var argsDump = Path.Combine(dir, "args.txt");
        var script = Path.Combine(dir, "fake-claude.sh");
        // Waits until the flag exists (as a tool call would), then ends.
        File.WriteAllText(script, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > "{{argsDump}}"
            if [ -e "{{Path.Combine(flags, "factory-sc-9.pause")}}" ]; then echo stale > "{{Path.Combine(dir, "saw-stale")}}"; fi
            echo '{"type":"system","subtype":"init","session_id":"s-9"}'
            while [ ! -e "{{Path.Combine(flags, "factory-sc-9.pause")}}" ]; do sleep 0.02; done
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"s-9"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(flags);
        File.WriteAllText(Path.Combine(flags, "factory-sc-9.pause"), "stale");
        var worker = new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1), pauseFlagDirectory: flags);

        var run = worker.RunAsync(worktree, "p", null, new WorkerCallbacks(OnSession: (_, _) =>
        {
            ((IWorker)worker).RequestPause(worktree);
            return Task.CompletedTask;
        }), CancellationToken.None);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded);
        var args = File.ReadAllLines(argsDump).ToList();
        Assert.Equal(ClaudeWorker.BuildPauseSettings(Path.Combine(flags, "factory-sc-9.pause")), args[args.IndexOf("--settings") + 1]);
        Assert.False(File.Exists(Path.Combine(flags, "factory-sc-9.pause"))); // cleared after the run (and the stale one before it)
        Assert.False(File.Exists(Path.Combine(dir, "saw-stale")));
    }

    [Fact]
    public void Pause_flags_are_readable_by_the_worker_user_whatever_the_umask_and_continue_withdraws_them()
    {
        var dir = Directory.CreateTempSubdirectory("df-pause-").FullName;
        var flags = Path.Combine(dir, "controls");
        // As mkdir/creat would leave them under umask 077: owner-only.
        Directory.CreateDirectory(flags);
        File.SetUnixFileMode(flags, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var flag = ClaudeWorker.PauseFlagPath(flags, Path.Combine(dir, "factory-sc-9"));
        File.WriteAllText(flag, "");
        File.SetUnixFileMode(flag, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        IWorker worker = new ClaudeWorker("claude", Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1), pauseFlagDirectory: flags);

        worker.RequestPause(Path.Combine(dir, "factory-sc-9"));

        Assert.Equal(ClaudeWorker.PauseFlagDirectoryMode, File.GetUnixFileMode(flags)); // 0755
        Assert.Equal(ClaudeWorker.PauseFlagMode, File.GetUnixFileMode(flag)); // 0644
        const UnixFileMode othersWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(flags) & othersWrite); // the worker user cannot raise or clear a flag
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(flag) & othersWrite);

        worker.CancelPause(Path.Combine(dir, "factory-sc-9"));

        Assert.False(File.Exists(flag));
    }

    [Fact]
    public void Resume_passes_the_session_id_to_claude()
    {
        var args = ClaudeWorker.BuildArguments("go on", "sess-1").ToList();
        Assert.Equal("sess-1", args[args.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--resume", ClaudeWorker.BuildArguments("start"));
    }

    [Fact]
    public async Task Session_callback_runs_as_soon_as_the_id_streams_before_the_worker_exits()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var flag = Path.Combine(dir, "recorded");
        var argsDump = Path.Combine(dir, "args.txt");
        var script = Path.Combine(dir, "fake-claude.sh");
        // The worker only finishes once the callback has created the flag file.
        File.WriteAllText(script, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > "{{argsDump}}"
            echo '{"type":"system","subtype":"init","session_id":"sess-early"}'
            i=0
            while [ ! -f "{{flag}}" ]; do i=$((i+1)); [ $i -gt 200 ] && exit 9; sleep 0.05; done
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"sess-early"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var seen = new List<string>();

        var result = await new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1)).RunAsync(dir, "p", "sess-early",
            new WorkerCallbacks(OnSession: (sid, _) =>
            {
                seen.Add(sid);
                File.WriteAllText(flag, sid);
                return Task.CompletedTask;
            }), CancellationToken.None);

        Assert.True(result.Succeeded, result.StderrTail);
        Assert.Equal(["sess-early"], seen);
        var args = File.ReadAllLines(argsDump).ToList();
        Assert.Equal("sess-early", args[args.IndexOf("--resume") + 1]);
    }

    [Fact]
    public async Task A_stalled_line_tap_is_bounded_by_the_worker_timeout()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var script = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(script, """
            #!/bin/sh
            echo '{"type":"system","subtype":"init","session_id":"sess-stall"}'
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"sess-stall"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // The tap never finishes on its own (a stalled database): only the worker timeout can end it.
        var run = new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMilliseconds(500)).RunAsync(dir, "p", null,
            new WorkerCallbacks(OnLine: async (_, c) => await Task.Delay(Timeout.Infinite, c)), CancellationToken.None);

        // (WaitAsync's own TimeoutException has a different message.)
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.StartsWith("Worker did not finish within", ex.Message);
    }

    [Fact]
    public async Task Worker_leads_its_own_process_group_and_reports_its_pid_before_streaming()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var ids = Path.Combine(dir, "ids.txt");
        var script = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "$$ $(ps -o pgid= -p $$ | tr -d ' ')" > "{{ids}}"
            echo '{"type":"system","subtype":"init","session_id":"s"}'
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"s"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var events = new List<string>();

        var result = await new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1)).RunAsync(dir, "p", null,
            new WorkerCallbacks(
                OnStarted: (pid, _) => { events.Add($"started {pid}"); return Task.CompletedTask; },
                OnSession: (sid, _) => { events.Add($"session {sid}"); return Task.CompletedTask; }),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StderrTail);
        var (pid, group) = File.ReadAllText(ids).Trim().Split(' ') is [var p, var g] ? (p, g) : throw new InvalidOperationException();
        Assert.Equal(pid, group); // claude itself (same pid as the launcher) leads its group
        Assert.Equal([$"started {pid}", "session s"], events);
    }

    [Fact]
    public async Task Stop_orphan_leaves_a_process_that_is_not_this_workers_claude_alone()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        // A group leader, like a worker, but running something other than the configured claude.
        using var other = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-e", "setpgrp(0, 0); sleep 30"]))!;
        try
        {
            await Task.Delay(300);
            var worker = new ClaudeWorker(Path.Combine(dir, "fake-claude.sh"), Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1));

            Assert.False(await worker.StopOrphanAsync(other.Id, CancellationToken.None));
            Assert.False(other.HasExited);
        }
        finally
        {
            other.Kill();
        }
    }

    [Fact]
    public async Task Stop_orphan_kills_a_worker_group_including_its_children()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var pids = Path.Combine(dir, "pids.txt");
        var script = Path.Combine(dir, "fake-claude.sh");
        // Ignores SIGTERM in the leader so only the SIGKILL escalation ends it; its child is a "tool".
        File.WriteAllText(script, $$"""
            #!/bin/sh
            trap '' TERM
            sleep 600 &
            echo "$$ $!" > "{{pids}}"
            echo '{"type":"system","subtype":"init","session_id":"s"}'
            wait
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var worker = new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1));
        var started = new TaskCompletionSource<int>();
        // Stands in for the crashed orchestrator: the worker keeps running while we stop it "from the next run".
        var run = worker.RunAsync(dir, "p", null, new WorkerCallbacks(OnStarted: (pid, _) => { started.SetResult(pid); return Task.CompletedTask; }), CancellationToken.None);
        var leader = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (!File.Exists(pids) || File.ReadAllText(pids).Trim().Split(' ').Length < 2)
        {
            await Task.Delay(50);
        }
        var child = int.Parse(File.ReadAllText(pids).Trim().Split(' ')[1]);

        Assert.True(await worker.StopOrphanAsync(leader, CancellationToken.None));

        Assert.False(IsAlive(leader));
        Assert.False(IsAlive(child));
        Assert.False(await worker.StopOrphanAsync(leader, CancellationToken.None)); // gone now
        Assert.False((await run.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded); // the stream just ends
    }

    private static bool IsAlive(int pid)
    {
        using var p = Process.Start(new ProcessStartInfo("kill", ["-0", pid.ToString()]) { RedirectStandardError = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
