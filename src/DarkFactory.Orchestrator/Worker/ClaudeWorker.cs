using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DarkFactory.Orchestrator.Worker;

public sealed record WorkerResult(string? SessionId, int ExitCode, bool IsError, string? ResultSubtype, string? ResultText, string StderrTail,
    string? TerminalReason = null, bool RateLimited = false)
{
    public const string HookStoppedReason = "hook_stopped";

    public bool Succeeded => ExitCode == 0 && !IsError && SessionId is not null;

    /// <summary>
    /// How a failed session reads when the plans ran out rather than the work failing: the router's answer when every
    /// subscription is exhausted (429 "All enrolled subscription accounts are currently unavailable."), an upstream 429
    /// or 529 passed through (Claude Code's "API Error: 429 …", <c>rate_limit_error</c>, <c>overloaded_error</c>,
    /// "Repeated 529 Overloaded errors") and a plan's own limit ("usage limit reached", "hit your limit").
    /// </summary>
    public static readonly string[] UsageLimitMarkers =
    [
        "All enrolled subscription accounts are currently unavailable",
        "API Error: 429",
        "API Error: 529",
        "rate_limit_error",
        "overloaded_error",
        "Repeated 529",
        "usage limit reached",
        "hit your limit",
    ];

    /// <summary>
    /// The session failed because the router or the plan refused it for usage (exhausted, rate-limited, overloaded):
    /// Claude Code flagged a <c>rate_limit</c> API error, its error result (<see cref="IsError"/>) or its stderr carries a
    /// <see cref="UsageLimitMarkers"/> marker. A result that is not an error is the model's own prose (which may well
    /// talk about rate limits) and never counts. Such a failure pauses the factory instead of escalating the item.
    /// </summary>
    public bool UsageLimited => !Succeeded && (RateLimited || (IsError && HasUsageMarker(ResultText)) || HasUsageMarker(StderrTail));

    private static bool HasUsageMarker(string? text) =>
        text is not null && UsageLimitMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A hook ended the session (<c>terminal_reason: hook_stopped</c>), e.g. the pause hook at a tool boundary: the
    /// result is a success but says nothing about the work being finished.
    /// </summary>
    public bool HookStopped => TerminalReason == HookStoppedReason;
}

/// <summary>
/// Hooks a worker run awaits while it runs, so the ledger knows about the run before it ends.
/// <see cref="OnStarted"/> gets the worker's process id as soon as the process exists;
/// <see cref="OnSession"/> gets the Claude session id as soon as it appears in the stream;
/// <see cref="OnLine"/> gets every stdout line, in order, as it is read;
/// <see cref="OnModel"/> gets each model that answers the session, the first time it appears.
/// </summary>
public sealed record WorkerCallbacks(
    Func<int, CancellationToken, Task>? OnStarted = null,
    Func<string, CancellationToken, Task>? OnSession = null,
    Func<string, CancellationToken, ValueTask>? OnLine = null,
    Func<string, CancellationToken, Task>? OnModel = null);

public interface IWorker
{
    /// <summary>
    /// Runs one worker session. <paramref name="resumeSessionId"/> continues an earlier session;
    /// the <paramref name="callbacks"/> fire before the worker finishes, so a crash can still
    /// resume the session and find the process.
    /// </summary>
    Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
        WorkerCallbacks? callbacks, CancellationToken ct);

    /// <summary>
    /// Stops a worker process (and its process group) that an earlier, crashed orchestrator
    /// started as <paramref name="pid"/>. Returns false, touching nothing, when that process is
    /// gone or is no longer this worker's.
    /// </summary>
    Task<bool> StopOrphanAsync(int pid, CancellationToken ct);

    /// <summary>
    /// Asks the worker running in <paramref name="workingDirectory"/> to stop at its next tool boundary,
    /// keeping its session for <c>--resume</c>. Best effort: the worker could ignore it, so the caller
    /// enforces the pause by cancelling the run if it does not end in time.
    /// </summary>
    void RequestPause(string workingDirectory)
    {
    }

    /// <summary>
    /// Withdraws a <see cref="RequestPause"/> for the worker in <paramref name="workingDirectory"/> (Continue arrived
    /// before it reached a tool boundary): its next tool call proceeds.
    /// </summary>
    void CancelPause(string workingDirectory)
    {
    }
}

/// <summary>
/// Marks a cancellation or timeout after which the worker could not be confirmed stopped, so the
/// caller must not delete its worktree under it (the next run stops every worker-user process
/// first: the launch helper kills them all whenever it exits).
/// </summary>
public static class WorkerStillRunning
{
    private const string Key = "DarkFactory.WorkerStillRunning";

    public static TException Mark<TException>(TException ex) where TException : Exception
    {
        ex.Data[Key] = true;
        return ex;
    }

    public static bool IsMarked(Exception ex) => ex.Data.Contains(Key);
}

/// <summary>How the worker's Claude Code authenticates. Neither mode hands the worker a provider key (E1).</summary>
public enum WorkerAuth
{
    /// <summary>
    /// Weaker, opt-in (<c>Worker:Auth=claude-login</c>): Claude Code uses the worker user's own Claude login (its
    /// <c>claude</c> OAuth session) and the router passes it upstream. The worker then holds an Anthropic credential,
    /// which E5 forbids; kept only as a fallback for a router with no enrolled plans.
    /// </summary>
    ClaudeLogin,

    /// <summary>
    /// Default (<c>Worker:Auth=router-key</c>): the worker holds no provider credential, only the router key. It goes in
    /// <c>X-Weave-Router-Key</c> (the router's preferred routing token) and as <c>ANTHROPIC_AUTH_TOKEN</c>, so Claude Code
    /// starts without a login and sends it as <c>Authorization: Bearer rk_…</c>, which the router strips before any
    /// upstream relay (unlike <c>x-api-key</c>, which its pass-through tier would forward). The router serves the
    /// request from the plans enrolled for the key (<c>router login claude</c> / <c>router login codex</c>).
    /// </summary>
    RouterKey,
}

/// <summary>
/// Runs Claude Code headless in a worktree. The child gets an allowlisted
/// environment: OS basics plus the router URL and router key — never the
/// parent's provider keys, GitHub tokens or Claude Code session variables (E1).
/// Each worker leads its own process group, so the whole tree (claude and the tools it
/// runs) can be signalled together, including after the orchestrator that started it died.
/// With a <see cref="WorkerSandbox"/> it runs as the dedicated worker user (E5) through the
/// launch helper, which builds its environment from the router variables alone; without one
/// (<c>Worker:RunAs=none</c>) it runs as the owner.
/// With <paramref name="pauseFlagDirectory"/> (owner-owned, readable but not writable by the worker user) every
/// run gets a PreToolUse hook (<c>--settings</c>, which <c>--setting-sources</c> does not filter) that denies the
/// next tool call and ends the session (<c>continue: false</c>) once <see cref="RequestPause"/> has created the
/// run's flag file there: the worker stops after its current tool call and <c>claude --resume</c> continues it.
/// </summary>
public sealed class ClaudeWorker(
    string claudePath, Uri routerBaseUrl, string routerKey, WorkerAuth auth, TimeSpan timeout,
    WorkerSandbox? sandbox = null, TimeSpan? stopGrace = null, string? pauseFlagDirectory = null) : IWorker
{
    public const string PauseReason = "Paused by the Dark Factory; the session resumes on Continue.";

    /// <summary>How long a stopped sandboxed worker gets to exit after its helper's stdin closes.</summary>
    private readonly TimeSpan _stopGrace = stopGrace ?? TimeSpan.FromSeconds(10);

    public const string RouterKeyHeader = "X-Weave-Router-Key";

    /// <summary>Tools a skeleton worker may use; no git/gh so it cannot push or merge on its own.</summary>
    public static readonly string[] AllowedTools =
        ["Read", "Edit", "Write", "Glob", "Grep", "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet restore:*)"];

    private static readonly string[] PassThroughVariables =
        ["PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "TMPDIR", "TERM", "DOTNET_ROOT"];

    /// <summary>The router variables, the only secrets a worker holds (E1). The sandbox helper adds PATH, HOME and build settings.</summary>
    public static Dictionary<string, string> BuildRouterVariables(Uri routerBaseUrl, string routerKey, WorkerAuth auth)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANTHROPIC_BASE_URL"] = routerBaseUrl.ToString().TrimEnd('/'),
            ["ANTHROPIC_CUSTOM_HEADERS"] = $"{RouterKeyHeader}: {routerKey}",
        };
        if (auth == WorkerAuth.RouterKey)
        {
            env["ANTHROPIC_AUTH_TOKEN"] = routerKey;
        }
        return env;
    }

    /// <summary>Unsandboxed environment: OS basics from the parent plus the router variables.</summary>
    public static Dictionary<string, string> BuildEnvironment(IDictionary parent, Uri routerBaseUrl, string routerKey, WorkerAuth auth)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassThroughVariables)
        {
            if (parent[name] is string value)
            {
                env[name] = value;
            }
        }
        foreach (var (k, v) in BuildRouterVariables(routerBaseUrl, routerKey, auth))
        {
            env[k] = v;
        }
        return env;
    }

    /// <summary>The pause flag of the run in <paramref name="workingDirectory"/> (one per worktree, so per item).</summary>
    public static string PauseFlagPath(string pauseFlagDirectory, string workingDirectory) =>
        Path.Combine(pauseFlagDirectory, $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory))}.pause");

    /// <summary>
    /// <c>--settings</c> JSON with one PreToolUse hook: while <paramref name="flagPath"/> exists it denies the
    /// tool call and ends the session (<c>continue: false</c>); otherwise it prints nothing and the call proceeds.
    /// </summary>
    public static string BuildPauseSettings(string flagPath)
    {
        if (flagPath.Contains('\'') || flagPath.IndexOfAny(['\n', '\r']) >= 0)
        {
            throw new ArgumentException($"Pause flag path '{flagPath}' must not contain a quote or a line break.", nameof(flagPath));
        }
        var decision = System.Text.Json.JsonSerializer.Serialize(new
        {
            @continue = false,
            stopReason = PauseReason,
            hookSpecificOutput = new { hookEventName = "PreToolUse", permissionDecision = "deny", permissionDecisionReason = PauseReason },
        });
        var command = $"if [ -e '{flagPath}' ]; then printf '%s' '{decision}'; fi";
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            hooks = new { PreToolUse = new[] { new { matcher = "*", hooks = new[] { new { type = "command", command } } } } },
        });
    }

    public static IReadOnlyList<string> BuildArguments(string prompt, string? resumeSessionId = null, string? settings = null)
    {
        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", "acceptEdits",
            // Ignore the OS user's ~/.claude settings (env, hooks, plugins) and MCP servers so the
            // worker sees only what the factory passes; the repo's own .claude settings still apply.
            "--setting-sources", "project,local",
            "--strict-mcp-config",
            "--allowedTools",
        };
        args.AddRange(AllowedTools);
        if (settings is not null)
        {
            args.Add("--settings");
            args.Add(settings);
        }
        if (resumeSessionId is not null)
        {
            args.Add("--resume");
            args.Add(resumeSessionId);
        }
        return args;
    }

    /// <summary>
    /// .NET cannot start a Unix child in a new process group, so perl does it: it makes
    /// itself the group leader, then execs claude in place (same pid).
    /// </summary>
    private const string GroupLeaderLauncher = "/usr/bin/perl";
    private const string GroupLeaderScript = """setpgrp(0, 0) or die "setpgrp: $!\n"; exec { $ARGV[0] } @ARGV or die "exec $ARGV[0]: $!\n";""";

    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromSeconds(5);

    public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
        WorkerCallbacks? callbacks, CancellationToken ct)
    {
        if (pauseFlagDirectory is null)
        {
            return await RunProcessAsync(workingDirectory, BuildArguments(prompt, resumeSessionId), callbacks, ct);
        }
        var flag = PauseFlagPath(pauseFlagDirectory, workingDirectory);
        EnsurePauseFlagDirectory(pauseFlagDirectory);
        File.Delete(flag); // left by a crashed run, it would stop this one at its first tool call
        try
        {
            return await RunProcessAsync(workingDirectory, BuildArguments(prompt, resumeSessionId, BuildPauseSettings(flag)), callbacks, ct);
        }
        finally
        {
            File.Delete(flag);
        }
    }

    /// <summary>The flag directory and flags are world-readable and owner-only writable whatever the umask, so the worker user's hook sees them.</summary>
    public const UnixFileMode PauseFlagDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute; // 0755

    public const UnixFileMode PauseFlagMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead; // 0644

    private static void EnsurePauseFlagDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            // mkdir applies the umask (under 077 the worker user could not see the flag): set the mode explicitly.
            File.SetUnixFileMode(directory, PauseFlagDirectoryMode);
        }
    }

    public void RequestPause(string workingDirectory)
    {
        if (pauseFlagDirectory is not null)
        {
            EnsurePauseFlagDirectory(pauseFlagDirectory);
            var flag = PauseFlagPath(pauseFlagDirectory, workingDirectory);
            File.WriteAllText(flag, "paused\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(flag, PauseFlagMode);
            }
        }
    }

    public void CancelPause(string workingDirectory)
    {
        if (pauseFlagDirectory is not null)
        {
            File.Delete(PauseFlagPath(pauseFlagDirectory, workingDirectory));
        }
    }

    private async Task<WorkerResult> RunProcessAsync(string workingDirectory, IReadOnlyList<string> args, WorkerCallbacks? callbacks, CancellationToken ct)
    {
        // Sandboxed, the helper makes the worker a process-group leader and the pid reported is sudo's.
        using var process = sandbox is null
            ? StartDirect(workingDirectory, args)
            : sandbox.Start(workingDirectory, claudePath, args, BuildRouterVariables(routerBaseUrl, routerKey, auth));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var state = new StreamJsonState();
        var stderr = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                stderr.Enqueue(line);
                if (stderr.Count > 40)
                {
                    stderr.Dequeue();
                }
            }
        });

        try
        {
            if (callbacks?.OnStarted is { } onStarted)
            {
                await onStarted(process.Id, ct);
            }
            var (onSession, onLine, onModel) = (callbacks?.OnSession, callbacks?.OnLine, callbacks?.OnModel);
            string? reported = null;
            var modelsReported = 0;
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                if (onLine is not null)
                {
                    // The worker's timeout also bounds a stalled tap (e.g. a slow database).
                    await onLine(line, timeoutCts.Token);
                }
                state.Accept(line);
                if (onSession is not null && state.SessionId is { } sid && sid != reported)
                {
                    reported = sid;
                    await onSession(sid, ct);
                }
                while (onModel is not null && modelsReported < state.Models.Count)
                {
                    await onModel(state.Models[modelsReported++], ct);
                }
            }
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException cancelled)
        {
            var stopped = await StopAsync(process);
            Exception ex = ct.IsCancellationRequested
                ? new OperationCanceledException("Worker run cancelled.", cancelled, ct)
                : new TimeoutException($"Worker did not finish within {timeout} (session {state.SessionId ?? "unknown"}).");
            if (!stopped)
            {
                // Still Ctrl-C / timeout to the caller, but nothing may delete the worktree under it.
                WorkerStillRunning.Mark(ex);
            }
            throw ex;
        }
        catch (Exception ex)
        {
            // e.g. the ledger write in onSession failed: don't leave the worker running unrecorded.
            if (!await StopAsync(process))
            {
                WorkerStillRunning.Mark(ex);
            }
            throw;
        }
        await stderrTask;
        if (sandbox is not null)
        {
            WorkerSandbox.Stop(process);
        }

        return new WorkerResult(
            state.SessionId,
            process.ExitCode,
            !state.SawResult || state.ResultIsError,
            state.ResultSubtype,
            state.ResultText,
            string.Join('\n', stderr),
            state.TerminalReason,
            state.RateLimited);
    }

    /// <summary>
    /// Stops a running worker. Unsandboxed, the owner kills the tree; sandboxed, the owner cannot
    /// signal the worker user's processes, so it closes the helper's stdin and the helper kills them.
    /// </summary>
    /// <returns>False when the sandboxed worker did not exit within the grace period.</returns>
    private async Task<bool> StopAsync(Process process)
    {
        if (sandbox is null)
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        WorkerSandbox.Stop(process);
        using var grace = new CancellationTokenSource(_stopGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private Process StartDirect(string workingDirectory, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(GroupLeaderLauncher)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(GroupLeaderScript);
        psi.ArgumentList.Add(claudePath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment.Clear();
        foreach (var (k, v) in BuildEnvironment(Environment.GetEnvironmentVariables(), routerBaseUrl, routerKey, auth))
        {
            psi.Environment[k] = v;
        }
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {claudePath}.");
        process.StandardInput.Close();
        return process;
    }

    public async Task<bool> StopOrphanAsync(int pid, CancellationToken ct)
    {
        if (sandbox is not null)
        {
            // The recorded pid is sudo's and the owner can't signal the worker user anyway; the worker
            // user is single-tenant, so any of its processes is a leftover worker: the helper kills them all.
            return await sandbox.StopAllAsync(ct);
        }
        // Only a group leader running claudePath is ours: a recycled pid is left alone.
        if (await ProcessInfoAsync(pid, ct) is not { } info || info.ProcessGroup != pid || !IsClaudeCommand(info.Command))
        {
            return false;
        }
        Signal(-pid, SigTerm);
        if (!await GroupExitsAsync(pid, OrphanGracePeriod, ct))
        {
            Signal(-pid, SigKill);
            await GroupExitsAsync(pid, OrphanGracePeriod, ct);
        }
        return true;
    }

    private bool IsClaudeCommand(string command) =>
        // argv[0], or the script path when claude is a script run by an interpreter.
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Any(arg => arg == claudePath || Path.GetFileName(arg) == Path.GetFileName(claudePath));

    private static async Task<(int ProcessGroup, string Command)?> ProcessInfoAsync(int pid, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-ww", "-o", "pgid=,command=", "-p", pid.ToString() })
        {
            psi.ArgumentList.Add(arg);
        }
        using var ps = Process.Start(psi)!;
        var output = await ps.StandardOutput.ReadToEndAsync(ct);
        await ps.WaitForExitAsync(ct);
        var line = output.Trim();
        var space = line.IndexOf(' ');
        if (ps.ExitCode != 0 || space < 0 || !int.TryParse(line[..space], out var group))
        {
            return null;
        }
        return (group, line[(space + 1)..].Trim());
    }

    private static async Task<bool> GroupExitsAsync(int group, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (Signal(-group, 0))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(50, ct);
        }
        return true;
    }

    private const int SigTerm = 15;
    private const int SigKill = 9;

    /// <summary>kill(2); true when the signal was delivered (for 0: the process or group exists).</summary>
    private static bool Signal(int pid, int signal) => SysKill(pid, signal) == 0;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);
}
