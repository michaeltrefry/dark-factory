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

    internal TimeSpan DirectStopGrace { get; init; } = StopGrace;
    internal Action<Process>? DirectStopForTest { get; init; }
    internal Func<Process, CancellationToken, Task>? DirectWaitForTest { get; init; }
    /// <summary>Starts an unsandboxed step's process. A test may refuse it (a program that cannot start at all).</summary>
    internal Func<ProcessStartInfo, Process?>? DirectStartForTest { get; init; }


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
        Prepare.TestEnvironment.Validate(spec.TestEnvironment);
        var workspace = await git.PrepareCommitAsync(repo, spec.Name, spec.Commit, ct);
        var cleaned = false;
        var retain = false;
        var environmentValidated = spec.TestEnvironment.Count == 0;
        try
        {
            var report = await RunInWorktreeAsync(workspace, spec, ct, () => environmentValidated = true);
            cleaned = true;
            return await CleanUpAsync(workspace.Path, spec) is { Length: > 0 } cleanup ? report with { Log = report.Log + cleanup } : report;
        }
        catch (Exception ex) when (WorkerStillRunning.IsMarked(ex))
        {
            retain = true;
            throw;
        }
        finally
        {
            if (!retain && !DirectTestProcess.Protected(workspace.Path))
            {
                if (!cleaned && environmentValidated)
                {
                    await CleanUpAsync(workspace.Path, spec); // only after termination was confirmed
                }
                if (!DirectTestProcess.Protected(workspace.Path)) await git.RemoveAsync(repo, workspace, CancellationToken.None);
            }
        }
    }

    /// <summary>How long each <see cref="TestPhase.Cleanup"/> step may take (it runs on no cancellation token, after the run's own deadline).</summary>
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The run's <see cref="TestPhase.Cleanup"/> steps, each bounded by <see cref="CleanupTimeout"/> and on no cancellation
    /// token, whatever happened before; their log (empty when there are none). Ordinary cleanup failures decide nothing,
    /// but unconfirmed process termination propagates: neither another cleanup command nor deletion may run beneath it.
    /// </summary>
    private async Task<string> CleanUpAsync(string worktree, TestRunSpec spec)
    {
        var log = new StringBuilder();
        foreach (var step in spec.Steps.Where(s => s.Phase == TestPhase.Cleanup))
        {
            if (DirectTestProcess.Protected(worktree)) break;
            try
            {
                var (exit, output, timedOut, _) = await RunStepAsync(worktree, step, CleanupTimeout, CancellationToken.None);
                log.AppendLine($"$ {Describe(step)} → {(timedOut ? "timed out" : $"exit {exit}")}").AppendLine(output);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not FactoryUnavailableException && !WorkerStillRunning.IsMarked(e))
            {
                log.AppendLine($"$ {Describe(step)} → could not be run: {e.Message}");
            }
        }
        return log.ToString();
    }

    private async Task<TestRunReport> RunInWorktreeAsync(Workspace workspace, TestRunSpec spec, CancellationToken ct, Action environmentValidated)
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
            _ = Prepare.TestEnvironment.Resolve(spec.TestEnvironment, workspace.Path); // refuse before any toolchain/recipe command
            environmentValidated();
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
                var (exit, output, timedOut, captured) = await RunStepAsync(workspace.Path, WithTestEnvironment(step, spec.TestEnvironment, workspace.Path), deadline - DateTimeOffset.UtcNow, ct);
                log.AppendLine($"$ {Describe(step)} → {(timedOut ? "timed out" : $"exit {exit}")}").AppendLine(output);
                for (var fallback = step.Fallback; !timedOut && exit == TestStep.InvalidCommandLine && fallback is not null; fallback = fallback.Fallback)
                {
                    (exit, output, timedOut, captured) = await RunStepAsync(workspace.Path, WithTestEnvironment(fallback, spec.TestEnvironment, workspace.Path), deadline - DateTimeOffset.UtcNow, ct);
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
                        .SelectMany(l => l.Content is null
                            ? new[] { new BuildError(null, 0, 0, "capture", $"{l.Name}: build diagnostics incomplete (unreadable or larger than {maxResultFileBytes} bytes)") }
                            : spec.Strategy.ParseBuildErrors(l.Content, roots)).Distinct().ToList(),
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

    internal static TestStep WithTestEnvironment(TestStep step, IReadOnlyDictionary<string, string> values, string worktree)
    {
        if (values.Count == 0 || step.Phase != TestPhase.Test) return step;
        // Result collectors also have Test phase. They must never receive repository test inputs.
        var xcode = step.Args.Count >= 2 && step.Args[0] == XcodeNewTests.GateMode && step.Args[1] == "test-without-building";
        if (!xcode && step.Program is not ("dotnet" or "cargo" or "swift")) return step;
        var environment = Prepare.TestEnvironment.Resolve(values, worktree);
        if (xcode)
            return step with { Args = step.Args.Concat(values.SelectMany(p => new[] { "--test-env", p.Key + "=" + p.Value })).ToArray() };
        return step with { Program = Prepare.PrepareSteps.EnvPath, Args = environment.Select(p => p.Key + "=" + p.Value).Concat(new[] { step.Program }).Concat(step.Args).ToArray() };
    }

    /// <summary>
    /// Runs one step. <c>Captured</c> is its standard output (<see cref="TestStep.CaptureOutput"/>), with stderr only for
    /// build diagnostics explicitly requesting <see cref="TestStep.CaptureBuildStderr"/> (never test result formats),
    /// null when it does not or the output is larger than the result-file limit.
    /// </summary>
    private async Task<(int ExitCode, string Output, bool TimedOut, string? Captured)> RunStepAsync(string worktree, TestStep step, TimeSpan remaining, CancellationToken ct)
    {
        if (step.Timeout is { } own && own < remaining)
        {
            remaining = own;
        }
        if (StepDirectory(worktree, step) is not { } directory)
        {
            return (126, $"`{step.WorkingDirectory}` is not a directory inside the worktree (every link resolved), so the step was not run", false, null);
        }
        if (step.ExpandsWorktree)
        {
            step = step with
            {
                Program = step.Program.Replace(Prepare.PrepareRecipe.WorktreePlaceholder, worktree, StringComparison.Ordinal),
                Args = step.Args.Select(a => a.Replace(Prepare.PrepareRecipe.WorktreePlaceholder, worktree, StringComparison.Ordinal)).ToList(),
            };
        }
        Process started;
        string? markerToken = null;
        try
        {
            if (Sandbox is null)
            {
                (started, markerToken) = StartDirect(directory, step, worktree);
            }
            else
            {
                started = Sandbox.Start(directory, step.Program, step.Args, new Dictionary<string, string>());
            }
        }
        catch (System.ComponentModel.Win32Exception e) when (Sandbox is null)
        {
            // Unsandboxed, a program that is not installed fails at start; sandboxed, the helper exits 127 for it.
            return (127, $"{step.Program} could not be started: {e.Message}", false, null);
        }
        var stopped = false;
        try
        {
            if (Sandbox is not null) await Sandbox.VerifyStartedAsync(started, ct);
            var result = await RunStartedAsync(started, step, remaining, worktree, ct);
            if (Sandbox is not null) Sandbox.VerifyCompleted(started);
            stopped = true;
            return result;
        }
        catch (OperationCanceledException ex) when (!WorkerStillRunning.IsMarked(ex))
        {
            stopped = Sandbox is null || await StopAsync(started);
            if (!stopped) WorkerStillRunning.Mark(ex);
            throw;
        }
        catch (Exception ex) when (Sandbox is not null && !WorkerStillRunning.IsMarked(ex))
        {
            stopped = await StopAsync(started);
            if (!stopped) WorkerStillRunning.Mark(ex);
            throw;
        }
        finally
        {
            if (Sandbox is null)
            {
                if (stopped) ReleaseOwnedMarker(worktree, markerToken ?? "");
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

    private async Task<(int ExitCode, string Output, bool TimedOut, string? Captured)> RunStartedAsync(Process process, TestStep step, TimeSpan remaining, string worktree, CancellationToken ct)
    {
        var output = new Tail();
        var capture = step.CaptureOutput is null ? null : new Capture(maxResultFileBytes);
        var errors = step.Phase == TestPhase.Build && step.CaptureBuildStderr && capture is not null ? new Capture(maxResultFileBytes) : null;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var drainCancellation = new CancellationTokenSource();
        var stdout = PumpAsync(process.StandardOutput, output, capture, drainCancellation.Token);
        var stderr = PumpAsync(process.StandardError, output, errors, drainCancellation.Token);
        var drains = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
            await drains.WaitAsync(bounded.Token);
            if (Sandbox is null) await DirectTestProcess.WaitStoppedAsync(process, bounded.Token);
        }
        catch (OperationCanceledException)
        {
            var stopped = await StopAsync(process);
            if (Sandbox is null)
            {
                // A retained pipe can belong to an escaped descendant even after the original leader died. Neither that
                // pid nor a group number alone authorizes a late signal. Keep the worktree if all pipe holders cannot exit.
                try { await drains.WaitAsync(DirectStopGrace); }
                catch (TimeoutException) { stopped = false; }
                if (!stopped) throw DirectTestProcess.Unconfirmed(worktree);
            }
            ct.ThrowIfCancellationRequested();
            return (-1, output.ToString(), true, null);
        }
        finally
        {
            drainCancellation.Cancel();
            if (Sandbox is not null)
            {
                WorkerSandbox.Stop(process); // always stop the helper, even if a drain cannot finish
            }
            try { await drains.WaitAsync(Sandbox is null ? DirectStopGrace : StopGrace); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { throw DirectTestProcess.Unconfirmed(worktree); }
        }
        var captured = capture?.Text;
        if (errors is not null)
        {
            // Deterministic stream order, not arrival order: diagnostics never depend on how the two pumps interleave.
            captured = captured is not null && errors.Text is { } stderrText ? captured + "\n" + stderrText : null;
            if (captured is not null && Encoding.UTF8.GetByteCount(captured) > maxResultFileBytes)
            {
                captured = null;
            }
        }
        return (process.ExitCode, output.ToString(), false, captured);
    }

    /// <summary>Bounded UTF-8 evidence; once oversized none of it can be used as partial proof.</summary>
    private sealed class Capture(long maxBytes)
    {
        private StringBuilder? _text = new();
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private long _bytes;

        public string? Text
        {
            get
            {
                var text = _text?.ToString();
                return text is not null && Encoding.UTF8.GetByteCount(text) <= maxBytes ? text : null;
            }
        }

        public void Add(string text)
        {
            if (_text is null) return;
            _bytes += _encoder.GetByteCount(text.AsSpan(), flush: false);
            if (_bytes > maxBytes)
            {
                _text = null;
            }
            _text?.Append(text);
        }
    }

    private async Task<bool> StopAsync(Process process)
    {
        if (Sandbox is null)
        {
            (DirectStopForTest ?? DirectTestProcess.Stop)(process);
            using var grace = new CancellationTokenSource(DirectStopGrace);
            try
            {
                // WaitAsync is intentional even if an injected/platform waiter ignores cancellation.
                await (DirectWaitForTest ?? DirectTestProcess.WaitStoppedAsync)(process, grace.Token).WaitAsync(DirectStopGrace);
                return true;
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                return false;
            }
        }
        WorkerSandbox.Stop(process);
        // An unresponsive helper keeps its slot lock through WorkerSandbox.EndAsync, as before.
        return await WorkerSandbox.ExitsWithinAsync(process, StopGrace);
    }

    private static async Task PumpAsync(StreamReader reader, Tail tail, Capture? capture = null, CancellationToken ct = default)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
        {
            capture?.Add(new string(buffer, 0, count));
            for (var n = 0; n < count; n++)
            {
                if (buffer[n] == '\n')
                {
                    tail.Add(line.ToString());
                    line.Clear();
                }
                else if (line.Length < 2000)
                {
                    line.Append(buffer[n]);
                }
            }
        }
        if (line.Length > 0) tail.Add(line.ToString());
    }

    /// <summary>
    /// The directory <paramref name="step"/> runs in (sc-26232): the worktree, or its <see cref="TestStep.WorkingDirectory"/> when
    /// that is a directory inside the worktree once every link is resolved (owner-side); null when it is not (the step is not run).
    /// </summary>
    internal static string? StepDirectory(string worktree, TestStep step)
    {
        if (step.WorkingDirectory is null or "." || Prepare.PrepareRecipe.RelativeDirectory(step.WorkingDirectory) is not { } relative)
        {
            return step.WorkingDirectory is null or "." ? worktree : null;
        }
        var full = Path.Combine(worktree, relative);
        if (!Directory.Exists(full))
        {
            return null;
        }
        var (root, real) = (RealPath(worktree), RealPath(full));
        return real.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// A step's command line for the run's log: an argument longer than 200 characters (the iOS simulator steps' script,
    /// sc-26132) is shown as its length, so the log's tail is the steps' output, not a script repeated.
    /// </summary>
    internal static string Describe(TestStep step) =>
        (step.Label is { } label ? $"[{label}{(step.WorkingDirectory is null or "." ? "" : $" in {step.WorkingDirectory}")}] " : "")
        + string.Join(' ', step.Args.Select(a => a.Length > 200 ? $"<{a.Length} characters>" : a).Prepend(step.Program));

    /// <summary>The variables an unsandboxed run keeps from the owner's environment (no secrets: never the router key or a token).</summary>
    private static readonly string[] PassThrough = ["PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "TMPDIR", "DOTNET_ROOT"];

    /// <summary>The protection marker's ownership line: the token of the run that wrote it. A marker this run did not write
    /// is never removed by it (another run's protection, or a root quarantine beside it).</summary>
    private const string MarkerOwnership = "Owned by this run: ";

    /// <summary>Writes this run's protection marker and answers its ownership token. The marker is written before the process
    /// exists: a crash between the two leaves it, and the worktree stays protected until the owner proves the process gone.</summary>
    internal static string WriteOwnedMarker(string worktree, string description)
    {
        var token = Guid.NewGuid().ToString("N");
        File.WriteAllText(DirectTestProcess.Marker(worktree), $"{description}\n{MarkerOwnership}{token}\n");
        return token;
    }

    /// <summary>Removes the protection marker only when it is the one this run wrote: another run's marker (a still
    /// unconfirmed process) and any root quarantine beside it are never touched. A marker that is already gone is fine.</summary>
    internal static void ReleaseOwnedMarker(string worktree, string token)
    {
        if (string.IsNullOrEmpty(token)) return; // no token, no ownership: never remove a marker this run did not write
        var marker = DirectTestProcess.Marker(worktree);
        try
        {
            if (!File.ReadAllText(marker).Contains(token, StringComparison.Ordinal)) return;
            File.Delete(marker);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { } // the marker cannot be read: leave it (fail closed); it is another run's protection
    }

    private (Process Process, string Token) StartDirect(string directory, TestStep step, string worktree)
    {
        var token = WriteOwnedMarker(worktree, $"Test command: {Path.GetFileName(step.Program)}; termination not yet confirmed.");
        var grouped = !OperatingSystem.IsWindows();
        var psi = grouped ? new ProcessStartInfo(DirectTestProcess.Launcher) : new ProcessStartInfo(step.Program);
        psi.WorkingDirectory = directory;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        if (grouped)
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(DirectTestProcess.Supervisor);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(step.Program);
        }
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
        // Roslyn's compiler server is separate from both MSBuild servers and node reuse. It otherwise remains in this
        // step's owned group after a successful build, making descendant-safe teardown wait out the entire run deadline.
        psi.Environment["UseSharedCompilation"] = "false";
        Process process;
        try
        {
            process = (DirectStartForTest ?? ((Func<ProcessStartInfo, Process?>)(psi => Process.Start(psi))))(psi)
                ?? throw new InvalidOperationException($"Could not start {step.Program}.");
        }
        catch
        {
            // The process never existed: only this run's own marker is removed, so the worktree is reusable. Another run's
            // protection marker and any root quarantine beside it are left alone.
            ReleaseOwnedMarker(worktree, token);
            throw;
        }
        File.AppendAllText(DirectTestProcess.Marker(worktree), $"Owned supervisor pid: {process.Id}.\n");
        process.StandardInput.Close();
        return (process, token);
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
