using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// One file the PR changes (<c>git diff --name-status -M base...head</c>): <see cref="Status"/> is git's letter (A, M, D,
/// R, C, T); <see cref="OldPath"/> is null for an added file, <see cref="NewPath"/> null for a deleted one.
/// </summary>
public sealed record ChangedFile(char Status, string? OldPath, string? NewPath)
{
    public bool Deleted => NewPath is null;

    public IEnumerable<string> Paths => new[] { OldPath, NewPath }.OfType<string>().Distinct(StringComparer.Ordinal);
}

/// <summary>One executed test case (a fact, or one data row of a theory): its display name and outcome (passed, failed, skipped).</summary>
public sealed record TestCaseResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("outcome")] string Outcome)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>What a failed command of a test run means: restore and build are told apart from the tests' own failures.</summary>
public enum TestPhase
{
    Restore,
    Build,
    Test,
}

/// <summary>One command of a test run, run in the worktree's root.</summary>
public sealed record TestStep(TestPhase Phase, string Program, IReadOnlyList<string> Args);

/// <summary>
/// One sandboxed test run: <see cref="Commit"/> checked out in a fresh throwaway worktree named <see cref="Name"/>; with
/// <see cref="OverlayFrom"/>, the <see cref="OverlayPaths"/> of that commit written over it, <see cref="DeletePaths"/>
/// removed and the <see cref="Replacements"/> written in place of those files (the base run: the PR's test files applied
/// to the base commit; its retry: the same without the members that did not compile there); then <see cref="Steps"/>, and
/// the files matching <see cref="INewTestStrategy.ResultFilePattern"/> (after a failed build,
/// <see cref="INewTestStrategy.BuildLogPattern"/>) under <see cref="ResultsDirectory"/> parsed by <see cref="Strategy"/>.
/// <see cref="ResultsDirectory"/> is a fresh random name per run (<see cref="NewTestsCheck.NewResultsDirectory"/>), so no
/// commit can have put files there; a worktree that already has it fails the run.
/// </summary>
public sealed record TestRunSpec(
    string Name,
    string Commit,
    string? OverlayFrom,
    IReadOnlyList<string> OverlayPaths,
    IReadOnlyList<string> DeletePaths,
    IReadOnlyList<TestStep> Steps,
    INewTestStrategy Strategy,
    string ResultsDirectory)
{
    /// <summary>Overlaid files written with this content instead of the <see cref="OverlayFrom"/> commit's, by path.</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public enum TestRunStatus
{
    /// <summary>Restored, built and the tests ran (some may have failed): <see cref="TestRunReport.Results"/> holds them.</summary>
    Ran,
    /// <summary>The restore failed: an environment failure, never evidence about the tests.</summary>
    RestoreFailed,
    /// <summary>The code did not build; <see cref="TestRunReport.BuildErrors"/> says where (on the base, <see cref="BuildFailure"/>).</summary>
    BuildFailed,
    /// <summary>The run did not finish within its timeout.</summary>
    TimedOut,
    /// <summary>The tests ran but left no result file.</summary>
    NoResults,
}

/// <summary>
/// One error of a failed build: its file (repository-relative; null when it names no file inside the worktree, e.g. an
/// MSBuild or NuGet error), 1-based line and column, code (e.g. <c>CS0117</c>; empty when it has none) and message.
/// </summary>
public sealed record BuildError(string? Path, int Line, int Column, string Code, string Message)
{
    public override string ToString() => Path is null ? $"{Code} {Message}".Trim() : $"{Path}({Line},{Column}): {Code} {Message}";
}

/// <summary>What one run produced: its status, each test's cases by test id, and the tail of its output.</summary>
public sealed record TestRunReport(TestRunStatus Status, IReadOnlyDictionary<string, IReadOnlyList<TestCaseResult>> Results, string Log)
{
    /// <summary>A <see cref="TestRunStatus.BuildFailed"/> run's errors, read from the build's error logs.</summary>
    public IReadOnlyList<BuildError> BuildErrors { get; init; } = [];

    public static TestRunReport Failed(TestRunStatus status, string log) => new(status, new Dictionary<string, IReadOnlyList<TestCaseResult>>(), log);
}

/// <summary>
/// The I/O the check needs, all on the gate's own clone of the target repo (never the PR's word): the PR's changed files,
/// a commit's file list and file contents, and a sandboxed test run. <see cref="SandboxTestRunner"/> in production; tests
/// fake it.
/// </summary>
public interface IGateTestRunner
{
    /// <summary>The files the PR changes, <c>base...head</c> with renames detected (fetches first).</summary>
    Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct);

    /// <summary>Every file path in <paramref name="sha"/>'s tree.</summary>
    Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct);

    /// <summary><paramref name="path"/>'s content at <paramref name="sha"/>, or null when it has no such file.</summary>
    Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct);

    /// <summary>Runs <paramref name="spec"/> sandboxed, exactly as a worker runs (the worker user, a throwaway worktree, no secrets).</summary>
    Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct);
}

/// <summary>A repository's files at the base and head commits, read through the runner.</summary>
public sealed class TestSource(IGateTestRunner runner, RepoRef repo, string baseSha, string headSha)
{
    private readonly Dictionary<string, IReadOnlyList<string>> _files = new(StringComparer.Ordinal);

    public string BaseSha => baseSha;

    public string HeadSha => headSha;

    public async Task<IReadOnlyList<string>> FilesAsync(string sha, CancellationToken ct)
    {
        if (!_files.TryGetValue(sha, out var files))
        {
            _files[sha] = files = await runner.FilesAsync(repo, sha, ct);
        }
        return files;
    }

    public Task<string?> ReadAsync(string sha, string path, CancellationToken ct) => runner.ReadAsync(repo, sha, path, ct);
}

/// <summary>
/// What one test stack found in the PR: the changed files that are its test files (<see cref="TestFiles"/>: the base run
/// applies them), the ids of the tests the PR adds (<see cref="Tests"/>), and additions it cannot isolate as tests
/// (<see cref="Unisolable"/>, each with why: the check cannot run them, so it fails, E2). <see cref="Projects"/> names the
/// project (at the head) that holds each new test: the runs build and test just those projects.
/// </summary>
public sealed record NewTestSet(IReadOnlyList<ChangedFile> TestFiles, IReadOnlyList<string> Tests, IReadOnlyList<string> Unisolable)
{
    public static readonly NewTestSet None = new([], [], []);

    /// <summary>Each new test's project file, by test id.</summary>
    public IReadOnlyDictionary<string, string> Projects { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// What a failed base build (the base with the PR's test files) says about the new tests, by compiler evidence only: the
/// new tests a compiler error lies in (<see cref="NotBuilt"/>: in the test method's own declaration, or the header of a
/// type that contains it — such a test cannot pass on the base, so it counts as failing there); errors that say nothing
/// about the tests (<see cref="Unattributable"/>: not a compiler error, or in a file the PR's test files did not apply —
/// the base run is then no evidence, an error); compiler errors in the applied files outside any member
/// (<see cref="Unremovable"/>: no retry can build without them, so the other new tests stay unproven); and, for the one
/// retry, the applied files without every member and <c>using</c> that holds an error (<see cref="Replacements"/>;
/// <see cref="Removed"/> describes each).
/// </summary>
public sealed record BuildFailure(
    IReadOnlyList<string> NotBuilt,
    IReadOnlyList<string> Unattributable,
    IReadOnlyList<string> Unremovable,
    IReadOnlyList<string> Removed,
    IReadOnlyDictionary<string, string> Replacements);

/// <summary>
/// The base side of one stack's check: the base run (<see cref="Run"/>), and when it did not build, its
/// <see cref="BuildFailure"/> (<see cref="First"/>) and the retry without the members that did not compile
/// (<see cref="Retry"/>, and its own <see cref="RetryFailure"/> when that did not build either).
/// </summary>
public sealed record BaseEvidence(TestRunReport Run, BuildFailure? First = null, TestRunReport? Retry = null, BuildFailure? RetryFailure = null)
{
    /// <summary>The new tests a compiler error was attributed to, in the base run or its retry.</summary>
    public IEnumerable<string> NotBuilt => (First?.NotBuilt ?? []).Concat(RetryFailure?.NotBuilt ?? []);
}

/// <summary>
/// One language/framework's way to find the tests a PR adds (deterministically, from the files at the base and the head)
/// and to run just those tests. <see cref="XunitNewTests"/> is .NET/xUnit; a stack none claims is
/// <see cref="NewTestsOutcome.Unsupported"/>.
/// </summary>
public interface INewTestStrategy
{
    string Name { get; }

    /// <summary>
    /// Whether this strategy reads files like <paramref name="path"/> (e.g. every <c>.cs</c> file): it then decides itself
    /// whether such a file holds tests it cannot run (<see cref="NewTestSet.Unisolable"/>), instead of
    /// <see cref="NewTestsCheck.LooksLikeTest"/>.
    /// </summary>
    bool Understands(string path);

    /// <summary>The result files a run leaves under its <see cref="TestRunSpec.ResultsDirectory"/> (e.g. <c>*.trx</c>).</summary>
    string ResultFilePattern { get; }

    /// <summary>The build-error logs a run's build steps leave under its <see cref="TestRunSpec.ResultsDirectory"/>.</summary>
    string BuildLogPattern { get; }

    Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct);

    /// <summary>
    /// The commands that restore, build and run exactly <paramref name="tests"/> (in their <paramref name="projects"/>) at
    /// <paramref name="commit"/>: every restore, then every build, then every test command, writing results and build-error
    /// logs under <paramref name="resultsDirectory"/> (relative to the worktree root).
    /// </summary>
    Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects,
        string resultsDirectory, CancellationToken ct);

    /// <summary>Each test's cases (by test id) from the run's result files.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<TestCaseResult>> ParseResults(IEnumerable<string> resultFiles);

    /// <summary>The errors in one build-error log; files under one of <paramref name="roots"/> (the worktree) are made relative to it.</summary>
    IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots);

    /// <summary>
    /// Attributes a failed base build's <paramref name="errors"/> to the new <paramref name="tests"/>, given the content of
    /// every file the run applied (<paramref name="applied"/>, by path; deterministic, nothing compiled).
    /// </summary>
    BuildFailure ExplainBuildFailure(IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests);
}

/// <summary>The check's outcomes. Anything but <see cref="Pass"/> fails the check (the typed outcome maps it to <c>gate_rejected</c>).</summary>
public static class NewTestsOutcome
{
    /// <summary>Every new test failed (or could not build) on the base and passed on the head.</summary>
    public const string Pass = "pass";
    /// <summary>
    /// A new test already passes on the base (it checks nothing), is skipped there, could not be shown to fail there (the
    /// base does not build with it, for a reason outside its own declaration), or does not pass on the head.
    /// </summary>
    public const string Rejected = "rejected";
    /// <summary>The PR adds no test, in a tier that requires the check.</summary>
    public const string NoTests = "no-tests";
    /// <summary>The PR adds tests in a stack no strategy supports, or ones a strategy cannot isolate: the check cannot run.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>
    /// A run could not produce evidence (restore failed, timed out, no results, a new test not executed, a base build error
    /// outside the PR's test files or not from the compiler, a git or sandbox failure).
    /// </summary>
    public const string Error = "error";
}

/// <summary>One new test's results on the base (with the PR's test files) and on the head.</summary>
public sealed record NewTestResult(
    [property: JsonPropertyName("test")] string Test,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base_cases")] IReadOnlyList<TestCaseResult> BaseCases,
    [property: JsonPropertyName("head_cases")] IReadOnlyList<TestCaseResult> HeadCases);

/// <summary>
/// The check's result for one base/head pair, recorded in the ledger (<c>new-tests</c> checkpoint) before the gate uses it
/// (E5): the outcome, why, the run statuses and every new test's per-case results on both commits.
/// </summary>
public sealed record NewTestsResult(
    [property: JsonPropertyName("base")] string BaseSha,
    [property: JsonPropertyName("head")] string HeadSha,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("strategy")] string? Strategy,
    [property: JsonPropertyName("base_run")] string? BaseRun,
    [property: JsonPropertyName("head_run")] string? HeadRun,
    [property: JsonPropertyName("tests")] IReadOnlyList<NewTestResult> Tests)
{
    /// <summary>The base retry's status (null: none ran), after a base that did not build with the PR's test files.</summary>
    [JsonPropertyName("base_retry")]
    public string? BaseRetry { get; init; }

    /// <summary>What the base retry left out of the PR's test files (members and <c>using</c>s that did not compile there).</summary>
    [JsonPropertyName("base_removed")]
    public IReadOnlyList<string> BaseRemoved { get; init; } = [];

    [JsonIgnore]
    public bool Passed => Outcome == NewTestsOutcome.Pass;

    public string ToDetail() => JsonSerializer.Serialize(this);

    public static NewTestsResult? FromDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<NewTestsResult>(detail) is { BaseSha: not null, HeadSha: not null, Outcome: not null } r
                ? r with { Tests = r.Tests ?? [], Reason = r.Reason ?? "", BaseRemoved = r.BaseRemoved ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static NewTestsResult Without(string baseSha, string headSha, string outcome, string reason, string? strategy = null) =>
        new(baseSha, headSha, outcome, reason, strategy, null, null, []);
}

/// <summary>
/// The merge gate's <c>new-tests-fail-on-base</c> check (sc-25382), deterministic code over executed results (E1, E5):
/// the tests the PR adds are found from the files at the base and the head (<see cref="INewTestStrategy"/>), run on the
/// base commit with the PR's test files applied and on the head, each in a throwaway sandboxed worktree. Every new test
/// must fail on the base and pass on the head. A new test that already passes on the base checks nothing, and one skipped
/// there proves nothing: rejected, naming it. A base that does not build with the PR's test files is evidence only for the
/// new tests a compiler error lies in (<see cref="BuildFailure"/>: those cannot pass there, so they count as failing);
/// the others get a real result from one retry of the base without every member that did not compile, and any the retry
/// cannot run either stay unproven (rejected, named) — so no test that passes on the base hides behind another's compile
/// error. A PR that adds no test fails the check (it is required only where a tier lists it: normal and protected by the
/// floor); one whose tests no strategy can run (another stack, or new data rows on an existing theory) fails as
/// unsupported (E2).
/// </summary>
public static class NewTestsCheck
{
    /// <summary>The prefix of a run's results directory (relative to the worktree root; <see cref="NewResultsDirectory"/>).</summary>
    public const string ResultsDirectoryPrefix = ".factory-test-results-";

    /// <summary>A fresh results directory name: random, so no commit can have put result files there in advance.</summary>
    public static string NewResultsDirectory() => ResultsDirectoryPrefix + Guid.NewGuid().ToString("N");

    /// <summary>The supported stacks.</summary>
    public static readonly IReadOnlyList<INewTestStrategy> Strategies = [new XunitNewTests()];

    public static async Task<NewTestsResult> RunAsync(IGateTestRunner runner, IReadOnlyList<INewTestStrategy> strategies, RepoRef repo,
        string baseSha, string headSha, string runName, Action<string>? log, CancellationToken ct)
    {
        var changes = await runner.ChangesAsync(repo, baseSha, headSha, ct);
        var source = new TestSource(runner, repo, baseSha, headSha);
        var found = new List<(INewTestStrategy Strategy, NewTestSet Set)>();
        foreach (var strategy in strategies)
        {
            var set = await strategy.IdentifyAsync(source, changes, ct);
            if (set.TestFiles.Count > 0 || set.Tests.Count > 0 || set.Unisolable.Count > 0)
            {
                found.Add((strategy, set));
            }
        }
        var claimed = found.SelectMany(f => f.Set.TestFiles).SelectMany(f => f.Paths).ToHashSet(StringComparer.Ordinal);
        var unsupported = changes.Where(c => !c.Deleted && c.NewPath is { } p && LooksLikeTest(p) && !claimed.Contains(p)
            && !strategies.Any(s => s.Understands(p))).Select(c => c.NewPath!).ToList();
        var unisolable = found.SelectMany(f => f.Set.Unisolable).ToList();
        if (unsupported.Count > 0 || unisolable.Count > 0)
        {
            var why = new List<string>();
            if (unsupported.Count > 0)
            {
                why.Add($"test file(s) of a stack the gate cannot run ({string.Join(", ", strategies.Select(s => s.Name))} supported): {string.Join(", ", unsupported)}");
            }
            why.AddRange(unisolable);
            return NewTestsResult.Without(baseSha, headSha, NewTestsOutcome.Unsupported,
                $"the PR's new tests cannot be run by the gate, so the check fails: {string.Join("; ", why)}");
        }
        var withTests = found.Where(f => f.Set.Tests.Count > 0).ToList();
        if (withTests.Count == 0)
        {
            return NewTestsResult.Without(baseSha, headSha, NewTestsOutcome.NoTests,
                $"the PR adds no test between {Ci.Short(baseSha)} and {Ci.Short(headSha)}, and a test that fails on the base is required");
        }

        var results = new List<NewTestsResult>();
        foreach (var (strategy, set) in withTests)
        {
            var overlay = set.TestFiles.Where(f => !f.Deleted).Select(f => f.NewPath!).Distinct(StringComparer.Ordinal).ToList();
            var deletes = set.TestFiles.Where(f => f.OldPath is not null && f.OldPath != f.NewPath).Select(f => f.OldPath!)
                .Where(p => !overlay.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            log?.Invoke($"{strategy.Name}: {set.Tests.Count} new test(s): {string.Join(", ", set.Tests)}; base {Ci.Short(baseSha)} + {overlay.Count} test file(s), then head {Ci.Short(headSha)}");
            async Task<TestRunSpec> Spec(string name, string commit, string? from, IReadOnlyList<string> tests)
            {
                var directory = NewResultsDirectory();
                return new TestRunSpec(name, commit, from, from is null ? [] : overlay, from is null ? [] : deletes,
                    await strategy.StepsAsync(source, commit, tests, set.Projects, directory, ct), strategy, directory);
            }
            var baseSpec = await Spec($"{runName}-base", baseSha, headSha, set.Tests);
            var baseRun = await runner.RunAsync(repo, baseSpec, ct);
            var headRun = await runner.RunAsync(repo, await Spec($"{runName}-head", headSha, null, set.Tests), ct);
            var evidence = new BaseEvidence(baseRun);
            if (baseRun.Status == TestRunStatus.BuildFailed && headRun.Status == TestRunStatus.Ran)
            {
                // Which new tests the compiler says cannot build on the base; the others get one retry without whatever
                // did not compile, so a test that passes there cannot hide behind another one's compile error.
                var applied = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in overlay)
                {
                    if (await source.ReadAsync(headSha, path, ct) is { } text)
                    {
                        applied[path] = text;
                    }
                }
                var first = strategy.ExplainBuildFailure(applied, baseRun.BuildErrors, set.Tests);
                evidence = evidence with { First = first };
                var remaining = set.Tests.Except(first.NotBuilt, StringComparer.Ordinal).ToList();
                if (first.Unattributable.Count == 0 && first.Unremovable.Count == 0 && remaining.Count > 0 && first.Replacements.Count > 0)
                {
                    log?.Invoke($"{strategy.Name}: the base does not build with the PR's test files; retrying it without {string.Join(", ", first.Removed)}");
                    var retrySpec = await Spec($"{runName}-base-retry", baseSha, headSha, remaining) with { Replacements = first.Replacements };
                    var retry = await runner.RunAsync(repo, retrySpec, ct);
                    evidence = evidence with { Retry = retry };
                    if (retry.Status == TestRunStatus.BuildFailed)
                    {
                        var stripped = new Dictionary<string, string>(applied, StringComparer.Ordinal);
                        foreach (var (path, text) in first.Replacements)
                        {
                            stripped[path] = text;
                        }
                        evidence = evidence with { RetryFailure = strategy.ExplainBuildFailure(stripped, retry.BuildErrors, remaining) };
                    }
                }
            }
            results.Add(Judge(baseSha, headSha, strategy.Name, set.Tests, evidence, headRun));
        }
        return results.FirstOrDefault(r => !r.Passed) ?? results[0] with
        {
            Strategy = string.Join(", ", results.Select(r => r.Strategy)),
            Tests = results.SelectMany(r => r.Tests).ToList(),
            Reason = string.Join("; ", results.Select(r => r.Reason)),
        };
    }

    /// <summary>A new test's base summary when a compiler error lies in its own declaration (it cannot pass on the base).</summary>
    public const string NotBuilt = "not-built";

    /// <summary>A new test's base summary when the base did not build with it for a reason outside its own declaration.</summary>
    public const string Unproven = "unproven";

    /// <summary>A new test's summary when its run ran but has no case of it.</summary>
    public const string NotRun = "not-run";

    /// <summary>Judges one stack's runs, the base run taken as it is (<see cref="BaseEvidence"/> without a build explanation).</summary>
    public static NewTestsResult Judge(string baseSha, string headSha, string strategy, IReadOnlyList<string> tests, TestRunReport baseRun, TestRunReport headRun) =>
        Judge(baseSha, headSha, strategy, tests, new BaseEvidence(baseRun), headRun);

    /// <summary>
    /// Judges one stack's runs. The head must have run and every new test pass there (every case passed). On the base every
    /// new test must have failed (a failing case), or hold a compiler error in its own declaration
    /// (<see cref="NotBuilt"/>). A new test that passes or is skipped on the base, or that the base never built for another
    /// reason (<see cref="Unproven"/>), is rejected, named; a base run that produced no evidence (restore failed, timed
    /// out, no results, build errors that are no compiler error in the PR's test files, a new test not executed) is an error.
    /// </summary>
    public static NewTestsResult Judge(string baseSha, string headSha, string strategy, IReadOnlyList<string> tests, BaseEvidence evidence, TestRunReport headRun)
    {
        var (b, h) = (Ci.Short(baseSha), Ci.Short(headSha));
        var notBuilt = evidence.NotBuilt.ToHashSet(StringComparer.Ordinal);
        // The run that has each test's base result: the retry when one ran, else the base run.
        var baseRun = evidence.Retry ?? evidence.Run;
        var rows = tests.Select(t => notBuilt.Contains(t)
            ? new NewTestResult(t, NotBuilt, Summary(headRun, t), [], Cases(headRun, t))
            : new NewTestResult(t, baseRun.Status == TestRunStatus.BuildFailed ? Unproven : Summary(baseRun, t), Summary(headRun, t), Cases(baseRun, t), Cases(headRun, t)))
            .ToList();
        NewTestsResult Result(string outcome, string reason) =>
            new(baseSha, headSha, outcome, reason, strategy, Status(evidence.Run.Status), Status(headRun.Status), rows)
            {
                BaseRetry = evidence.Retry is { } retry ? Status(retry.Status) : null,
                BaseRemoved = evidence.Retry is null ? [] : evidence.First?.Removed ?? [],
            };

        if (headRun.Status != TestRunStatus.Ran)
        {
            return Result(NewTestsOutcome.Error, $"the head {h} could not be tested ({Status(headRun.Status)}): {Tail(headRun.Log)}");
        }
        foreach (var (run, label) in new[] { (evidence.Run, "the base"), (evidence.Retry, "the base retry") })
        {
            if (run is not null && run.Status is not (TestRunStatus.Ran or TestRunStatus.BuildFailed))
            {
                return Result(NewTestsOutcome.Error, $"{label} {b} with the PR's test files could not be tested ({Status(run.Status)}): {Tail(run.Log)}");
            }
        }
        if ((evidence.Run.Status == TestRunStatus.BuildFailed && evidence.First is null)
            || (evidence.Retry?.Status == TestRunStatus.BuildFailed && evidence.RetryFailure is null))
        {
            return Result(NewTestsOutcome.Error, $"the base {b} does not build with the PR's test files and its build errors were not examined: {Tail(baseRun.Log)}");
        }
        var unattributable = (evidence.First?.Unattributable ?? []).Concat(evidence.RetryFailure?.Unattributable ?? []).ToList();
        if (unattributable.Count > 0)
        {
            return Result(NewTestsOutcome.Error,
                $"the base {b} does not build with the PR's test files for a reason that says nothing about the new tests (not a compiler error in those files): {string.Join("; ", unattributable.Take(10))}");
        }

        var reasons = new List<string>();
        void Name(IEnumerable<NewTestResult> named, string why)
        {
            if (named.Select(r => r.Test).ToList() is { Count: > 0 } list)
            {
                reasons.Add($"{why}: {string.Join(", ", list)}");
            }
        }
        Name(rows.Where(r => r.Base == TestCaseResult.Passed), $"new test(s) already pass on the base {b} (with the PR's test files), so they check nothing");
        Name(rows.Where(r => r.Base == TestCaseResult.Skipped), $"new test(s) are skipped on the base {b}, which does not show they fail there");
        if (rows.Any(r => r.Base == Unproven))
        {
            var why = (evidence.RetryFailure ?? evidence.First)!;
            var blocking = why.Unremovable.Concat(why.Removed).ToList();
            Name(rows.Where(r => r.Base == Unproven),
                $"new test(s) could not be shown to fail on the base {b}: it does not build with the PR's test files for a reason outside their own declarations ({string.Join("; ", blocking.Take(10))})");
        }
        Name(rows.Where(r => r.Head != TestCaseResult.Passed).Select(r => r with { Test = $"{r.Test} ({r.Head})" }), $"new test(s) do not pass on the head {h}");
        if (reasons.Count > 0)
        {
            return Result(NewTestsOutcome.Rejected, string.Join("; ", reasons));
        }
        if (rows.Where(r => r.Base == NotRun).Select(r => r.Test).ToList() is { Count: > 0 } notRun)
        {
            return Result(NewTestsOutcome.Error, $"new test(s) were not executed on the base {b} (with the PR's test files), which is no evidence they fail there: {string.Join(", ", notRun)}");
        }
        var compiled = rows.Where(r => r.Base == NotBuilt).Select(r => r.Test).ToList();
        var how = compiled.Count == 0 ? "" : $" ({compiled.Count} of them do not compile there: {string.Join(", ", compiled)})";
        return Result(NewTestsOutcome.Pass, $"{rows.Count} new test(s) fail on the base {b}{how} and pass on the head {h}: {string.Join(", ", rows.Select(r => r.Test))}");
    }

    private static IReadOnlyList<TestCaseResult> Cases(TestRunReport run, string test) =>
        run.Results.TryGetValue(test, out var cases) ? cases : [];

    /// <summary>passed (every case passed), failed (any failed), skipped, not-run (no case).</summary>
    private static string Summary(TestRunReport run, string test)
    {
        var cases = Cases(run, test);
        return cases.Count == 0 ? NotRun
            : cases.All(c => c.Outcome == TestCaseResult.Passed) ? TestCaseResult.Passed
            : cases.Any(c => c.Outcome == TestCaseResult.Failed) ? TestCaseResult.Failed
            : TestCaseResult.Skipped;
    }

    private static string Status(TestRunStatus status) => status switch
    {
        TestRunStatus.Ran => "ran",
        TestRunStatus.RestoreFailed => "restore-failed",
        TestRunStatus.BuildFailed => "build-failed",
        TestRunStatus.TimedOut => "timed-out",
        TestRunStatus.NoResults => "no-results",
        _ => status.ToString(),
    };

    private static string Tail(string log)
    {
        var text = log.Trim();
        return text.Length <= 600 ? text : "…" + text[^600..];
    }

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".go", ".rb", ".java", ".kt", ".kts", ".rs", ".php",
        ".swift", ".scala", ".c", ".cc", ".cpp", ".h", ".hpp", ".m", ".ex", ".exs", ".dart",
    };

    private static readonly Regex TestDirectory = new(@"^(tests?|specs?|__tests__)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestProjectDirectory = new(@"[.]?Tests?$", RegexOptions.CultureInvariant);
    private static readonly Regex TestFileName = new(@"(^test_|_test$|[._-](test|spec)s?$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalTestFileName = new(@"Tests?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a source file looks like a test in any common stack (a code file in a <c>test</c>/<c>tests</c>/<c>spec</c>
    /// directory or a <c>*Tests</c> project directory, or named like <c>test_x.py</c>, <c>x_test.go</c>, <c>x.test.ts</c>,
    /// <c>XTests.cs</c>): such a file no strategy claims makes the check unsupported rather than silently passing.
    /// </summary>
    public static bool LooksLikeTest(string path)
    {
        if (!CodeExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }
        var segments = path.Split('/');
        var name = Path.GetFileNameWithoutExtension(segments[^1]);
        return segments[..^1].Any(s => TestDirectory.IsMatch(s) || TestProjectDirectory.IsMatch(s))
            || TestFileName.IsMatch(name) || PascalTestFileName.IsMatch(name);
    }

    /// <summary>The worktree name prefix of an item's gate runs (swept like any worktree no run resumes).</summary>
    public static string RunName(WorkStory story) => $"gate-{story.Ref}";
}
