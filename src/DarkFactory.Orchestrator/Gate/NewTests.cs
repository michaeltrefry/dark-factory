using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Shortcut;

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
/// <see cref="OverlayFrom"/>, the <see cref="OverlayPaths"/> of that commit written over it and <see cref="DeletePaths"/>
/// removed (the base run: the PR's test files applied to the base commit); then <see cref="Steps"/>, and the files matching
/// <see cref="INewTestStrategy.ResultFilePattern"/> under <see cref="NewTestsCheck.ResultsDirectory"/> parsed by
/// <see cref="Strategy"/>.
/// </summary>
public sealed record TestRunSpec(
    string Name,
    string Commit,
    string? OverlayFrom,
    IReadOnlyList<string> OverlayPaths,
    IReadOnlyList<string> DeletePaths,
    IReadOnlyList<TestStep> Steps,
    INewTestStrategy Strategy);

public enum TestRunStatus
{
    /// <summary>Restored, built and the tests ran (some may have failed): <see cref="TestRunReport.Results"/> holds them.</summary>
    Ran,
    /// <summary>The restore failed: an environment failure, never evidence about the tests.</summary>
    RestoreFailed,
    /// <summary>The code did not build (on the base: the new tests cannot pass there).</summary>
    BuildFailed,
    /// <summary>The run did not finish within its timeout.</summary>
    TimedOut,
    /// <summary>The tests ran but left no result file.</summary>
    NoResults,
}

/// <summary>What one run produced: its status, each test's cases by test id, and the tail of its output.</summary>
public sealed record TestRunReport(TestRunStatus Status, IReadOnlyDictionary<string, IReadOnlyList<TestCaseResult>> Results, string Log)
{
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
/// (<see cref="Unisolable"/>, each with why: the check cannot run them, so it fails, E2).
/// </summary>
public sealed record NewTestSet(IReadOnlyList<ChangedFile> TestFiles, IReadOnlyList<string> Tests, IReadOnlyList<string> Unisolable)
{
    public static readonly NewTestSet None = new([], [], []);
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

    /// <summary>The result files a run leaves under <see cref="NewTestsCheck.ResultsDirectory"/> (e.g. <c>*.trx</c>).</summary>
    string ResultFilePattern { get; }

    Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct);

    /// <summary>The commands that restore, build and run exactly <paramref name="tests"/> at <paramref name="commit"/>.</summary>
    Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, CancellationToken ct);

    /// <summary>Each test's cases (by test id) from the run's result files.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<TestCaseResult>> ParseResults(IEnumerable<string> resultFiles);
}

/// <summary>The check's outcomes. Anything but <see cref="Pass"/> fails the check (the typed outcome maps it to <c>gate_rejected</c>).</summary>
public static class NewTestsOutcome
{
    /// <summary>Every new test failed (or could not build) on the base and passed on the head.</summary>
    public const string Pass = "pass";
    /// <summary>A new test already passes on the base (it checks nothing), or does not pass on the head.</summary>
    public const string Rejected = "rejected";
    /// <summary>The PR adds no test, in a tier that requires the check.</summary>
    public const string NoTests = "no-tests";
    /// <summary>The PR adds tests in a stack no strategy supports, or ones a strategy cannot isolate: the check cannot run.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>A run could not produce evidence (restore failed, timed out, no results, a git or sandbox failure).</summary>
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
                ? r with { Tests = r.Tests ?? [], Reason = r.Reason ?? "" }
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
/// must not pass on the base (a failure, or a base that does not build with the new tests, counts) and must pass on the
/// head. A new test that already passes on the base checks nothing: rejected, naming it. A PR that adds no test fails the
/// check (it is required only where a tier lists it: normal and protected by the floor); one whose tests no strategy
/// can run (another stack, or new data rows on an existing theory) fails as unsupported (E2).
/// </summary>
public static class NewTestsCheck
{
    /// <summary>Where a run's result files go, relative to the worktree root.</summary>
    public const string ResultsDirectory = ".factory-test-results";

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
            var baseRun = await runner.RunAsync(repo, new TestRunSpec($"{runName}-base", baseSha, headSha, overlay, deletes,
                await strategy.StepsAsync(source, baseSha, set.Tests, ct), strategy), ct);
            var headRun = await runner.RunAsync(repo, new TestRunSpec($"{runName}-head", headSha, null, [], [],
                await strategy.StepsAsync(source, headSha, set.Tests, ct), strategy), ct);
            results.Add(Judge(baseSha, headSha, strategy.Name, set.Tests, baseRun, headRun));
        }
        return results.FirstOrDefault(r => !r.Passed) ?? results[0] with
        {
            Strategy = string.Join(", ", results.Select(r => r.Strategy)),
            Tests = results.SelectMany(r => r.Tests).ToList(),
            Reason = string.Join("; ", results.Select(r => r.Reason)),
        };
    }

    /// <summary>
    /// Judges one stack's runs: the head must have run and every new test pass there (every case passed); the base must
    /// have restored and run (or failed to build — the new tests cannot pass there) and no new test may pass there.
    /// </summary>
    public static NewTestsResult Judge(string baseSha, string headSha, string strategy, IReadOnlyList<string> tests, TestRunReport baseRun, TestRunReport headRun)
    {
        var (b, h) = (Ci.Short(baseSha), Ci.Short(headSha));
        var rows = tests.Select(t => new NewTestResult(t, Summary(baseRun, t), Summary(headRun, t), Cases(baseRun, t), Cases(headRun, t))).ToList();
        NewTestsResult Result(string outcome, string reason) =>
            new(baseSha, headSha, outcome, reason, strategy, Status(baseRun.Status), Status(headRun.Status), rows);

        if (headRun.Status != TestRunStatus.Ran)
        {
            return Result(NewTestsOutcome.Error, $"the head {h} could not be tested ({Status(headRun.Status)}): {Tail(headRun.Log)}");
        }
        if (baseRun.Status is not (TestRunStatus.Ran or TestRunStatus.BuildFailed))
        {
            return Result(NewTestsOutcome.Error, $"the base {b} with the PR's test files could not be tested ({Status(baseRun.Status)}): {Tail(baseRun.Log)}");
        }
        var notPassingOnHead = rows.Where(r => r.Head != TestCaseResult.Passed).ToList();
        var passingOnBase = rows.Where(r => r.Base == TestCaseResult.Passed).ToList();
        var reasons = new List<string>();
        if (passingOnBase.Count > 0)
        {
            reasons.Add($"new test(s) already pass on the base {b} (with the PR's test files), so they check nothing: {string.Join(", ", passingOnBase.Select(r => r.Test))}");
        }
        if (notPassingOnHead.Count > 0)
        {
            reasons.Add($"new test(s) do not pass on the head {h}: {string.Join(", ", notPassingOnHead.Select(r => $"{r.Test} ({r.Head})"))}");
        }
        if (reasons.Count > 0)
        {
            return Result(NewTestsOutcome.Rejected, string.Join("; ", reasons));
        }
        var how = baseRun.Status == TestRunStatus.BuildFailed ? $"pass on the head {h}, and the base {b} does not build with them" : $"fail on the base {b} and pass on the head {h}";
        return Result(NewTestsOutcome.Pass, $"{rows.Count} new test(s) {how}: {string.Join(", ", rows.Select(r => r.Test))}");
    }

    private static IReadOnlyList<TestCaseResult> Cases(TestRunReport run, string test) =>
        run.Results.TryGetValue(test, out var cases) ? cases : [];

    /// <summary>passed (every case passed), failed (any failed), skipped, not-run (no case), not-built (the run did not build).</summary>
    private static string Summary(TestRunReport run, string test)
    {
        if (run.Status == TestRunStatus.BuildFailed)
        {
            return "not-built";
        }
        var cases = Cases(run, test);
        return cases.Count == 0 ? "not-run"
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
    public static string RunName(int storyId) => $"gate-{StoryId.Format(storyId)}";
}
