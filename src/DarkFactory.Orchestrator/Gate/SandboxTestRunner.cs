using System.Diagnostics;
using System.Text;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// The gate's test runs (sc-25382) on the factory's own clone (<see cref="GitWorkspace"/>), executed exactly like a worker:
/// each run gets a fresh throwaway worktree shared with the worker user, and every command goes through the root-installed
/// launch helper as that user (<see cref="WorkerSandbox.Start"/>) with no variables at all — no router key, no token — so
/// the tests the PR's model wrote never run as the owner. The worktree is deleted after the run (as the worker user first).
/// Without a sandbox (<c>Worker:RunAs=none</c>, development only) the commands run as the owner with a minimal environment,
/// as unsandboxed workers do. A run is bounded by <paramref name="timeout"/>; the result files are read only as regular
/// files (never through a link the tests could plant).
/// </summary>
public sealed class SandboxTestRunner(GitWorkspace git, WorkerSandbox? sandbox, TimeSpan timeout) : IGateTestRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    /// <summary>How long a stopped run gets to exit after its helper's stdin closes.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private const int MaxResultFileBytes = 50 * 1024 * 1024;

    public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) =>
        git.ChangedFilesAsync(repo, baseSha, headSha, ct);

    public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) => git.FilesAsync(repo, sha, ct);

    public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) => git.ReadFileAsync(repo, sha, path, ct);

    public async Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct)
    {
        var workspace = await git.PrepareCommitAsync(repo, spec.Name, spec.Commit, ct);
        try
        {
            if (spec.OverlayFrom is { } overlay)
            {
                await git.OverlayAsync(workspace, overlay, spec.OverlayPaths, spec.DeletePaths, ct);
            }
            var log = new StringBuilder();
            var deadline = DateTimeOffset.UtcNow + timeout;
            foreach (var step in spec.Steps)
            {
                var (exit, output, timedOut) = await RunStepAsync(workspace.Path, step, deadline - DateTimeOffset.UtcNow, ct);
                log.AppendLine($"$ {step.Program} {string.Join(' ', step.Args)} → {(timedOut ? "timed out" : $"exit {exit}")}").AppendLine(output);
                if (timedOut)
                {
                    return TestRunReport.Failed(TestRunStatus.TimedOut, log.ToString());
                }
                if (exit != 0 && step.Phase != TestPhase.Test)
                {
                    return TestRunReport.Failed(step.Phase == TestPhase.Restore ? TestRunStatus.RestoreFailed : TestRunStatus.BuildFailed, log.ToString());
                }
            }
            var files = ResultFiles(Path.Combine(workspace.Path, NewTestsCheck.ResultsDirectory), spec.Strategy.ResultFilePattern);
            return files.Count == 0
                ? TestRunReport.Failed(TestRunStatus.NoResults, log.ToString())
                : new TestRunReport(TestRunStatus.Ran, spec.Strategy.ParseResults(files), log.ToString());
        }
        finally
        {
            await git.RemoveAsync(repo, workspace, CancellationToken.None);
        }
    }

    /// <summary>
    /// The result files' contents: regular files only, found without following links (the directory and everything in it
    /// were written by the tests), each at most <see cref="MaxResultFileBytes"/>.
    /// </summary>
    internal static List<string> ResultFiles(string directory, string pattern)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return [];
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return info.EnumerateFiles(pattern, options)
            .Where(f => f.LinkTarget is null && f.Length <= MaxResultFileBytes)
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => File.ReadAllText(f.FullName))
            .ToList();
    }

    private async Task<(int ExitCode, string Output, bool TimedOut)> RunStepAsync(string worktree, TestStep step, TimeSpan remaining, CancellationToken ct)
    {
        using var process = sandbox is null ? StartDirect(worktree, step) : sandbox.Start(worktree, step.Program, step.Args, new Dictionary<string, string>());
        var output = new Tail();
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, output);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            return (-1, output.ToString(), true);
        }
        finally
        {
            if (sandbox is not null)
            {
                WorkerSandbox.Stop(process); // the helper then kills whatever the step left running
            }
        }
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, output.ToString(), false);
    }

    private async Task StopAsync(Process process)
    {
        if (sandbox is null)
        {
            process.Kill(entireProcessTree: true);
            return;
        }
        WorkerSandbox.Stop(process);
        using var grace = new CancellationTokenSource(StopGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            // Still running as the worker user: the next helper exit (the worktree's deletion) kills it.
        }
    }

    private static async Task PumpAsync(StreamReader reader, Tail tail)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            tail.Add(line);
        }
    }

    /// <summary>The variables an unsandboxed run keeps from the owner's environment (no secrets: never the router key or a token).</summary>
    private static readonly string[] PassThrough = ["PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "TMPDIR", "DOTNET_ROOT"];

    private static Process StartDirect(string worktree, TestStep step)
    {
        var psi = new ProcessStartInfo(step.Program)
        {
            WorkingDirectory = worktree,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in step.Args)
        {
            psi.ArgumentList.Add(arg);
        }
        var parent = Environment.GetEnvironmentVariables();
        psi.Environment.Clear();
        foreach (var name in PassThrough)
        {
            if (parent[name] is string value)
            {
                psi.Environment[name] = value;
            }
        }
        // As the helper sets them: no build server or node outlives the run.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {step.Program}.");
        process.StandardInput.Close();
        return process;
    }

    /// <summary>The last lines of a run's output (thread-safe: stdout and stderr both add).</summary>
    private sealed class Tail
    {
        private const int MaxLines = 60;
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                if (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return string.Join('\n', _lines);
            }
        }
    }
}
