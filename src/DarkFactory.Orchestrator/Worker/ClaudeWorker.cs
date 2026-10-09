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
/// <see cref="OnModel"/> gets each model that answers the session, the first time it appears;
/// <see cref="OnUntrusted"/> gets the session id and the taint reason (<see cref="Taint.ForTool"/>) of each web or MCP tool the session
/// uses, the first time, before the next line is read, with a token that is never cancelled (the record must commit). A session that uses one with no <see cref="OnUntrusted"/> to record it fails
/// (E2: a taint that cannot be recorded is not ignored).
/// </summary>
public sealed record WorkerCallbacks(
    Func<int, CancellationToken, Task>? OnStarted = null,
    Func<string, CancellationToken, Task>? OnSession = null,
    Func<string, CancellationToken, ValueTask>? OnLine = null,
    Func<string, CancellationToken, Task>? OnModel = null,
    Func<string, string, CancellationToken, Task>? OnUntrusted = null);

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

    /// <summary>The tools and permission mode the worker's sessions run with (<see cref="WorkerTools"/>).</summary>
    WorkerTools Tools => WorkerTools.Implementer;
}

/// <summary>
/// What a worker session may do: Claude Code's <c>--permission-mode</c>, <c>--allowedTools</c> and <c>--disallowedTools</c>
/// (a deny beats any allow rule, the target repo's own settings included).
/// </summary>
public sealed record WorkerTools
{
    private WorkerTools(string permissionMode, IReadOnlyList<string> allowed, IReadOnlyList<string> denied, string settingSources,
        bool confinedToWorkingDirectory) =>
        (PermissionMode, Allowed, Denied, SettingSources, ConfinedToWorkingDirectory) =
        (permissionMode, allowed, denied, settingSources, confinedToWorkingDirectory);

    public string PermissionMode { get; }

    /// <summary>The allow rules every session gets; a confined session also gets <see cref="ReadRule"/> of its working directory.</summary>
    public IReadOnlyList<string> Allowed { get; }

    public IReadOnlyList<string> Denied { get; }

    /// <summary>Claude Code's <c>--setting-sources</c>: which settings files the session loads (empty: none, only <c>--settings</c>).</summary>
    public string SettingSources { get; }

    /// <summary>
    /// Whether the session's file reads are confined to its working directory: its only allow rule is <see cref="ReadRule"/> of that
    /// directory and its <c>--settings</c> set <see cref="BlockReadsOutsideWorkingDirectories"/>.
    /// </summary>
    public bool ConfinedToWorkingDirectory { get; }

    /// <summary>
    /// An implementing worker (implement, review fix, CI fix, conflict fix): edits files and builds and tests; no web, no git. It loads
    /// the target repo's own Claude settings (<c>project,local</c>; <see cref="Taint.OfRepoSettings"/>).
    /// </summary>
    public static readonly WorkerTools Implementer = new("acceptEdits", ClaudeWorker.AllowedTools, ClaudeWorker.DeniedTools, "project,local",
        confinedToWorkingDirectory: false);

    /// <summary>
    /// The tools a read-only session uses: it reads and searches its own checkout, nothing else. None is allowed by name (a bare
    /// <c>Read</c> rule would pre-approve a read of any path): Claude Code runs them without approval inside the working directory
    /// only, and bounds Glob and Grep by the <c>Read</c> rules.
    /// </summary>
    public static readonly string[] ReadOnlyTools = ["Read", "Glob", "Grep"];

    /// <summary>The settings key that makes Claude Code's file tools refuse every path outside the working directories, in every mode.</summary>
    public const string BlockReadsOutsideWorkingDirectories = "blockReadsOutsideWorkingDirectories";

    /// <summary>
    /// The one allow rule of a confined session: <c>Read(//&lt;absolute directory&gt;/**)</c> (Claude Code's <c>//</c> anchors at the
    /// filesystem root; a single <c>/</c> would anchor at the settings source). Refuses a directory whose path a gitignore pattern
    /// would read as more than itself (<c>* ? [ ] \ !</c>, a leading <c>#</c>, a line break) or that is not absolute.
    /// </summary>
    public static string ReadRule(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!path.StartsWith('/') || path == "/" || path.IndexOfAny(['*', '?', '[', ']', '\\', '!', '#', '\n', '\r', '(', ')']) >= 0)
        {
            throw new ArgumentException($"Cannot confine reads to '{directory}': not an absolute path a Read rule matches literally.", nameof(directory));
        }
        return $"Read(/{path}/**)";
    }

    /// <summary>The allow rules of a session in <paramref name="workingDirectory"/>: <see cref="Allowed"/>, plus its <see cref="ReadRule"/> when confined.</summary>
    public IReadOnlyList<string> AllowedIn(string? workingDirectory) => !ConfinedToWorkingDirectory
        ? Allowed
        : [.. Allowed, ReadRule(workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory),
            "a session confined to its working directory needs that directory"))];

    /// <summary>
    /// The tools that write a file, run a command, start a sub-agent (which could be given other tools) or reach the web: every one is
    /// denied to a read-only session, whatever the repo's settings allow.
    /// </summary>
    public static readonly string[] WriteOrExecTools =
        ["Write", "Edit", "MultiEdit", "NotebookEdit", "Bash", "BashOutput", "KillShell", "Task", "Agent", .. Taint.WebTools];

    /// <summary>
    /// Claude Code's mode that auto-denies every tool call that would otherwise prompt (headless, nobody can approve one): file reads
    /// inside the working directory and calls an allow rule pre-approves still run, nothing else does; <c>acceptEdits</c> would let a
    /// session edit files unasked.
    /// </summary>
    public const string ReadOnlyPermissionMode = "dontAsk";

    /// <summary>
    /// The triage worker (E4): a session that reads an issue's text holds no write or exec capability at all and reads nothing but its
    /// own triage worktree — Read, Glob and Grep inside its working directory (<see cref="ReadRule"/> its only allow rule,
    /// <see cref="BlockReadsOutsideWorkingDirectories"/> set), in <see cref="ReadOnlyPermissionMode"/>, with every
    /// <see cref="WriteOrExecTools"/> tool denied and no settings file loaded (<c>--setting-sources ""</c>: neither the repo's nor the
    /// worker user's settings can widen it). It reasons from the code; it builds and runs nothing.
    /// </summary>
    public static readonly WorkerTools ReadOnly = new(ReadOnlyPermissionMode, [], WriteOrExecTools, "", confinedToWorkingDirectory: true);

    /// <summary>
    /// Whether these tools are read-only and confined: no allow rule beyond the working directory's <see cref="ReadRule"/>, every
    /// <see cref="WriteOrExecTools"/> tool denied, in <see cref="ReadOnlyPermissionMode"/>, loading no settings file.
    /// </summary>
    public bool IsReadOnly =>
        PermissionMode == ReadOnlyPermissionMode
        && Allowed.Count == 0
        && ConfinedToWorkingDirectory
        && SettingSources.Length == 0
        && WriteOrExecTools.All(t => Denied.Contains(t, StringComparer.Ordinal));
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
    WorkerSandbox? sandbox = null, TimeSpan? stopGrace = null, string? pauseFlagDirectory = null, WorkerTools? tools = null) : IWorker
{
    /// <summary>The tools and permission mode every session of this worker runs with (default <see cref="WorkerTools.Implementer"/>).</summary>
    public WorkerTools Tools { get; } = tools ?? WorkerTools.Implementer;

    public const string PauseReason = "Paused by the Dark Factory; the session resumes on Continue.";

    /// <summary>How long a stopped sandboxed worker gets to exit after its helper's stdin closes.</summary>
    private readonly TimeSpan _stopGrace = stopGrace ?? TimeSpan.FromSeconds(10);

    public const string RouterKeyHeader = "X-Weave-Router-Key";

    /// <summary>Tools a skeleton worker may use; no git/gh so it cannot push or merge on its own.</summary>
    public static readonly string[] AllowedTools =
        ["Read", "Edit", "Write", "Glob", "Grep", "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet restore:*)"];

    /// <summary>
    /// Tools a worker is denied (<c>--disallowedTools</c>, which beats any allow rule, the target repo's own settings included): the
    /// web tools (<see cref="Taint.WebTools"/>). Defence in depth: a use that still shows in the stream taints the session (E4).
    /// </summary>
    public static readonly string[] DeniedTools = Taint.WebTools;

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

    /// <summary>
    /// <paramref name="settings"/> (<c>--settings</c> JSON, or none) with <c>permissions.blockReadsOutsideWorkingDirectories</c> set:
    /// Claude Code's file tools then refuse every path outside the working directories in every permission mode.
    /// </summary>
    public static string WithReadsBlockedOutsideWorkingDirectories(string? settings)
    {
        var root = settings is null ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonNode.Parse(settings)!.AsObject();
        if (root["permissions"] is not System.Text.Json.Nodes.JsonObject permissions)
        {
            root["permissions"] = permissions = new System.Text.Json.Nodes.JsonObject();
        }
        permissions[WorkerTools.BlockReadsOutsideWorkingDirectories] = true;
        return root.ToJsonString();
    }

    /// <summary>
    /// The CLI arguments of a session. Its working directory is required for tools confined to it
    /// (<see cref="WorkerTools.ConfinedToWorkingDirectory"/>), whose one allow rule names it.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string prompt, string? resumeSessionId = null, string? settings = null,
        WorkerTools? tools = null, string? workingDirectory = null)
    {
        tools ??= WorkerTools.Implementer;
        var args = new List<string>
        {
            "-p", prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", tools.PermissionMode,
            // Never the OS user's ~/.claude settings (env, hooks, plugins) or MCP servers, so the worker sees only what the factory
            // passes; an implementer still loads the repo's own .claude settings (project,local), a read-only session none ("").
            "--setting-sources", tools.SettingSources,
            "--strict-mcp-config",
            "--allowedTools",
        };
        args.AddRange(tools.AllowedIn(workingDirectory));
        args.Add("--disallowedTools");
        args.AddRange(tools.Denied);
        if (tools.ConfinedToWorkingDirectory)
        {
            settings = WithReadsBlockedOutsideWorkingDirectories(settings);
        }
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
            return await RunProcessAsync(workingDirectory, BuildArguments(prompt, resumeSessionId, tools: Tools, workingDirectory: workingDirectory), callbacks, ct);
        }
        var flag = PauseFlagPath(pauseFlagDirectory, workingDirectory);
        EnsurePauseFlagDirectory(pauseFlagDirectory);
        File.Delete(flag); // left by a crashed run, it would stop this one at its first tool call
        try
        {
            return await RunProcessAsync(workingDirectory, BuildArguments(prompt, resumeSessionId, BuildPauseSettings(flag), Tools, workingDirectory), callbacks, ct);
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
            var (onSession, onLine, onModel, onUntrusted) = (callbacks?.OnSession, callbacks?.OnLine, callbacks?.OnModel, callbacks?.OnUntrusted);
            string? reported = null;
            var modelsReported = 0;
            var untrustedReported = 0;
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
                while (untrustedReported < state.UntrustedReads.Count)
                {
                    var reason = state.UntrustedReads[untrustedReported++];
                    if (onUntrusted is null || state.SessionId is not { } tainted)
                    {
                        throw new InvalidOperationException(
                            $"The worker used an untrusted-content tool ({reason}) but its taint cannot be recorded (no session id or no taint callback).");
                    }
                    // Not cancellable: a Ctrl-C or Pause must not abort a taint the session has already earned (E4).
                    await onUntrusted(tainted, reason, CancellationToken.None);
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
