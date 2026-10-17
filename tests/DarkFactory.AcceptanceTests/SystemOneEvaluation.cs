using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.AcceptanceTests;

// Held-out data is authored independently. These anchors bind the author's commit-equivalent bytes, not a timestamp
// supplied by a caller. A new corpus requires independent labeling and a new identity, never tuning against answers.
public sealed class EvaluationCorpus
{
    public const string AuthorCommit = "80e21ab563ba67ba82ad859675f8cd6217adf5a1";
    public const string FreezeSha = "dd1d7e2a6dbccf2b33df75cf8a12dd0140bc00cbdcb7634ac6194415e00f3965";
    public const string ManifestSha = "1972fafabdbab74de63e81cc8f1632f736d79e32e59befc7c126b082850504b5";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public required string Root { get; init; }
    public string Identity => ManifestSha;
    public required string RepositorySha { get; init; }
    public required IReadOnlyList<EvaluationInput> Reports { get; init; }
    public required IReadOnlyList<EvaluationLabel> Labels { get; init; }

    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static EvaluationCorpus Load(string root)
    {
        try
        {
            root = Path.GetFullPath(root);
            var files = Inventory(root).Order(StringComparer.Ordinal).ToArray();
            var freezeBytes = File.ReadAllBytes(Path.Combine(root, "freeze.json"));
            Require(Hash(freezeBytes) == FreezeSha);
            using var freeze = JsonDocument.Parse(freezeBytes);
            var manifest = freeze.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Require(files.SequenceEqual(manifest.Select(f => f.GetProperty("path").GetString()!).Append("freeze.json").Order(StringComparer.Ordinal)));
            var digest = new StringBuilder();
            foreach (var file in manifest.OrderBy(f => f.GetProperty("path").GetString(), StringComparer.Ordinal))
            {
                var path = file.GetProperty("path").GetString()!;
                var bytes = File.ReadAllBytes(Path.Combine(root, path));
                var sha = Hash(bytes);
                Require(bytes.Length == file.GetProperty("bytes").GetInt32() && sha == file.GetProperty("sha256").GetString());
                digest.Append(sha).Append("  ").Append(path).Append('\n');
            }
            Require(Hash(digest.ToString()) == ManifestSha);
            using var reports = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "reports.json")));
            using var labels = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "labels.json")));
            Require(reports.RootElement.GetProperty("schemaId").GetString() == "dark-factory.systemone.heldout.reports.v1");
            Require(labels.RootElement.GetProperty("schemaId").GetString() == "dark-factory.systemone.heldout.labels.v1");
            var inputs = reports.RootElement.GetProperty("reports").Deserialize<List<EvaluationInput>>(Json)!;
            var expected = labels.RootElement.GetProperty("labels").Deserialize<List<EvaluationLabel>>(Json)!;
            Require(inputs.Count == 15 && inputs.Select(r => r.CaseId).Distinct().Count() == 15);
            Require(inputs.Select(r => r.CaseId).Order().SequenceEqual(expected.Select(l => l.CaseId).Order()));
            Require(IssueClassifier.Labels.All(c => expected.Count(l => l.ExpectedCategory == c) == 3));
            Require(inputs.Any(r => r.Title.Length + r.Body.Length > 4096));
            Require(new[] { "ambiguous", "misleading-duplicate-claim", "prompt-injection", "oversized-input" }.All(t => inputs.Any(r => r.Tags.Contains(t))));
            Require(expected.All(l => l.AcceptableAuthoritativeRoutes.Count > 0 && l.AcceptableAuthoritativeRoutes.All(r => Enum.TryParse<IssueRoute>(r, out _))));
            return new EvaluationCorpus { Root = root, Reports = inputs, Labels = expected,
                RepositorySha = freeze.RootElement.GetProperty("repositorySnapshot").GetProperty("sha256").GetString()! };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("The independently frozen synthetic corpus failed its integrity/provenance guard.", ex);
        }
    }

    public async Task<string> MaterializeRepositoryAsync(string destination, CancellationToken ct)
    {
        Load(Root); // Do not materialize data that changed after the original seal check.
        if (Directory.Exists(destination) || File.Exists(destination))
        { throw new InvalidDataException("The evaluation snapshot destination must be new."); }
        Directory.CreateDirectory(destination);
        var source = Path.Combine(Root, "repository");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        await Git(["init", "--initial-branch=main"]);
        await Git(["add", "."]);
        await Git(["-c", "user.name=Synthetic Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "Frozen synthetic repository"]);
        return (await Git(["rev-parse", "HEAD"])).Trim();

        Task<string> Git(string[] arguments)
        {
            var (env, args) = OwnerGit.Isolate(new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = "2026-10-16T00:00:00Z", ["GIT_COMMITTER_DATE"] = "2026-10-16T00:00:00Z",
            }, arguments);
            return GitWorkspace.RunGitAsync(destination, env, args, ct);
        }
    }

    private static IEnumerable<string> Inventory(string root)
    {
        Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            Require((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0);
            if (Directory.Exists(entry))
            {
                foreach (var nested in Inventory(entry)) { yield return Path.GetFileName(entry) + "/" + nested; }
            }
            else { yield return Path.GetFileName(entry); }
        }
    }
    private static void Require(bool valid)
    {
        if (!valid) { throw new InvalidDataException("Frozen corpus inventory, digest, provenance or coverage differs from the independent author's seal."); }
    }
}

public sealed record EvaluationPermissions(string IssueRepository, string AuthorPermission, string TargetRepository,
    string AuthorPermissionOnTarget, IReadOnlyList<string> WatchedRepositories);
public sealed record EvaluationInput(string CaseId, string Title, string Body, IReadOnlyList<string> Tags,
    EvaluationPermissions PermissionContext, string RepositoryRef);
public sealed record EvaluationLabel(string CaseId, string ExpectedCategory, IReadOnlyList<string> AcceptableAuthoritativeRoutes);

// No raw prompt, issue body, rationale, model answer or exception message may enter an observation/report.
public sealed record EvaluationObservation(string CaseId, string Mode, double DurationMs, long ClassifierElapsedMs,
    string? Route, bool Failed, string? ClassifierStatus, string? Category, long? ClassifierInputTokens,
    long? ClassifierOutputTokens, decimal? TriageCostUsd, IReadOnlyDictionary<string, int> ServedModelMessageCounts,
    string PromptSha, string RepositorySha, string? FailureCode, decimal? ClassifierCostUsd = null,
    long? TriageRequestCount = null, string? ClassifierModelSha = null,
    string? ClassifierPromptSha = null, string? ClassifierVersion = null,
    long? ClassifierRequestCount = null, string? ClassifierSessionId = null, string? ClassifierRequestedModelSha = null);
public interface IEvaluationArm
{
    // Checkpoint BEFORE destroying the arm's isolated ledger, including when cleanup later fails.
    Task RunAsync(EvaluationInput report, string mode, Func<EvaluationObservation, Task> completed, CancellationToken ct);
}
public sealed class DelegateEvaluationArm(Func<EvaluationInput, string, CancellationToken, Task<EvaluationObservation>> run) : IEvaluationArm
{
    public async Task RunAsync(EvaluationInput report, string mode, Func<EvaluationObservation, Task> completed, CancellationToken ct) =>
        await completed(await run(report, mode, ct));
}

public sealed record EvaluationCampaign(string Status, string CorpusIdentity, string AuthorCommit, int ExpectedArms,
    IReadOnlyList<EvaluationObservation> Observations, EvaluationReport? Summary);

public static class SystemOneEvaluation
{
    public static async Task<EvaluationReport> RunAsync(string root, Func<EvaluationCorpus, IEvaluationArm> createRunner, CancellationToken ct,
        Func<IReadOnlyList<EvaluationObservation>, EvaluationReport?, Task>? checkpoint = null, Action? checkCancellation = null)
    {
        var corpus = EvaluationCorpus.Load(root); // MUST precede client construction or any classifier/worker output.
        var observations = new List<EvaluationObservation>();
        // Even a setup failure or skip leaves a nonempty, explicitly incomplete evidence file.
        if (checkpoint is not null) { await checkpoint(observations, null); }
        CheckCancellation();
        var runner = createRunner(corpus);
        void CheckCancellation() { checkCancellation?.Invoke(); ct.ThrowIfCancellationRequested(); }
        for (var i = 0; i < corpus.Reports.Count; i++)
        {
            foreach (var mode in i % 2 == 0 ? new[] { "disabled", "advisory" } : new[] { "advisory", "disabled" })
            {
                CheckCancellation();
                try
                {
                    await runner.RunAsync(corpus.Reports[i], mode, async observation =>
                    {
                        observations.Add(observation);
                        // Not the campaign token: an interrupt must not cancel evidence persistence.
                        if (checkpoint is not null) { await checkpoint(observations, null); }
                    }, ct);
                }
                catch (Xunit.Sdk.SkipException)
                {
                    // Live opt-in gating precedes this loop. A missing arm prerequisite is not a green skipped campaign.
                    // Do not include the original exception: shared harness diagnostics may contain URLs/secrets/bodies.
                    throw new InvalidOperationException("Evaluation arm prerequisite unavailable; campaign incomplete (no automatic retry).");
                }
            }
        }
        CheckCancellation();
        var report = EvaluationReport.Create(corpus, observations);
        if (checkpoint is not null) { await checkpoint(observations, report); }
        return report;
    }

    public static async Task<EvaluationReport> RunToFileAsync(string root, string output,
        Func<EvaluationCorpus, IEvaluationArm> createRunner, CancellationToken ct, string? cancelFile = null, string? workerWorkRoot = null)
    {
        var corpus = EvaluationCorpus.Load(root);
        output = Path.GetFullPath(output);
        if (output == Path.GetFullPath(root) || output.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new InvalidDataException("Evidence must be outside the frozen corpus."); }
        // Artifact-location rules hold for every campaign, cancellation flag or not; only the flag's
        // identity is conditional on a flag existing. Checked before any reservation or client work.
        EvaluationCancellation.Validate(output, root, workerWorkRoot);
        EvaluationCancellation.ValidateFlag(cancelFile, output);
        await using var cancellation = new EvaluationCancellation(cancelFile, ct);
        // Reserve once, never overwrite an earlier campaign. Checkpoints replace it atomically in the same directory.
        using (new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return await RunAsync(root, createRunner, cancellation.Token, (observations, summary) =>
        {
            var evidence = new EvaluationCampaign(summary is null ? "incomplete" : "complete", corpus.Identity,
                EvaluationCorpus.AuthorCommit, corpus.Reports.Count * 2, observations, summary);
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, evidence, new JsonSerializerOptions { WriteIndented = true });
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, output, overwrite: true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
            return Task.CompletedTask;
        }, cancellation.ThrowIfCancellationRequested);
    }
}

// Evaluation-only, one explicitly named flag per evidence file; never reads its contents or signals a process.
internal sealed class EvaluationCancellation : IAsyncDisposable
{
    private readonly string? file;
    private readonly CancellationTokenSource linked;
    private readonly CancellationTokenSource stop = new();
    private readonly Task watcher;
    public CancellationToken Token => linked.Token;

    public EvaluationCancellation(string? file, CancellationToken caller)
    {
        this.file = file is null ? null : Path.GetFullPath(file);
        linked = CancellationTokenSource.CreateLinkedTokenSource(caller);
        watcher = file is null ? Task.CompletedTask : WatchAsync();
    }

    // Runs for every campaign, whatever the cancellation flag: an evidence file under the scoring corpus,
    // the worker work root, git metadata or a symlinked directory is refused before anything is reserved.
    public static void Validate(string output, string corpusRoot, string? workerWorkRoot)
    {
        // The live composition supplies its configured work root: all isolated ledgers' synthetic snapshots,
        // clones and triage ReadRule worktrees live below it. The owner's checkout is NOT a worker context.
        if (Within(output, PhysicalRoot(new DirectoryInfo(corpusRoot)))
            || workerWorkRoot is not null && Within(output, PhysicalRoot(new DirectoryInfo(workerWorkRoot))))
        { throw new InvalidDataException("Cancellation artifacts must be outside the scoring corpus and worker work root."); }
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(output)!); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null || directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("Cancellation artifacts must be outside git metadata and symlinked directories."); }
        }
    }

    // Only the flag's identity depends on a flag existing; no flag means no watcher, never a skipped location check.
    public static void ValidateFlag(string? file, string output)
    {
        if (file is null) { return; } // No global/default flag; existing campaigns are unaffected.
        if (!Path.IsPathFullyQualified(file) || Path.GetFullPath(file) != output + ".cancel")
        { throw new InvalidDataException("Cancellation file must be the absolute evidence path plus .cancel."); }
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Resolve protected-root aliases too; artifacts themselves reject every symlink ancestor below.
    private static string PhysicalRoot(DirectoryInfo directory) => directory.LinkTarget is not null
        ? directory.ResolveLinkTarget(returnFinalTarget: true)!.FullName
        : directory.Parent is null ? directory.FullName : Path.Combine(PhysicalRoot(directory.Parent), directory.Name);

    private void Refresh()
    {
        // Existence only: a directory or dangling symlink at the flag also stops safely.
        if (file is not null && (Path.Exists(file) || new FileInfo(file).LinkTarget is not null)) { linked.Cancel(); }
    }

    public void ThrowIfCancellationRequested() { Refresh(); Token.ThrowIfCancellationRequested(); }

    private async Task WatchAsync()
    {
        try
        {
            while (!Token.IsCancellationRequested)
            {
                Refresh();
                await Task.Delay(TimeSpan.FromMilliseconds(100), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await watcher; }
        finally { stop.Dispose(); linked.Dispose(); }
    }
}

// Evaluation-only ownership. The parent is owner-controlled; workers have fully unwound before disposal.
// Like GitWorkspace's owner-side fallback, Directory.Delete(recursive: true) unlinks directory symlinks,
// never traversing their targets. No extra sandbox helper (and hence no uid sweep) is launched here.
public sealed class EvaluationWorkRoot
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<IDisposable> RetainedLocks = new();
    private bool workerMayBeRunning;
    private int active;
    private readonly int workerSlots;
    public string Path { get; }
    private EvaluationWorkRoot(string path, int workerSlots) => (Path, this.workerSlots) = (path, workerSlots);

    public static EvaluationWorkRoot Create(string parent, string name, int workerSlots = 1)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
        { throw new InvalidDataException("Evaluation root must be a unique child directory."); }
        if (workerSlots is < 1 or > WorkerSlots.MaxSlots) { throw new ArgumentOutOfRangeException(nameof(workerSlots)); }
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(parent), name);
        RequireNoLinkedAncestors(path);
        if (System.IO.Path.Exists(path)) { throw new IOException("Evaluation root already exists; refusing to adopt it."); }
        Directory.CreateDirectory(path);
        return new EvaluationWorkRoot(path, workerSlots);
    }

    private static void RequireNoLinkedAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null)
            { throw new InvalidDataException("Evaluation root or ancestor is a symlink; refusing cleanup/creation."); }
        }
    }

    public async Task RunAsync(Func<Task> run)
    {
        Interlocked.Increment(ref active);
        try { await run(); }
        catch (Exception ex) when (DarkFactory.Orchestrator.Worker.WorkerStillRunning.IsMarked(ex))
        {
            workerMayBeRunning = true;
            throw;
        }
        finally { Interlocked.Decrement(ref active); }
    }

    // Guard the runner itself: IssueIntake can record/swallow a triage failure before it reaches the arm.
    public ITriageRunner Guard(ITriageRunner runner) => new GuardedRunner(this, runner);
    private sealed class GuardedRunner(EvaluationWorkRoot root, ITriageRunner inner) : ITriageRunner
    {
        public async Task<WorkerResult> RunAsync(DarkFactory.Orchestrator.Ledger.WorkItem item, RepoRef repo, string prompt,
            Func<string, CancellationToken, Task> onSession, Func<string, string, CancellationToken, Task> onTaint, CancellationToken ct)
        {
            WorkerResult? result = null;
            await root.RunAsync(async () => { result = await inner.RunAsync(item, repo, prompt, onSession, onTaint, ct); });
            return result!;
        }
    }

    public async Task DisposeAsync(Func<Task> dropLedger, IDisposable? heldLocks)
    {
        if (workerMayBeRunning || Volatile.Read(ref active) != 0)
        {
            // The unsafe arm cannot continue. Keep the real machine locks strongly rooted until this process exits;
            // operator inspection is required, rather than deleting under a worker or starting another sweeping helper.
            if (heldLocks is not null) { RetainedLocks.Add(heldLocks); }
            throw new InvalidOperationException($"Evaluation worker may still be running; retained ledger, locks and work root {Path}. Stop/inspect the runner before manual cleanup.");
        }
        var stopped = new List<IDisposable>();
        try
        {
            // Exception markers alone are insufficient: probe cancellation can be unmarked, and recorder cleanup
            // can replace a worker's exception. EndAsync/KeepUntil retains the actual isolated slot lock in both cases.
            RequireNoLinkedAncestors(Path);
            for (var slot = 1; slot <= workerSlots; slot++)
            {
                var lockPath = WorkerLock.For(Path, slot).Path;
                RequireNoLinkedAncestors(System.IO.Path.GetDirectoryName(lockPath)!);
                if (new FileInfo(lockPath).LinkTarget is not null) { throw new InvalidDataException("Linked evaluation lock file."); }
                stopped.Add(WorkerLock.Acquire(Path, slot)); // Nonblocking; no process/helper/probe.
            }
        }
        catch (Exception ex)
        {
            stopped.ForEach(held => held.Dispose());
            if (heldLocks is not null) { RetainedLocks.Add(heldLocks); }
            throw new InvalidOperationException($"Evaluation sandbox quiescence not established ({ex.GetType().Name}); retained ledger, machine locks and work root {Path}. Inspect the runner before manual cleanup.");
        }
        try
        {
            try { await dropLedger(); }
            finally
            {
                RequireNoLinkedAncestors(Path);
                if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
            }
        }
        finally
        {
            stopped.ForEach(held => held.Dispose());
            heldLocks?.Dispose();
        }
    }
}

// Evaluation-only prerequisites. No board, GitHub App, gate or review credentials are read.
// The caller transfers the held machine sandbox locks to the isolated ledger on success.
public static class EvaluationSetup
{
    public static async Task RequireRouterAsync(Func<CancellationToken, Task> probe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await probe(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"Evaluation router prerequisite unreachable ({ex.GetType().Name}); campaign incomplete (no automatic retry).");
        }
    }

    public static async Task<T> StartAsync<T>(FactoryOptions options, Func<IDisposable> acquireLocks,
        Func<T, Task> sandboxProbe, Func<IDisposable, Task<T>> createIsolatedLedger, CancellationToken ct = default) where T : IAsyncDisposable
    {
        ct.ThrowIfCancellationRequested();
        if (options.WorkerSandbox is null)
        { throw new InvalidOperationException("The evaluation requires the actual worker sandbox."); }
        _ = options.RouterKey;
        var held = acquireLocks();
        T ledger;
        try { ledger = await createIsolatedLedger(held); }
        catch { held.Dispose(); throw; }
        try
        {
            ct.ThrowIfCancellationRequested();
            await sandboxProbe(ledger);
            ct.ThrowIfCancellationRequested();
            return ledger;
        }
        catch { await ledger.DisposeAsync(); throw; }
    }
}

public sealed record CategoryQuality(string Category, int Total, int Answered, int Correct, double Accuracy,
    IReadOnlyDictionary<string, int> Confusion);
public sealed record ArmSummary(int Count, double DurationMs, int RoutingErrors, int Failures, double FailureRate,
    decimal? TriageCostUsd, int MeasuredTriageCosts);
public sealed record EvaluationPair(string CaseId, EvaluationObservation Baseline, EvaluationObservation Advisory,
    double DurationDeltaMs, bool BaselineRoutingError, bool AdvisoryRoutingError, bool? ServedModelsDiffer);
public sealed record EvaluationReport(string CorpusIdentity, string AuthorCommit, string FreezeSha, string RepositorySha,
    IReadOnlyList<EvaluationPair> Pairs, IReadOnlyList<CategoryQuality> Classification, ArmSummary Baseline, ArmSummary Advisory,
    double DurationDeltaMs, int ClassifierUnavailable, int ClassifierSkipped, decimal? AdditionalInferenceCostUsd,
    bool OptimizationDemonstrated, string Limitation, bool NecessaryPerformanceGoalMet,
    double ClassifierFailureRate, string WorkerModelClass = "low", int TriageDeadlineSeconds = 120,
    int ClassifierDeadlineSeconds = 5, int ClassifierInputBudgetChars = 4096, int TriageMaxTurns = 12)
{
    public static EvaluationReport Create(EvaluationCorpus corpus, IReadOnlyList<EvaluationObservation> observations)
    {
        var pairs = corpus.Reports.Select(input =>
        {
            var baseline = observations.Single(o => o.CaseId == input.CaseId && o.Mode == "disabled");
            var advisory = observations.Single(o => o.CaseId == input.CaseId && o.Mode == "advisory");
            if (baseline.RepositorySha != advisory.RepositorySha)
            { throw new InvalidDataException("A paired evaluation must use the identical repository snapshot in both arms."); }
            var accepted = corpus.Labels.Single(l => l.CaseId == input.CaseId).AcceptableAuthoritativeRoutes;
            return new EvaluationPair(input.CaseId, baseline, advisory, advisory.DurationMs - baseline.DurationMs,
                baseline.Failed || !accepted.Contains(baseline.Route!), advisory.Failed || !accepted.Contains(advisory.Route!),
                baseline.ServedModelMessageCounts.Count == 0 || advisory.ServedModelMessageCounts.Count == 0 ? null
                    : !baseline.ServedModelMessageCounts.OrderBy(m => m.Key, StringComparer.Ordinal)
                        .SequenceEqual(advisory.ServedModelMessageCounts.OrderBy(m => m.Key, StringComparer.Ordinal)));
        }).ToList();
        var quality = IssueClassifier.Labels.Select(category =>
        {
            var rows = pairs.Where(p => corpus.Labels.Single(l => l.CaseId == p.CaseId).ExpectedCategory == category).Select(p => p.Advisory).ToList();
            var correct = rows.Count(o => o.ClassifierStatus == "completed" && o.Category == category);
            return new CategoryQuality(category, rows.Count, rows.Count(o => o.ClassifierStatus == "completed"), correct,
                (double)correct / rows.Count, rows.GroupBy(o => o.Category ?? "unavailable").ToDictionary(g => g.Key, g => g.Count()));
        }).ToList();
        ArmSummary Summary(bool advisory)
        {
            var rows = pairs.Select(p => advisory ? p.Advisory : p.Baseline).ToList();
            return new(rows.Count, rows.Sum(o => o.DurationMs), pairs.Count(p => advisory ? p.AdvisoryRoutingError : p.BaselineRoutingError),
                rows.Count(o => o.Failed), (double)rows.Count(o => o.Failed) / rows.Count,
                rows.All(o => o.TriageCostUsd.HasValue) ? rows.Sum(o => o.TriageCostUsd!.Value) : null, rows.Count(o => o.TriageCostUsd.HasValue));
        }
        var b = Summary(false);
        var a = Summary(true);
        var addedCost = pairs.All(p => p.Advisory.ClassifierCostUsd.HasValue && p.Advisory.TriageCostUsd.HasValue && p.Baseline.TriageCostUsd.HasValue)
            ? pairs.Sum(p => p.Advisory.ClassifierCostUsd!.Value + p.Advisory.TriageCostUsd!.Value - p.Baseline.TriageCostUsd!.Value) : (decimal?)null;
        // This mechanism corpus and a single pair/case cannot establish a production optimization, even if its raw times improve.
        return new(corpus.Identity, EvaluationCorpus.AuthorCommit, EvaluationCorpus.FreezeSha, corpus.RepositorySha, pairs, quality, b, a,
            a.DurationMs - b.DurationMs, pairs.Count(p => p.Advisory.ClassifierStatus is not ("completed" or "skipped_oversize")),
            pairs.Count(p => p.Advisory.ClassifierStatus == "skipped_oversize"), addedCost, false,
            "Synthetic mechanism coverage, three/category, one counterbalanced pair/case; not population accuracy or a statistically established speedup. " +
            "Category accuracy is correct/all labeled cases (unavailable/skipped included); Answered reports coverage. Classifier failure rate is unavailable/all cases; oversize skips are separate. " +
            "Class-based serving may differ: hashed model identities with deduplicated assistant-message counts identify this confound. CodeGraph disabled in both arms; only the pinned synthetic repository is available. " +
            "Duration includes classifier, sandbox setup/checkout, triage, worker session accounting and deterministic routing; classifier cost-report reads occur afterwards. " +
            "Cost is router-reported USD via separately correlated classifier/worker sessions, not a hardware-energy estimate. Missing telemetry is null, and zero requires a recorded request. " +
            "One attempt per arm, no immediate triage retry, two-minute/12-turn triage cap in BOTH arms (a cap failure remains a failure). " +
            "Faster without added routing errors is necessary, not sufficient, to recommend advisory deployment.",
            a.DurationMs < b.DurationMs && a.RoutingErrors <= b.RoutingErrors && a.Failures <= b.Failures,
            (double)pairs.Count(p => p.Advisory.ClassifierStatus is not ("completed" or "skipped_oversize")) / pairs.Count);
    }
}
