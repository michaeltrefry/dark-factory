using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DarkFactory.Orchestrator.Worker;

public sealed record WorkerResult(string? SessionId, int ExitCode, bool IsError, string? ResultSubtype, string? ResultText, string StderrTail)
{
    public bool Succeeded => ExitCode == 0 && !IsError && SessionId is not null;
}

/// <summary>
/// Hooks a worker run awaits while it runs, so the ledger knows about the run before it ends.
/// <see cref="OnStarted"/> gets the worker's process id as soon as the process exists;
/// <see cref="OnSession"/> gets the Claude session id as soon as it appears in the stream.
/// </summary>
public sealed record WorkerCallbacks(
    Func<int, CancellationToken, Task>? OnStarted = null,
    Func<string, CancellationToken, Task>? OnSession = null);

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
}

/// <summary>How the worker's Claude Code authenticates. Neither mode hands the worker a provider key (E1).</summary>
public enum WorkerAuth
{
    /// <summary>
    /// Claude Code uses its own login (the OS user's <c>claude</c> OAuth session) and the
    /// router passes it upstream. For routers without BYOK provider keys.
    /// </summary>
    ClaudeLogin,

    /// <summary>
    /// The router key doubles as Claude Code's API key (sent as <c>x-api-key</c>, which the
    /// router accepts as a routing token); the router supplies BYOK provider keys upstream.
    /// </summary>
    RouterKey,
}

/// <summary>
/// Runs Claude Code headless in a worktree. The child gets an allowlisted
/// environment: OS basics plus the router URL and router key — never the
/// parent's provider keys, GitHub tokens or Claude Code session variables (E1).
/// Each worker leads its own process group, so the whole tree (claude and the tools it
/// runs) can be signalled together, including after the orchestrator that started it died.
/// </summary>
public sealed class ClaudeWorker(string claudePath, Uri routerBaseUrl, string routerKey, WorkerAuth auth, TimeSpan timeout) : IWorker
{
    public const string RouterKeyHeader = "X-Weave-Router-Key";

    /// <summary>Tools a skeleton worker may use; no git/gh so it cannot push or merge on its own.</summary>
    public static readonly string[] AllowedTools =
        ["Read", "Edit", "Write", "Glob", "Grep", "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet restore:*)"];

    private static readonly string[] PassThroughVariables =
        ["PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "TMPDIR", "TERM", "DOTNET_ROOT"];

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
        env["ANTHROPIC_BASE_URL"] = routerBaseUrl.ToString().TrimEnd('/');
        env["ANTHROPIC_CUSTOM_HEADERS"] = $"{RouterKeyHeader}: {routerKey}";
        if (auth == WorkerAuth.RouterKey)
        {
            env["ANTHROPIC_API_KEY"] = routerKey;
        }
        return env;
    }

    public static IReadOnlyList<string> BuildArguments(string prompt, string? resumeSessionId = null)
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
        foreach (var arg in BuildArguments(prompt, resumeSessionId))
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment.Clear();
        foreach (var (k, v) in BuildEnvironment(Environment.GetEnvironmentVariables(), routerBaseUrl, routerKey, auth))
        {
            psi.Environment[k] = v;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {claudePath}.");
        process.StandardInput.Close();
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
            var onSession = callbacks?.OnSession;
            string? reported = null;
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                state.Accept(line);
                if (onSession is not null && state.SessionId is { } sid && sid != reported)
                {
                    reported = sid;
                    await onSession(sid, ct);
                }
            }
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Worker did not finish within {timeout} (session {state.SessionId ?? "unknown"}).");
        }
        catch
        {
            // e.g. the ledger write in onSession failed: don't leave the worker running unrecorded.
            process.Kill(entireProcessTree: true);
            throw;
        }
        await stderrTask;

        return new WorkerResult(
            state.SessionId,
            process.ExitCode,
            !state.SawResult || state.ResultIsError,
            state.ResultSubtype,
            state.ResultText,
            string.Join('\n', stderr));
    }

    public async Task<bool> StopOrphanAsync(int pid, CancellationToken ct)
    {
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
