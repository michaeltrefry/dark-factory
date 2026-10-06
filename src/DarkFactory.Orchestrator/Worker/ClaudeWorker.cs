using System.Collections;
using System.Diagnostics;

namespace DarkFactory.Orchestrator.Worker;

public sealed record WorkerResult(string? SessionId, int ExitCode, bool IsError, string? ResultSubtype, string? ResultText, string StderrTail)
{
    public bool Succeeded => ExitCode == 0 && !IsError && SessionId is not null;
}

public interface IWorker
{
    /// <summary>
    /// Runs one worker session. <paramref name="resumeSessionId"/> continues an earlier session;
    /// <paramref name="onSession"/> is awaited as soon as the session id appears in the stream,
    /// before the worker finishes, so a crash can still resume it.
    /// </summary>
    Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
        Func<string, CancellationToken, Task>? onSession, CancellationToken ct);
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

    public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
        Func<string, CancellationToken, Task>? onSession, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(claudePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
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
}
