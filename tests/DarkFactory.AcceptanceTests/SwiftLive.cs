using System.Text.RegularExpressions;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// What the Phase 5 live tests (epic sc-26124, sc-26134; docs/acceptance.md, "Phase 5") seed, as files and per run. The seeds a
/// repository must hold on its default branch before the tests can run are directories of this project (<c>Seeds/</c>, copied to
/// the output): <see cref="PackageSeed"/> (a SwiftPM package, put at <see cref="PackageDir"/> of the sandbox) and
/// <see cref="IosSeed"/> (an iOS app with a unit-test and a UI-test target, put at <see cref="IosDir"/> of the iOS repository) —
/// by an owner's pull request, once. A new test's target must exist on the base (a new test target cannot be run there: the check
/// would be <c>unsupported</c>), so each run seeds only a test, and the production change it needs, into those.
/// </summary>
internal static partial class SwiftSeeds
{
    /// <summary>The SwiftPM package's seed directory under <c>Seeds/</c>.</summary>
    public const string PackageSeed = "swift-package";

    /// <summary>The iOS app's seed directory under <c>Seeds/</c>.</summary>
    public const string IosSeed = "ios-app";

    /// <summary>Where the runbook puts <see cref="PackageSeed"/> in the sandbox.</summary>
    public const string PackageDir = "e2e-swift";

    /// <summary>Where the runbook puts <see cref="IosSeed"/> in the iOS repository (<c>FACTORY_E2E_P5_IOS_DIR</c> overrides it).</summary>
    public const string IosDir = "e2e-ios";

    /// <summary>The SwiftPM package's test target (its module's name, the first part of a test's id).</summary>
    public const string PackageTarget = "DfE2eSwiftTests";

    /// <summary>The iOS app's unit-test target.</summary>
    public const string IosUnitTarget = "TallyTests";

    /// <summary>The iOS app's UI-test target.</summary>
    public const string IosUiTarget = "TallyUITests";

    /// <summary>A seed's files as (path under <paramref name="into"/>, text), ordered; read from the test's output directory.</summary>
    public static IReadOnlyList<(string Path, string Content)> Files(string seed, string into)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Seeds", seed);
        return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => ($"{into}/{Path.GetRelativePath(root, f).Replace('\\', '/')}", File.ReadAllText(f)))
            .OrderBy(f => f.Item1, StringComparer.Ordinal)];
    }

    /// <summary>
    /// P5-AT2/P5-AT5's change (<paramref name="id"/>: this run's own, so the files are new on every base): a source file of the
    /// package with a new function, and an XCTest test of it. On the base the function does not exist, so the test does not compile
    /// there (<see cref="NewTestsCheck.NotBuilt"/>); on the head it passes. Its id as the SwiftPM strategy names it.
    /// </summary>
    public static (string Source, string SourceText, string Test, string TestText, string TestId) MergingChange(string id) =>
        ($"{PackageDir}/Sources/DfE2eSwift/P{id}.swift",
            $"// [dark-factory e2e] seeded by P5-AT2/P5-AT5 (run {id}): the test beside it calls this. Safe to delete.\npublic func dfE2eDouble_p{id}(_ x: Int) -> Int {{ x * 2 }}\n",
            $"{PackageDir}/Tests/{PackageTarget}/P{id}Tests.swift",
            $$"""
            import XCTest
            @testable import DfE2eSwift

            // [dark-factory e2e] seeded by P5-AT2/P5-AT5 (run {{id}}): it calls the function the pull request adds, so it cannot build
            // on the base and passes on the head. Safe to delete.
            final class P{{id}}Tests: XCTestCase {
                func testDoubles() {
                    XCTAssertEqual(dfE2eDouble_p{{id}}(21), 42)
                }
            }

            """,
            $"{PackageTarget}.P{id}Tests/testDoubles");

    /// <summary>P5-AT6's test: a Swift Testing test asserting nothing the change does, so it passes on the base; never merged.</summary>
    public static (string Test, string TestText, string TestId) AlreadyPassing =>
        ($"{PackageDir}/Tests/{PackageTarget}/E2eAlreadyPassing.swift",
            """
            import Testing

            // [dark-factory e2e] seeded by P5-AT6: it passes on the base, so the gate must reject it.
            @Test func e2eAlreadyPassesOnTheBase() {
                #expect(1 + 1 == 2)
            }

            """,
            $"{PackageTarget}.e2eAlreadyPassesOnTheBase()");

    [GeneratedRegex(@"static let count = (\d+)")]
    private static partial Regex CountLine();

    /// <summary>The iOS app's run counter file (<c>Tally/E2eRun.swift</c>) under <paramref name="dir"/>.</summary>
    public static string CounterFile(string dir) => $"{dir}/Tally/E2eRun.swift";

    /// <summary>The run count <paramref name="counter"/> (the counter file's text) holds; null when it holds none.</summary>
    public static int? CountOf(string counter) =>
        CountLine().Match(counter) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>The counter file with its count set to <paramref name="count"/>, everything else kept.</summary>
    public static string WithCount(string counter, int count) => CountLine().Replace(counter, $"static let count = {count}", 1);

    /// <summary>
    /// P5-AT3's test, in the app's unit-test target: <c>E2eRun.count</c> is at least <paramref name="count"/>, the number the run
    /// sets. On the base (one lower) it fails, on the head it passes, and it keeps passing after later runs.
    /// </summary>
    public static (string Test, string TestText, string TestId) IosUnitTest(string dir, int count, string id) =>
        ($"{dir}/{IosUnitTarget}/E2eUnit{id}Tests.swift",
            $$"""
            import XCTest
            @testable import Tally

            // [dark-factory e2e] seeded by P5-AT3 (run {{id}}): the run raises E2eRun.count to {{count}}. Safe to delete.
            final class E2eUnit{{id}}Tests: XCTestCase {
                func testRunCountReached() {
                    XCTAssertGreaterThanOrEqual(E2eRun.count, {{count}})
                }
            }

            """,
            $"{IosUnitTarget}/E2eUnit{id}Tests/testRunCountReached()");

    /// <summary>
    /// P5-AT4's test, in the app's UI-test target: the app, launched on the simulator, shows a run count of at least
    /// <paramref name="count"/> (the <c>e2e-runs</c> label). Fails on the base, passes on the head.
    /// </summary>
    public static (string Test, string TestText, string TestId) IosUiTest(string dir, int count, string id) =>
        ($"{dir}/{IosUiTarget}/E2eUi{id}UITests.swift",
            $$"""
            import XCTest

            // [dark-factory e2e] seeded by P5-AT4 (run {{id}}): the run raises the count the app shows to {{count}}. Safe to delete.
            final class E2eUi{{id}}UITests: XCTestCase {
                func testRunCountShown() {
                    let app = XCUIApplication()
                    app.launch()
                    let label = app.staticTexts["e2e-runs"]
                    XCTAssertTrue(label.waitForExistence(timeout: 30))
                    XCTAssertGreaterThanOrEqual(Int(label.label.replacingOccurrences(of: "Runs: ", with: "")) ?? -1, {{count}})
                }
            }

            """,
            $"{IosUiTarget}/E2eUi{id}UITests/testRunCountShown()");

    /// <summary>A short id of this run (8 hex digits).</summary>
    public static string RunId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>The Phase 5 live tests' shared checks (sc-26134).</summary>
internal static class SwiftLive
{
    /// <summary>The LensWorks repository (P5-AT1, P5-AT7).</summary>
    public static readonly RepoRef LensWorks = new("michaeltrefry", "LensWorks");

    /// <summary>The LensWorks Xcode project's Swift Testing unit-test target.</summary>
    public const string LensWorksTarget = "CompositorTests";

    /// <summary>The iOS repository: <c>FACTORY_E2E_P5_IOS_REPO</c> (<c>owner/name</c>), else the sandbox (the factory's default repository).</summary>
    public static RepoRef IosRepo(FactoryOptions options) =>
        Environment.GetEnvironmentVariable("FACTORY_E2E_P5_IOS_REPO") is { Length: > 0 } repo ? RepoRef.Parse(repo) : options.DefaultRepo;

    /// <summary>Where the iOS app seed is in <see cref="IosRepo"/>: <c>FACTORY_E2E_P5_IOS_DIR</c>, else <see cref="SwiftSeeds.IosDir"/>.</summary>
    public static string IosDir() =>
        Environment.GetEnvironmentVariable("FACTORY_E2E_P5_IOS_DIR") is { Length: > 0 } dir ? dir.Trim('/') : SwiftSeeds.IosDir;

    /// <summary>Skips unless the factory works Swift (<c>Worker:Swift=true</c>: the Swift and Xcode strategies run only then).</summary>
    public static void RequireSwift(FactoryOptions options)
    {
        if (!options.WorkerSwift)
        {
            Assert.Skip("Worker:Swift is off: set Worker__Swift=true (after `sudo scripts/setup-worker-user.sh --slots 2 --swift`).");
        }
    }

    /// <summary>
    /// Skips with the missing owner step unless the gate's App can read <paramref name="repo"/> and its <c>main</c> holds a
    /// <c>factory/gate.yaml</c> (<see cref="ReviewGateTests.RequireGateReadyAsync"/>, for any repository).
    /// </summary>
    public static async Task RequireGateReadyAsync(FactoryOptions options, RepoRef repo, CancellationToken ct)
    {
        Harness.RequireSecret(o => o.GitHubGateAppId);
        Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        Harness.RequireReviewSettings(options);
        using var sandbox = new SandboxRepo(options, repo);
        string? policy;
        try
        {
            policy = await sandbox.GatePolicyAsync("main", ct);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip($"The gate App cannot read {repo} (install both GitHub Apps there): {ex.Message}");
            throw;
        }
        if (policy is null)
        {
            Assert.Skip($"{repo} has no {GatePolicy.Path} on main: merge the PR that adds it (a version 4 policy).");
        }
    }

    /// <summary>Skips unless <paramref name="path"/> exists on <paramref name="repo"/>'s main, naming the runbook's set-up step.</summary>
    public static async Task<string> RequireFileOnMainAsync(SandboxRepo repo, string path, string setup, CancellationToken ct)
    {
        var token = await repo.AppTokenAsync(ct);
        if (!(await repo.TreeFilesAsync(token, "main", ct)).Contains(path))
        {
            Assert.Skip($"{repo.Repo} has no {path} on main: {setup} (docs/acceptance.md, \"Phase 5\").");
        }
        return await repo.FileTextAsync(token, "main", path, ct);
    }

    /// <summary>
    /// Whether CodeGraph indexes <paramref name="path"/>'s language in <paramref name="repo"/>, from <c>file_hotspot</c>'s C3 flag
    /// (sc-26137): true or false as CodeGraph says; false too when it answers no flag (a CodeGraph before C3, which indexes no
    /// Swift: the gate then blocks every Swift change, <see cref="LanguageCoverage.UnindexedBeforeC3"/>). Skips without a CodeGraph
    /// token or when <paramref name="repo"/> is not indexed.
    /// </summary>
    public static async Task<(bool Indexed, string Said)> LanguageIndexedAsync(FactoryOptions options, RepoRef repo, string path, CancellationToken ct)
    {
        var token = CodeGraphLive.RequireToken(options);
        using var http = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        var codeGraph = new CodeGraphMcpClient(http, token);
        var project = await CodeGraphLive.RequireProjectAsync(codeGraph, repo, ct);
        var answer = await codeGraph.FileHotspotAsync(project, path, ct);
        return answer.LanguageIndexed switch
        {
            true => (true, $"CodeGraph indexes {path}'s language ({answer.Language ?? "unnamed"})"),
            false => (false, $"CodeGraph says it does not index {path}'s language ({answer.Language ?? "unnamed"})"),
            null => (false, $"CodeGraph answered no languageIndexed flag for {path}: it predates C3 (sc-26137)"),
        };
    }

    /// <summary>Skips unless CodeGraph indexes Swift in <paramref name="repo"/> (a merge of a Swift change needs it: AT1–AT5b).</summary>
    public static async Task RequireSwiftIndexedAsync(FactoryOptions options, RepoRef repo, string path, CancellationToken ct)
    {
        var (indexed, said) = await LanguageIndexedAsync(options, repo, path, ct);
        if (!indexed)
        {
            Assert.Skip($"{said}. Until CodeGraph C3 (sc-26137) is deployed and indexes Swift, the gate blocks every Swift change on "
                + "risk-threshold (`language not indexed by CodeGraph`), so this merging test cannot pass yet.");
        }
    }

    /// <summary>The first <c>.swift</c> file on <paramref name="repo"/>'s main under <paramref name="under"/> (skips when none).</summary>
    public static async Task<string> SwiftFileOnMainAsync(SandboxRepo repo, string under, CancellationToken ct)
    {
        var files = await repo.TreeFilesAsync(await repo.AppTokenAsync(ct), "main", ct);
        var swift = files.Where(f => f.StartsWith(under, StringComparison.Ordinal) && f.EndsWith(".swift", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).FirstOrDefault();
        if (swift is null)
        {
            Assert.Skip($"{repo.Repo} has no .swift file under '{under}' on main.");
        }
        return swift!;
    }

    /// <summary>The repository the ledger records for the item (its <c>Repo:</c> line).</summary>
    public static async Task<RepoRef> ItemRepoAsync(string ledger, string externalId, CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(ledger));
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == externalId, ct);
        Assert.True(item is not null, $"the ledger has no item {externalId}");
        return RepoRef.Parse(item!.Repo);
    }

    /// <summary>
    /// Where the gate runs <paramref name="testId"/>'s target (<c>&lt;target&gt;/…</c>, an Xcode id) and what kind of bundle it is,
    /// from the projects <paramref name="pbxprojs"/> (path of each <c>*.xcodeproj/project.pbxproj</c> → its text): the target's
    /// <see cref="XcodeProject.TestPlatform"/> in Debug and its product type. Null when no project has that target.
    /// </summary>
    public static (string? Platform, string ProductType)? TargetOf(IReadOnlyDictionary<string, string> pbxprojs, string testId)
    {
        var name = testId[..testId.IndexOf('/')];
        foreach (var (path, text) in pbxprojs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var project = XcodeProject.Parse(path[..^"/project.pbxproj".Length], text);
            if (project.Targets.FirstOrDefault(t => t.Name == name) is { } target)
            {
                return (project.TestPlatform(target, "Debug").Platform, target.ProductType);
            }
        }
        return null;
    }

    /// <summary><see cref="TargetOf"/> over the projects of <paramref name="repo"/> at <paramref name="commit"/>, read through GitHub.</summary>
    public static async Task<(string? Platform, string ProductType)?> TargetAtAsync(SandboxRepo repo, string commit, string testId, CancellationToken ct)
    {
        var token = await repo.AppTokenAsync(ct);
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in (await repo.TreeFilesAsync(token, commit, ct)).Where(p => p.EndsWith(".xcodeproj/project.pbxproj", StringComparison.Ordinal)))
        {
            projects[path] = await repo.FileTextAsync(token, commit, path, ct);
        }
        return TargetOf(projects, testId);
    }

    /// <summary>The strategies a <c>new-tests</c> result names (one, or several joined by <c>", "</c>).</summary>
    public static IReadOnlyList<string> Strategies(NewTestsResult result) => (result.Strategy ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// What a merged item's ledger must hold (pure; empty when it holds it): typed outcomes are checked apart. The transitions end
    /// MergeGate → Merge → (Watch, and Done when the closeout is done); the last <c>new-tests</c> row is <c>pass</c> naming one of
    /// <paramref name="strategies"/> and holding a test <paramref name="isTest"/> picks that did not pass on the base and passed on the
    /// head; the last <c>gate</c> row is a Merge decision listing <c>new-tests-fail-on-base</c> and naming no file CodeGraph does not
    /// index; <c>gate-passed</c> names the head the check judged.
    /// </summary>
    public static List<string> MergedProblems(IReadOnlyList<LedgerEntry> history, IReadOnlyCollection<string> strategies, Func<NewTestResult, bool> isTest)
    {
        var problems = new List<string>();
        var transitions = history.Where(e => e.Step is null).Select(e => e.State).ToList();
        var merge = transitions.LastIndexOf(WorkState.Merge);
        if (merge < 1 || transitions[merge - 1] != WorkState.MergeGate || !transitions.Skip(merge + 1).All(s => s is WorkState.Watch or WorkState.Done)
            || merge == transitions.Count - 1)
        {
            problems.Add($"the transitions do not end MergeGate → Merge → Watch: {string.Join(" → ", transitions)}");
        }
        var newTests = NewTestsResult.FromDetail(history.LastOrDefault(e => e.Step == RunPipeline.Steps.NewTests)?.Detail);
        if (newTests is null)
        {
            problems.Add("no new-tests row");
        }
        else
        {
            if (newTests.Outcome != NewTestsOutcome.Pass)
            {
                problems.Add($"new-tests is {newTests.Outcome}: {newTests.Reason}");
            }
            if (!Strategies(newTests).Any(strategies.Contains))
            {
                problems.Add($"new-tests names strategies '{newTests.Strategy}', none of {string.Join(", ", strategies)}");
            }
            if (!newTests.Tests.Any(t => isTest(t) && t.Base != TestCaseResult.Passed && t.Head == TestCaseResult.Passed))
            {
                problems.Add($"new-tests has no expected new test failing on the base and passing on the head: {string.Join(", ", newTests.Tests.Select(t => $"{t.Test} {t.Base}/{t.Head}"))}");
            }
        }
        var gate = history.LastOrDefault(e => e.Step == RunPipeline.Steps.GateDecision);
        if (gate?.Detail is not { } detail || !detail.StartsWith($"{GateOutcome.Merge} ", StringComparison.Ordinal))
        {
            problems.Add($"the last gate row is not a Merge decision: {gate?.Detail ?? "none"}");
        }
        else
        {
            if (!detail.Contains(GateChecks.NewTestsFailOnBase, StringComparison.Ordinal))
            {
                problems.Add($"the gate's Merge decision does not list {GateChecks.NewTestsFailOnBase}");
            }
            if (detail.Contains(UnindexedMark, StringComparison.Ordinal))
            {
                problems.Add($"the gate's Merge decision names a file CodeGraph does not index: {detail}");
            }
        }
        var passed = history.LastOrDefault(e => e.Step == RunPipeline.Steps.GatePassed)?.Detail;
        if (passed is null || newTests is null || passed != newTests.HeadSha)
        {
            problems.Add($"gate-passed ({passed ?? "none"}) does not name the head the new-tests check judged ({newTests?.HeadSha ?? "none"})");
        }
        return problems;
    }

    /// <summary>How a <c>gate</c> row's CodeGraph inputs name a file whose language CodeGraph does not index (<see cref="CodeGraphRisk.Describe"/>).</summary>
    public const string UnindexedMark = "language not indexed by CodeGraph";

    /// <summary>How the gate's reason names it (<see cref="MergeGate"/>, sc-26133).</summary>
    public const string UnindexedReason = "CodeGraph does not index the language of a changed code file";
}
