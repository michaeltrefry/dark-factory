using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    /// <summary>A probe of the stack's toolchain (e.g. <c>cargo --version</c>): failing, the toolchain is missing for the run's user.</summary>
    Toolchain,
    Restore,
    Build,
    Test,

    /// <summary>
    /// Run after every other step of the run however the run ended — built or not, timed out, failed, cancelled — and
    /// before its worktree is deleted (e.g. the Xcode wrapper's <c>clean-derived-data</c>, sc-26131): its exit code is
    /// logged and decides nothing, and it is bounded on its own (<see cref="SandboxTestRunner.CleanupTimeout"/>).
    /// </summary>
    Cleanup,
}

/// <summary>
/// One command of a test run, run in the worktree's root. When it exits with <see cref="InvalidCommandLine"/> and has a
/// <see cref="Fallback"/>, the fallback runs instead (e.g. an option an older test framework version names differently).
/// </summary>
public sealed record TestStep(TestPhase Phase, string Program, IReadOnlyList<string> Args)
{
    /// <summary>Microsoft.Testing.Platform's exit code for a command line it rejects (e.g. an unknown option): nothing ran.</summary>
    public const int InvalidCommandLine = 5;

    /// <summary>The step run instead when this one exits with <see cref="InvalidCommandLine"/>.</summary>
    public TestStep? Fallback { get; init; }

    /// <summary>
    /// The result files this test step's runner writes (a file pattern, under the run's results directory; its fallback
    /// writes the same): only these are read as the run's results.
    /// </summary>
    public string? ResultFilePattern { get; init; }

    /// <summary>
    /// When set, the step's standard output is kept by the runner (in memory, never a file the run could write) under this
    /// name: a test step's is read as a result file with the step's exit code (<see cref="ResultFile.ExitCode"/>), a build
    /// step's as a build-error log. For a tool that reports on its output rather than in a file (cargo).
    /// </summary>
    public string? CaptureOutput { get; init; }

    /// <summary>Include stderr in captured build diagnostics only. Never for stdout-only machine result formats.</summary>
    public bool CaptureBuildStderr { get; init; }

    /// <summary>
    /// The exit by which this step says the machine cannot run it for anyone (sc-26132: the iOS simulator's <c>prepare</c> outside
    /// a GUI login session, or with no simulator runtime): the runner then fails the whole run as the factory's failure
    /// (<see cref="WorkSources.FactoryUnavailableException"/>), never the item's — after the run's cleanup steps, as always.
    /// </summary>
    public int? FactoryWideExit { get; init; }

    /// <summary>
    /// The directory the step runs in, relative to the worktree root ('/'-separated; null or "." the root; sc-26232: a prepare
    /// recipe's <c>cwd</c>). The runner refuses one that is not a directory inside the worktree once every link is resolved.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>How long this step may take at most (sc-26232: a recipe step's <c>timeout_minutes</c>), always within the run's own deadline.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Whether the runner replaces <c>${worktree}</c> in <see cref="Program"/> and <see cref="Args"/> with the run's worktree (sc-26232: a recipe step).</summary>
    public bool ExpandsWorktree { get; init; }

    /// <summary>What the run's log calls the step, before its command line (sc-26232: <c>prepare step &lt;name&gt;</c>).</summary>
    public string? Label { get; init; }
}

/// <summary>
/// One result file of a run: its path relative to the results directory, and its content; or a step's captured standard
/// output (<see cref="TestStep.CaptureOutput"/>: its name, the output, and the step's <see cref="ExitCode"/>).
/// </summary>
public sealed record ResultFile(string Name, string Content)
{
    /// <summary>The exit code of the step whose output this is; null for a file the run wrote.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// One sandboxed test run: <see cref="Commit"/> checked out in a fresh throwaway worktree named <see cref="Name"/>; with
/// <see cref="OverlayFrom"/>, the <see cref="OverlayPaths"/> of that commit written over it, <see cref="DeletePaths"/>
/// removed and the <see cref="Replacements"/> written in place of those files (the base run: the PR's test files applied
/// to the base commit; its retry: the same without the members that did not compile there); then <see cref="Steps"/>, and
/// the files matching its test steps' <see cref="TestStep.ResultFilePattern"/> (after a failed build,
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
    /// <summary>The stack's toolchain is not available to the run's user (a <see cref="TestPhase.Toolchain"/> step failed): nothing was built or run.</summary>
    ToolchainMissing,
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
    /// <summary>Why each result file of a <see cref="TestRunStatus.Ran"/> run that could not be read was unreadable (it gave no results).</summary>
    public IReadOnlyList<string> UnreadableReports { get; init; } = [];

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
public record NewTestSet(IReadOnlyList<ChangedFile> TestFiles, IReadOnlyList<string> Tests, IReadOnlyList<string> Unisolable)
{
    public static readonly NewTestSet None = new([], [], []);

    /// <summary>Each new test's project file, by test id.</summary>
    public IReadOnlyDictionary<string, string> Projects { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Xcode execution requirements, agreed at the base and head. Missing facts are never proof of non-GUI execution.</summary>
    public IReadOnlyDictionary<string, XcodeTestPlacement> XcodePlacements { get; init; } = new Dictionary<string, XcodeTestPlacement>(StringComparer.Ordinal);

    /// <summary>
    /// Test files the base run gets with this content instead of the head's, by path (each also one of
    /// <see cref="TestFiles"/>): a file that holds production code too, where only the PR's test code may be applied to
    /// the base (a Rust file's <c>#[cfg(test)]</c> modules grafted into its base version, <see cref="CargoNewTests"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> BaseReplacements { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What the strategy chose that the result should say (sc-26132: the platform a target building for both is tested on).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
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
    IReadOnlyDictionary<string, string> Replacements)
{
    /// <summary>This failure and a later retry's (<see cref="INewTestStrategy.BaseRetries"/>) as one: everything either found.</summary>
    public BuildFailure With(BuildFailure later) => new(
        NotBuilt.Concat(later.NotBuilt).Distinct(StringComparer.Ordinal).ToList(), [.. Unattributable, .. later.Unattributable], [.. Unremovable, .. later.Unremovable],
        [.. Removed, .. later.Removed], later.Replacements);
}

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
/// and to run just those tests. <see cref="XunitNewTests"/> is .NET/xUnit, <see cref="CargoNewTests"/> Rust/cargo,
/// <see cref="SwiftNewTests"/> Swift/SwiftPM with Swift Testing; a
/// stack none claims is <see cref="NewTestsOutcome.Unsupported"/>.
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

    /// <summary>The build-error logs a run's build steps leave under its <see cref="TestRunSpec.ResultsDirectory"/>.</summary>
    string BuildLogPattern { get; }

    /// <summary>
    /// How many times a base that does not build with the PR's test files is retried, each time without what did not
    /// compile in the run before (1 unless the stack's compiler reports its errors in stages, so that leaving one out
    /// uncovers the next: rustc names an import that does not resolve before the uses of it).
    /// </summary>
    int BaseRetries => 1;

    Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct);

    /// <summary>
    /// The commands that restore, build and run exactly <paramref name="tests"/> (in their <paramref name="projects"/>) at
    /// <paramref name="commit"/>: every restore, then every build, then every test command, writing results and build-error
    /// logs under <paramref name="resultsDirectory"/> (relative to the worktree root); each test command names the result
    /// files it writes (<see cref="TestStep.ResultFilePattern"/>).
    /// </summary>
    Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects,
        string resultsDirectory, CancellationToken ct);

    /// <summary>Each test's cases (by test id) from the run's result files, and why any of them could not be read.</summary>
    TestResults ParseResults(IEnumerable<ResultFile> resultFiles);

    /// <summary>The errors in one build-error log; files under one of <paramref name="roots"/> (the worktree) are made relative to it.</summary>
    IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots);

    /// <summary>
    /// Attributes a failed base build's <paramref name="errors"/> to the new <paramref name="tests"/> of
    /// <paramref name="set"/> (what <see cref="IdentifyAsync"/> found), given the content of every file the run applied
    /// (<paramref name="applied"/>, by path; deterministic, nothing compiled).
    /// </summary>
    BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests);
}

/// <summary>
/// A run's results: each test's cases by test id from the result files that could be read, and why each other result file
/// could not be (<see cref="Unreadable"/>; the tests it held then have no cases).
/// </summary>
public sealed record TestResults(IReadOnlyDictionary<string, IReadOnlyList<TestCaseResult>> Cases, IReadOnlyList<string> Unreadable);

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
    /// A run could not produce evidence (the stack's toolchain is missing, restore failed, timed out, no results, an unreadable test report, a new test
    /// not executed, a base build error outside the PR's test code or not from the compiler, a git or sandbox failure).
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
/// the others get a real result from a retry of the base without every member that did not compile (one retry, or as
/// many as the stack's compiler has stages, <see cref="INewTestStrategy.BaseRetries"/>), and any the last retry cannot run
/// either stay unproven (rejected, named) — so no test that passes on the base hides behind another's compile error. A PR that adds no test fails the check (it is required only where a tier lists it: normal and protected by the
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
    public static readonly IReadOnlyList<INewTestStrategy> Strategies = [new XunitNewTests(), new CargoNewTests(), new SwiftNewTests()];

    /// <summary>
    /// The supported stacks of a factory: with <c>Worker:Swift</c> on (<paramref name="swift"/>, sc-26131) also Xcode projects'
    /// macOS tests (<see cref="XcodeNewTests"/>, run through the installed xcodebuild wrapper <paramref name="wrapperPath"/>);
    /// off, exactly <see cref="Strategies"/> (E7).
    /// </summary>
    public static IReadOnlyList<INewTestStrategy> StrategiesFor(bool swift, string wrapperPath = Worker.SwiftToolchain.DefaultWrapperPath) =>
        swift ? [new XunitNewTests(), new CargoNewTests(), new SwiftNewTests(xcode: true), new XcodeNewTests(wrapperPath)] : Strategies;

    /// <param name="prepared">
    /// Each commit's prepare recipe steps (sc-26232; <see cref="Prepare.PreparedCommits"/>, artifacts already fetched owner-side): every
    /// run of a commit runs its own commit's steps — the base run and its retries the base's (the overlay applies only test files),
    /// the head run the head's — after the strategy's toolchain probes and before its restore. Null, or a commit with no recipe:
    /// the runs are exactly as before (E7). Its <see cref="Prepare.PreparedCommits.Changes"/>, when it has them, are the PR's changes
    /// already read (one fetch per check); its <see cref="Prepare.PreparedCommits.Note"/> (the base prepared with the head's recipe)
    /// is added to the result's reason.
    /// </param>
    /// <param name="localExecution">
    /// The pipeline's worker-phase boundary for a local stack/platform group (base, head and retries). Discovery and hosted iOS
    /// groups do not use it; callers without a sandbox phase may omit it, retaining the ordinary direct-run contract.
    /// </param>
    public static async Task<NewTestsResult> RunAsync(IGateTestRunner runner, IReadOnlyList<INewTestStrategy> strategies, RepoRef repo,
        string baseSha, string headSha, string runName, Action<string>? log, CancellationToken ct, Prepare.PreparedCommits? prepared = null, RemoteIosNewTests? remoteIos = null,
        Func<Func<Task<NewTestsResult>>, Task<NewTestsResult>>? localExecution = null)
    {
        var result = await RunCoreAsync(runner, strategies, repo, baseSha, headSha, runName, log, ct, prepared, remoteIos, localExecution);
        return prepared?.Note is { } note ? result with { Reason = $"{result.Reason}; {note}" } : result;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is under the prepare recipe's directory (<c>.factory/</c>, any case): never applied onto a base
    /// run (sc-26232) — each commit is prepared by its own recipe, so the PR's recipe must not reach the base's worktree as a "test file".
    /// </summary>
    public static bool IsPrepareRecipePath(string path) =>
        path.StartsWith(Prepare.PrepareRecipe.Directory + "/", StringComparison.OrdinalIgnoreCase);

    private static async Task<NewTestsResult> RunCoreAsync(IGateTestRunner runner, IReadOnlyList<INewTestStrategy> strategies, RepoRef repo,
        string baseSha, string headSha, string runName, Action<string>? log, CancellationToken ct, Prepare.PreparedCommits? prepared, RemoteIosNewTests? remoteIos,
        Func<Func<Task<NewTestsResult>>, Task<NewTestsResult>>? localExecution)
    {
        var changes = prepared?.Changes ?? await runner.ChangesAsync(repo, baseSha, headSha, ct);
        var source = new TestSource(runner, repo, baseSha, headSha);
        var found = new List<(INewTestStrategy Strategy, NewTestSet Set)>();
        foreach (var strategy in strategies)
        {
            var set = await strategy.IdentifyAsync(source, changes, ct);
            if (set.TestFiles.Count > 0 || set.Tests.Count > 0 || set.Unisolable.Count > 0)
            {
                if (remoteIos is not null && strategy is XcodeNewTests && set.Tests.Count > 0)
                {
                    // A PR may contain both platforms. Partition only the test ids, preserving the complete code-written
                    // base overlay and Swift location metadata for attribution and retries; no iOS test is sent locally.
                    foreach (var group in set.Tests.GroupBy(t => XcodeNewTests.Build.From(set.Projects[t]).Platform))
                        found.Add((strategy, set with { Tests = group.ToArray() }));
                }
                else found.Add((strategy, set));
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
            // Nothing under .factory/ is applied onto the base (sc-26232): the base is prepared by its own recipe.
            var overlay = set.TestFiles.Where(f => !f.Deleted).Select(f => f.NewPath!).Where(p => !IsPrepareRecipePath(p))
                .Distinct(StringComparer.Ordinal).ToList();
            var deletes = set.TestFiles.Where(f => f.OldPath is not null && f.OldPath != f.NewPath).Select(f => f.OldPath!)
                .Where(p => !overlay.Contains(p) && !IsPrepareRecipePath(p)).Distinct(StringComparer.Ordinal).ToList();
            var baseReplacements = set.BaseReplacements.Where(r => !IsPrepareRecipePath(r.Key))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            log?.Invoke($"{strategy.Name}: {set.Tests.Count} new test(s): {string.Join(", ", set.Tests)}; base {Ci.Short(baseSha)} + {overlay.Count} test file(s), then head {Ci.Short(headSha)}");
            async Task<TestRunSpec> Spec(string name, string commit, string? from, IReadOnlyList<string> tests)
            {
                var directory = NewResultsDirectory();
                return new TestRunSpec(name, commit, from, from is null ? [] : overlay, from is null ? [] : deletes,
                    Prepare.PrepareSteps.Before(prepared?.StepsFor(commit) ?? [], await strategy.StepsAsync(source, commit, tests, set.Projects, directory, ct)),
                    strategy, directory)
                {
                    Replacements = from is null ? new Dictionary<string, string>(StringComparer.Ordinal) : baseReplacements,
                };
            }
            var ios = remoteIos is not null && strategy is XcodeNewTests && XcodeNewTests.Build.From(set.Projects[set.Tests[0]]).Platform == XcodeProject.IOS;
            async Task<TestRunReport> Run(TestRunSpec spec, IReadOnlyList<string> tests)
            {
                if (!ios) return await runner.RunAsync(repo, spec, ct);
                var recipe = prepared?.Recipes.GetValueOrDefault(spec.Commit);
                if (recipe is null && prepared?.StepsFor(spec.Commit).Count > 0)
                    return TestRunReport.Failed(TestRunStatus.NoResults, "The declared prepare recipe is missing; private owner-cache steps are never transported.");
                return await remoteIos!.RunExecutionAsync(repo, baseSha, headSha, spec, tests, set.Projects, recipe, ct);
            }
            // Discovery and remote groups need no local sandbox user. A local group's base, head and retries
            // retain one worker phase, so its helper/worktree lifecycle stays protected as before.
            async Task<NewTestsResult> ExecuteGroup()
            {
                var baseSpec = await Spec($"{runName}-base", baseSha, headSha, set.Tests);
                var baseRun = await Run(baseSpec, set.Tests);
                var headRun = await Run(await Spec($"{runName}-head", headSha, null, set.Tests), set.Tests);
                var evidence = new BaseEvidence(baseRun);
                if (baseRun.Status == TestRunStatus.BuildFailed && headRun.Status == TestRunStatus.Ran)
                {
                    // Which new tests the compiler says cannot build on the base; the others get one retry without whatever
                    // did not compile, so a test that passes there cannot hide behind another one's compile error.
                    var applied = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var path in overlay)
                    {
                        if ((baseReplacements.TryGetValue(path, out var grafted) ? grafted : await source.ReadAsync(headSha, path, ct)) is { } text)
                        {
                            applied[path] = text;
                        }
                    }
                    var failure = strategy.ExplainBuildFailure(set, applied, baseRun.BuildErrors, set.Tests);
                    evidence = evidence with { First = failure };
                    var remaining = set.Tests.Except(failure.NotBuilt, StringComparer.Ordinal).ToList();
                    var stripped = new Dictionary<string, string>(applied, StringComparer.Ordinal);
                    var rewritten = baseReplacements.Keys.ToHashSet(StringComparer.Ordinal);
                    for (var attempt = 1; attempt <= Math.Max(strategy.BaseRetries, 1) && failure.Unattributable.Count == 0 && failure.Unremovable.Count == 0
                        && remaining.Count > 0 && failure.Replacements.Count > 0; attempt++)
                    {
                        log?.Invoke($"{strategy.Name}: the base does not build with the PR's test files; retrying it without {string.Join(", ", failure.Removed)}");
                        foreach (var (path, text) in failure.Replacements)
                        {
                            stripped[path] = text;
                            rewritten.Add(path);
                        }
                        if (evidence.RetryFailure is { } earlier)
                        {
                            // A further retry: what the one before it found is kept with the base run's.
                            evidence = evidence with { First = evidence.First!.With(earlier), RetryFailure = null };
                        }
                        // The retry's files: the base run's replacements, and over them the files without what did not compile.
                        var replaced = rewritten.ToDictionary(p => p, p => stripped[p], StringComparer.Ordinal);
                        var retrySpec = await Spec($"{runName}-base-retry{(attempt == 1 ? "" : $"-{attempt}")}", baseSha, headSha, remaining) with { Replacements = replaced };
                        var retry = await Run(retrySpec, remaining);
                        evidence = evidence with { Retry = retry };
                        if (retry.Status != TestRunStatus.BuildFailed)
                        {
                            break;
                        }
                        failure = strategy.ExplainBuildFailure(set, stripped, retry.BuildErrors, remaining);
                        evidence = evidence with { RetryFailure = failure };
                        remaining = remaining.Except(failure.NotBuilt, StringComparer.Ordinal).ToList();
                    }
                }
                var judged = Judge(baseSha, headSha, strategy.Name, set.Tests, evidence, headRun);
                return set.Notes.Count == 0 ? judged : judged with { Reason = SafeExcerpt($"{judged.Reason} ({string.Join("; ", set.Notes)})", 4000) };
            }
            results.Add(!ios && localExecution is not null ? await localExecution(ExecuteGroup) : await ExecuteGroup());
        }
        return results.FirstOrDefault(r => !r.Passed) ?? results[0] with
        {
            Strategy = string.Join(", ", results.Select(r => r.Strategy)),
            Tests = results.SelectMany(r => r.Tests).ToList(),
            Reason = SafeExcerpt(string.Join("; ", results.Select(r => r.Reason)), 4000),
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
    /// reason (<see cref="Unproven"/>), is rejected, named; a run that produced no evidence (restore failed, timed out, no
    /// results, a test report that could not be read, build errors that are no compiler error in the PR's test files, a new
    /// test not executed) is an error.
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
            new(baseSha, headSha, outcome, SafeExcerpt(reason, 4000), strategy, Status(evidence.Run.Status), Status(headRun.Status), rows)
            {
                BaseRetry = evidence.Retry is { } retry ? Status(retry.Status) : null,
                BaseRemoved = evidence.Retry is null ? [] : evidence.First?.Removed ?? [],
            };

        if (headRun.Status != TestRunStatus.Ran)
        {
            return Result(NewTestsOutcome.Error, $"the head {h} could not be tested ({Status(headRun.Status)}): {Tail(headRun.Log)}{BuildDiagnostics(headRun)}");
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
                $"the base {b} does not build with the PR's test files for a reason that says nothing about the new tests (not a compiler error in those files): {string.Join("; ", unattributable.Take(10).Select(e => SafeExcerpt(e, 300)))}{BuildDiagnostics(baseRun)}");
        }

        foreach (var (run, label) in new[] { (evidence.Run, $"the base {b}"), (evidence.Retry, $"the base retry {b}"), (headRun, $"the head {h}") })
        {
            if (run is { UnreadableReports.Count: > 0 })
            {
                return Result(NewTestsOutcome.Error,
                    $"the test report of {label} could not be read, so its results are no evidence: {string.Join("; ", run.UnreadableReports.Take(10))}");
            }
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
                $"new test(s) could not be shown to fail on the base {b}: it does not build with the PR's test files for a reason outside their own declarations ({string.Join("; ", blocking.Take(10).Select(e => SafeExcerpt(e, 300)))})");
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
        TestRunStatus.ToolchainMissing => "toolchain-missing",
        _ => status.ToString(),
    };

    /// <summary>Keep concrete compiler causes even when an unlocated companion failure blocks attribution.
    /// No raw build log is persisted: at most three redacted, single-line, 300-character located diagnostics.</summary>
    private static string BuildDiagnostics(TestRunReport report)
    {
        var diagnostics = report.BuildErrors.Where(e => e.Path is not null).Take(3).Select(e => SafeExcerpt(e.ToString(), 300)).ToList();
        return diagnostics.Count == 0 ? "" : "; compiler diagnostics: " + string.Join("; ", diagnostics);
    }

    /// <summary>The outgoing persistence boundary: normalize and redact BEFORE any clipping, never after a token was split.</summary>
    private static string SafeExcerpt(string text, int limit, bool keepEnd = false)
    {
        var safe = new string(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        safe = CiHeal.Redact(safe).Trim();
        return safe.Length <= limit ? safe : keepEnd ? "…" + safe[^(limit - 1)..] : safe[..(limit - 1)] + "…";
    }

    private static string Tail(string log) => SafeExcerpt(log, 600, keepEnd: true);

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
