using System.Diagnostics;
using System.Text;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// The gate's test runs (sc-25382) on the factory's own clone (<see cref="GitWorkspace"/>), executed exactly like a worker:
/// each run gets a fresh throwaway worktree shared with the worker user, and every command goes through the root-installed
/// launch helper as that user (<see cref="WorkerSandbox.Start"/>) with no variables at all — no router key, no token — so
/// the tests the PR's model wrote never run as the owner. The worktree is deleted after the run (as the worker user first).
/// Without a sandbox (<c>Worker:RunAs=none</c>, development only) the commands run as the owner with a minimal environment,
/// as unsandboxed workers do. A run is bounded by <paramref name="timeout"/>; the result files are read only as regular
/// files (never through a link the tests could plant). A step that reports on its standard output
/// (<see cref="TestStep.CaptureOutput"/>: cargo) has it kept in memory, with its exit code, instead of a file. A failed
/// <see cref="TestPhase.Toolchain"/> step (the stack's tool is not installed for the run's user, or not on the helper's
/// fixed PATH) ends the run as <see cref="TestRunStatus.ToolchainMissing"/>, naming the program.
/// </summary>
public sealed class SandboxTestRunner(GitWorkspace git, WorkerSlots? slots, TimeSpan timeout, long maxResultFileBytes = SandboxTestRunner.MaxResultFileBytes)
    : IGateTestRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    /// <summary>How long one sandboxed run may take (<c>Gate:TestTimeoutMinutes</c>).</summary>
    public TimeSpan Timeout => timeout;

    /// <summary>How long a stopped run gets to exit after its helper's stdin closes.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>The largest result file or build log read (a larger result file makes the run's report unreadable).</summary>
    public const long MaxResultFileBytes = 50 * 1024 * 1024;

    public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) =>
        git.ChangedFilesAsync(repo, baseSha, headSha, ct);

    public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) => git.FilesAsync(repo, sha, ct);

    public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) => git.ReadFileAsync(repo, sha, path, ct);

    /// <summary>
    /// Sandboxed, the whole run — its worktree, every step and the worktree's deletion as the worker user — happens under the
    /// sandbox user's lock (<see cref="WorkerSandbox.Lock"/>, sc-25988): the gate's test phase holds it already (and waited for it
    /// honouring Pause and Stop); a caller that holds none waits for it here.
    /// </summary>
    public Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct) =>
        slots is null ? RunLockedAsync(repo, spec, ct) : slots.HoldAsync(null, _ => RunLockedAsync(repo, spec, ct), ct);

    /// <summary>The sandbox the steps run in: the slot the calling flow holds (<see cref="WorkerSlots.Current"/>, sc-25989: the item's), or null unsandboxed.</summary>
    private WorkerSandbox? Sandbox => slots?.Current;

    private async Task<TestRunReport> RunLockedAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct)
    {
        if (spec.ResultsDirectory is not { Length: > 0 } name || name.Contains('/') || name.Contains('\\') || name is "." or "..")
        {
            throw new ArgumentException($"'{spec.ResultsDirectory}' is not a results directory name.", nameof(spec));
        }
        var workspace = await git.PrepareCommitAsync(repo, spec.Name, spec.Commit, ct);
        var cleaned = false;
        try
        {
            var report = await RunInWorktreeAsync(workspace, spec, ct);
            cleaned = true;
            return await CleanUpAsync(workspace.Path, spec) is { Length: > 0 } cleanup ? report with { Log = report.Log + cleanup } : report;
        }
        finally
        {
            if (!cleaned)
            {
                await CleanUpAsync(workspace.Path, spec); // a timed-out, failed or cancelled run is cleaned up too
            }
            await git.RemoveAsync(repo, workspace, CancellationToken.None);
        }
    }

    /// <summary>How long each <see cref="TestPhase.Cleanup"/> step may take (it runs on no cancellation token, after the run's own deadline).</summary>
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The run's <see cref="TestPhase.Cleanup"/> steps, each bounded by <see cref="CleanupTimeout"/> and on no cancellation
    /// token, whatever happened before; their log (empty when there are none). Never throws: a failed clean-up decides nothing.
    /// </summary>
    private async Task<string> CleanUpAsync(string worktree, TestRunSpec spec)
    {
        var log = new StringBuilder();
        foreach (var step in spec.Steps.Where(s => s.Phase == TestPhase.Cleanup))
        {
            try
            {
                var (exit, output, timedOut, _) = await RunStepAsync(worktree, step, CleanupTimeout, CancellationToken.None);
                log.AppendLine($"$ {Describe(step)} → {(timedOut ? "timed out" : $"exit {exit}")}").AppendLine(output);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log.AppendLine($"$ {Describe(step)} → could not be run: {e.Message}");
            }
        }
        return log.ToString();
    }

    private async Task<TestRunReport> RunInWorktreeAsync(Workspace workspace, TestRunSpec spec, CancellationToken ct)
    {
        {
            if (spec.OverlayFrom is { } overlay)
            {
                await git.OverlayAsync(workspace, overlay, spec.OverlayPaths, spec.DeletePaths, ct, spec.Replacements);
            }
            // Every result file must come from this run: the directory is a fresh random name, and nothing may be there yet.
            var results = Path.Combine(workspace.Path, spec.ResultsDirectory);
            if (Directory.Exists(results) || File.Exists(results) || new FileInfo(results).LinkTarget is not null)
            {
                throw new InvalidOperationException($"The run's results directory {spec.ResultsDirectory} already exists in {Ci.Short(spec.Commit)}'s worktree.");
            }
            var log = new StringBuilder();
            var deadline = DateTimeOffset.UtcNow + timeout;
            var buildFailed = false;
            // The outputs kept for the steps that report on standard output: build steps' are build logs, test steps' results.
            var buildOutputs = new List<(string Name, string? Content)>();
            var testOutputs = new List<(string Name, string? Content, int ExitCode)>();
            foreach (var step in spec.Steps.Where(s => s.Phase != TestPhase.Cleanup))
            {
                if (buildFailed && step.Phase == TestPhase.Test)
                {
                    break;
                }
                var (exit, output, timedOut, captured) = await RunStepAsync(workspace.Path, step, deadline - DateTimeOffset.UtcNow, ct);
                log.AppendLine($"$ {Describe(step)} → {(timedOut ? "timed out" : $"exit {exit}")}").AppendLine(output);
                for (var fallback = step.Fallback; !timedOut && exit == TestStep.InvalidCommandLine && fallback is not null; fallback = fallback.Fallback)
                {
                    (exit, output, timedOut, captured) = await RunStepAsync(workspace.Path, fallback, deadline - DateTimeOffset.UtcNow, ct);
                    log.AppendLine($"$ {Describe(fallback)} → {(timedOut ? "timed out" : $"exit {exit}")}").AppendLine(output);
                }
                if (timedOut)
                {
                    return TestRunReport.Failed(TestRunStatus.TimedOut, log.ToString());
                }
                if (step.FactoryWideExit is { } unavailable && exit == unavailable)
                {
                    // The machine, not the item (sc-26132): nothing of this run is judged, and no item escalates one by one for it.
                    var why = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3);
                    throw new FactoryUnavailableException($"the gate's test runs: `{Describe(step)}` exited {exit}: {string.Join(" ", why)}",
                        new InvalidOperationException(output));
                }
                if (exit != 0 && step.Phase == TestPhase.Toolchain)
                {
                    log.AppendLine($"{step.Program} is not available to the gate's test runs{(Sandbox is null ? "" : $" as {Sandbox.User}")} (`{Describe(step)}` exited {exit}): "
                        + $"install it where the test runs' PATH finds it{(Sandbox is null ? "" : " (the launch helper's fixed PATH: /opt/homebrew/bin, /usr/local/bin, …)")}.");
                    return TestRunReport.Failed(TestRunStatus.ToolchainMissing, log.ToString());
                }
                if (step.CaptureOutput is { } capture)
                {
                    if (step.Phase == TestPhase.Build)
                    {
                        buildOutputs.Add((capture, captured));
                    }
                    else if (step.Phase == TestPhase.Test)
                    {
                        testOutputs.Add((capture, captured, exit));
                    }
                }
                if (exit != 0 && step.Phase == TestPhase.Restore)
                {
                    return TestRunReport.Failed(TestRunStatus.RestoreFailed, log.ToString());
                }
                // Every build step runs, so the errors of every project are known.
                buildFailed |= exit != 0 && step.Phase == TestPhase.Build;
            }
            if (buildFailed)
            {
                string[] roots = [workspace.Path, RealPath(workspace.Path)];
                return TestRunReport.Failed(TestRunStatus.BuildFailed, log.ToString()) with
                {
                    BuildErrors = ResultFiles(results, spec.Strategy.BuildLogPattern, maxResultFileBytes).Concat(buildOutputs)
                        .Where(l => l.Content is not null).SelectMany(l => spec.Strategy.ParseBuildErrors(l.Content!, roots)).Distinct().ToList(),
                };
            }
            // Only the reports the test steps ask their runner for (one format per run), so nothing else is read as results.
            var files = spec.Steps.Select(s => s.ResultFilePattern).OfType<string>().Distinct(StringComparer.Ordinal)
                .SelectMany(p => ResultFiles(results, p, maxResultFileBytes)).DistinctBy(f => f.Name).ToList();
            if (files.Count == 0 && testOutputs.Count == 0)
            {
                return TestRunReport.Failed(TestRunStatus.NoResults, log.ToString());
            }
            var parsed = spec.Strategy.ParseResults(files.Where(f => f.Content is not null).Select(f => new ResultFile(f.Name, f.Content!))
                .Concat(testOutputs.Where(o => o.Content is not null).Select(o => new ResultFile(o.Name, o.Content!) { ExitCode = o.ExitCode })));
            var limit = maxResultFileBytes % (1024 * 1024) == 0 ? $"{maxResultFileBytes / (1024 * 1024)} MB" : $"{maxResultFileBytes} bytes";
            var tooLarge = files.Where(f => f.Content is null).Select(f => f.Name).Concat(testOutputs.Where(o => o.Content is null).Select(o => o.Name))
                .Select(name => $"{name}: it is larger than {limit}, so it was not read");
            return new TestRunReport(TestRunStatus.Ran, parsed.Cases, log.ToString()) { UnreadableReports = [.. tooLarge, .. parsed.Unreadable] };
        }
    }

    /// <summary>
    /// <paramref name="path"/> with every symlinked component resolved (e.g. macOS <c>/var</c> → <c>/private/var</c>): the
    /// form the build tools, which see the real working directory, print in their errors. The owner's own directories only.
    /// </summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current) is { LinkTarget: not null } link && link.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }
        return current;
    }

    /// <summary>
    /// The result files, by path relative to <paramref name="directory"/> ('/'-separated), with their contents: regular files
    /// only, found without following links (the directory and everything in it were written by the tests); a file larger
    /// than <paramref name="maxBytes"/> is listed with no content (not read).
    /// </summary>
    internal static List<(string Name, string? Content)> ResultFiles(string directory, string pattern, long maxBytes)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return [];
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return info.EnumerateFiles(pattern, options)
            .Where(f => f.LinkTarget is null)
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(info.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                f.Length <= maxBytes ? File.ReadAllText(f.FullName) : null))
            .ToList();
    }

    /// <summary>
    /// Runs one step. <c>Captured</c> is its whole standard output when the step asks for it (<see cref="TestStep.CaptureOutput"/>),
    /// null when it does not or the output is larger than the result-file limit.
    /// </summary>
    private async Task<(int ExitCode, string Output, bool TimedOut, string? Captured)> RunStepAsync(string worktree, TestStep step, TimeSpan remaining, CancellationToken ct)
    {
        Process started;
        try
        {
            started = Sandbox is null ? StartDirect(worktree, step) : Sandbox.Start(worktree, step.Program, step.Args, new Dictionary<string, string>());
        }
        catch (System.ComponentModel.Win32Exception e) when (Sandbox is null)
        {
            // Unsandboxed, a program that is not installed fails at start; sandboxed, the helper exits 127 for it.
            return (127, $"{step.Program} could not be started: {e.Message}", false, null);
        }
        try
        {
            return await RunStartedAsync(started, step, remaining, ct);
        }
        finally
        {
            if (Sandbox is null)
            {
                started.Dispose();
            }
            else
            {
                // Exited, or stopped and waited for (StopAsync): disposed now. A helper still alive keeps the sandbox user's lock
                // until it is gone (sc-25988): its exit still kills every process of the worker user.
                await Sandbox.EndAsync(started, TimeSpan.Zero);
            }
        }
    }

    private async Task<(int ExitCode, string Output, bool TimedOut, string? Captured)> RunStartedAsync(Process process, TestStep step, TimeSpan remaining, CancellationToken ct)
    {
        var output = new Tail();
        var capture = step.CaptureOutput is null ? null : new Capture(maxResultFileBytes);
        var stdout = PumpAsync(process.StandardOutput, output, capture);
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
            return (-1, output.ToString(), true, null);
        }
        finally
        {
            if (Sandbox is not null)
            {
                WorkerSandbox.Stop(process); // the helper then kills whatever the step left running
            }
        }
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, output.ToString(), false, capture?.Text);
    }

    /// <summary>A step's whole standard output, up to <paramref name="maxChars"/> characters: past that it is dropped (<see cref="Text"/> null).</summary>
    private sealed class Capture(long maxChars)
    {
        private StringBuilder? _text = new();

        public string? Text => _text?.ToString();

        public void Add(string line)
        {
            if (_text is not null && _text.Length + line.Length + 1 > maxChars)
            {
                _text = null;
            }
            _text?.Append(line).Append('\n');
        }
    }

    private async Task StopAsync(Process process)
    {
        if (Sandbox is null)
        {
            process.Kill(entireProcessTree: true);
            return;
        }
        WorkerSandbox.Stop(process);
        // If it is still running as the worker user after the grace: the next helper exit (the worktree's deletion, under the same
        // hold of the lock) kills it, and until it is gone the lock is kept (WorkerSandbox.EndAsync).
        await WorkerSandbox.ExitsWithinAsync(process, StopGrace);
    }

    private static async Task PumpAsync(StreamReader reader, Tail tail, Capture? capture = null)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            tail.Add(line);
            capture?.Add(line);
        }
    }

    /// <summary>
    /// A step's command line for the run's log: an argument longer than 200 characters (the iOS simulator steps' script,
    /// sc-26132) is shown as its length, so the log's tail is the steps' output, not a script repeated.
    /// </summary>
    internal static string Describe(TestStep step) =>
        string.Join(' ', step.Args.Select(a => a.Length > 200 ? $"<{a.Length} characters>" : a).Prepend(step.Program));

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
