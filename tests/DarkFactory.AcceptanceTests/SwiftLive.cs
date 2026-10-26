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
internal static class SwiftSeeds
{
    /// <summary>The SwiftPM package's seed directory under <c>Seeds/</c>.</summary>
    public const string PackageSeed = "swift-package";

    /// <summary>The iOS app's seed directory under <c>Seeds/</c>.</summary>
    public const string IosSeed = "ios-app";

    /// <summary>Where the runbook puts <see cref="PackageSeed"/> in the sandbox.</summary>
    public const string PackageDir = "e2e-swift";

    /// <summary>The manifest path that qualifies every SwiftPM result the live seeds expect.</summary>
    public const string PackageManifest = PackageDir + "/Package.swift";

    private static string PackageTestKey(string runtimeId) => SwiftNewTests.TestKey(PackageManifest, runtimeId);

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
    /// there (<see cref="NewTestsCheck.NotBuilt"/>); on the head it passes. TestId is the package-qualified ledger/result key,
    /// not a native filter: every P5-AT2/P5-AT5 result lookup must retain the package identity.
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
            PackageTestKey($"{PackageTarget}.P{id}Tests/testDoubles"));

    /// <summary>P5-AT6's test: a Swift Testing test asserting nothing the change does, so it passes on the base; never merged.
    /// TestId is the package-qualified result key used by its rejection lookup and reason assertion.</summary>
    public static (string Test, string TestText, string TestId) AlreadyPassing =>
        ($"{PackageDir}/Tests/{PackageTarget}/E2eAlreadyPassing.swift",
            """
            import Testing

            // [dark-factory e2e] seeded by P5-AT6: it passes on the base, so the gate must reject it.
            @Test func e2eAlreadyPassesOnTheBase() {
                #expect(1 + 1 == 2)
            }

            """,
            PackageTestKey($"{PackageTarget}.e2eAlreadyPassesOnTheBase()"));

    // A plain Regex, not [GeneratedRegex]: the gateway lint compiles this project's sources without its source generators.
    private static readonly Regex CountPattern = new(@"static let count = (\d+)", RegexOptions.CultureInvariant);

    private static Regex CountLine() => CountPattern;

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
    /// Whether the gate would count <paramref name="path"/>'s language as indexed by CodeGraph, from the same two answers the gate
    /// reads and by the gate's own rule (<see cref="LanguageCoverage.Of"/>; pure). <paramref name="impact"/>: the
    /// <c>analyze_impact_file</c> answer, null when it was not read; a C4 <c>notIndexed</c> answer (no ready overlay of the commit
    /// asked at) counts as not read. An error answer of either tool throws — the probe fails, it never skips on it: a CodeGraph that
    /// cannot answer would otherwise leave P5-AT5b skipping for ever and P5-AT5a (an unindexed Swift change recorded as no CodeGraph
    /// data) running on a false premise. And a file the rule counts as indexed must be one CodeGraph knows (<c>found: true</c>): an answer about a path
    /// CodeGraph has no data for says nothing of its language. Before C3 (no flag) a Swift file CodeGraph has no graph data for is
    /// unindexed by the rule (<see cref="LanguageCoverage.UnindexedBeforeC3"/>).
    /// </summary>
    public static (bool Indexed, string Said) ProbeOf(string path, ImpactFileAnswer? impact, HotspotAnswer hotspot)
    {
        if (hotspot.IsError)
        {
            throw new InvalidOperationException($"CodeGraph's file_hotspot answered an error for {path} ({hotspot.ErrorCode ?? "no code"}): {hotspot.Text}");
        }
        if (impact is { IsError: true, NotIndexed: null })
        {
            throw new InvalidOperationException($"CodeGraph's analyze_impact_file answered an error for {path} ({impact.ErrorCode ?? "no code"}): {impact.Text}");
        }
        var read = impact is { NotIndexed: null } ? impact : null;
        var found = read?.Found ?? hotspot.Found;
        var unindexed = LanguageCoverage.Of(path, read?.LanguageIndexed, read?.Language, hotspot.LanguageIndexed, hotspot.Language, found);
        var answers = read is null ? "file_hotspot's answer (analyze_impact_file not read)" : "analyze_impact_file's and file_hotspot's answers";
        if (unindexed is not null)
        {
            return (false, $"By the gate's rule over {answers}, CodeGraph does not index {unindexed}");
        }
        if (found != true)
        {
            throw new InvalidOperationException($"CodeGraph has no data for {path} (found: {found?.ToString() ?? "none"}), so its answers say "
                + "nothing of the file's language: probe a file CodeGraph has indexed.");
        }
        return (true, $"By the gate's rule over {answers}, CodeGraph indexes {path}'s language "
            + $"({LanguageCoverage.Name(read?.Language) ?? LanguageCoverage.Name(hotspot.Language) ?? "unnamed"})");
    }

    /// <summary>
    /// <see cref="ProbeOf"/> over CodeGraph's live answers for <paramref name="path"/> in <paramref name="repo"/>: <c>file_hotspot</c>,
    /// then <c>analyze_impact_file</c> at the default-branch index commit the hotspot answer names. Skips without a CodeGraph token
    /// or when <paramref name="repo"/> is not indexed; FAILS when CodeGraph answers an error or does not know the file.
    /// </summary>
    public static async Task<(bool Indexed, string Said)> LanguageIndexedAsync(FactoryOptions options, RepoRef repo, string path, CancellationToken ct)
    {
        var token = CodeGraphLive.RequireToken(options);
        using var http = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        var codeGraph = new CodeGraphMcpClient(http, token);
        var project = await CodeGraphLive.RequireProjectAsync(codeGraph, repo, ct);
        var hotspot = await codeGraph.FileHotspotAsync(project, path, ct);
        var commit = hotspot.IndexCommitSha ?? hotspot.CommitSha;
        var impact = hotspot.IsError || commit is null
            ? null
            : await codeGraph.AnalyzeImpactFileAsync(project, path, commit, CodeGraphRiskReader.ImpactDepth, ct);
        try
        {
            return ProbeOf(path, impact, hotspot);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail($"The CodeGraph language probe of {repo}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Skips unless CodeGraph indexes Swift in <paramref name="repo"/>: only P5-AT5b, which shows the gate USING CodeGraph's data of
    /// Swift files. No merge needs it (sc-26254: without it the gate records no CodeGraph data and merges).
    /// </summary>
    public static async Task RequireSwiftIndexedAsync(FactoryOptions options, RepoRef repo, string path, CancellationToken ct)
    {
        var (indexed, said) = await LanguageIndexedAsync(options, repo, path, ct);
        if (!indexed)
        {
            Assert.Skip($"{said}. P5-AT5b shows the gate using CodeGraph's data of Swift files, which needs CodeGraph C3 (sc-26137) "
                + "deployed and indexing Swift; until then run P5-AT5a.");
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
        // SwiftPM result keys are not Xcode ids, even when their runtime id contains a slash. Never strip their package
        // identity and count them as an Xcode/iOS execution proof. Unqualified ids without a target separator are not Xcode either.
        if (SwiftNewTests.RuntimeId(testId) != testId) return null;
        var separator = testId.IndexOf('/');
        if (separator <= 0) return null;
        var name = testId[..separator];
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
    /// head; the last <c>gate</c> row is a Merge decision listing <c>new-tests-fail-on-base</c> (a file CodeGraph does not index may be
    /// named there as having no CodeGraph data: that never blocks since sc-26254, and P5-AT5a/5b check it apart); <c>gate-passed</c>
    /// names the head the check judged.
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
        }
        var passed = history.LastOrDefault(e => e.Step == RunPipeline.Steps.GatePassed)?.Detail;
        if (passed is null || newTests is null || passed != newTests.HeadSha)
        {
            problems.Add($"gate-passed ({passed ?? "none"}) does not name the head the new-tests check judged ({newTests?.HeadSha ?? "none"})");
        }
        return problems;
    }

    /// <summary>
    /// Null when <paramref name="result"/> was decided by <paramref name="expected"/> alone; else a message naming the strategy that
    /// did decide it (pure). A rejected result names only the first strategy that rejected, so a .NET/xUnit test the implementer
    /// added that passes on the base would be the one named (P5-AT6's story must ask for none).
    /// </summary>
    public static string? RejectedByProblem(NewTestsResult result, string expected) =>
        result.Strategy == expected
            ? null
            : $"the new-tests check was decided by strategy '{result.Strategy ?? "none"}', not {expected} ({result.Outcome}: {result.Reason}); "
                + "the story must tell its implementer to add no .NET/xUnit test";

    /// <summary>
    /// One ledger item P5-AT7 reads: its id, repository, source, epic, whether it was delivered (merged through the gate; for a plan
    /// item, Done) and, for a plan item, the stories its newest <c>plan-stories</c> row lists (else empty).
    /// </summary>
    public sealed record RealItem(string Id, RepoRef Repo, string Source, long? EpicId, bool Delivered, IReadOnlyList<string> PlanStories);

    /// <summary>
    /// What makes the listed items short of a real LensWorks epic (pure; empty when they are one). A real epic is EITHER at least two
    /// listed LensWorks items (not plan items) of one epic (same source and <c>EpicId</c>), OR a listed LensWorks plan item every story
    /// of which (its <c>plan-stories</c>) is listed and delivered. And for every epic of a listed LensWorks item, every item the ledger
    /// holds of it (<paramref name="ledgerOfEpics"/>) must be listed and delivered, so a part of the epic left out of the list or not
    /// merged fails the test rather than going unseen.
    /// </summary>
    public static List<string> EpicProblems(IReadOnlyList<RealItem> listed, IReadOnlyList<RealItem> ledgerOfEpics)
    {
        var problems = new List<string>();
        var byId = listed.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var lens = listed.Where(i => i.Repo.Is(LensWorks)).ToList();
        var epics = lens.Where(i => i.EpicId is not null).Select(i => (i.Source, Epic: i.EpicId!.Value)).Distinct().ToList();
        var plans = lens.Where(i => i.PlanStories.Count > 0).ToList();
        var sharedEpic = epics.Any(e => lens.Count(i => i.PlanStories.Count == 0 && i.Source == e.Source && i.EpicId == e.Epic) >= 2);
        var wholePlan = plans.Any(p => p.PlanStories.All(s => byId.TryGetValue(s, out var story) && story.Delivered));
        if (!sharedEpic && !wholePlan)
        {
            problems.Add($"no real LensWorks epic among {string.Join(", ", listed.Select(i => i.Id))}: list at least two LensWorks items of one "
                + "epic, or a LensWorks plan item and every story it planned");
        }
        foreach (var (source, epic) in epics)
        {
            foreach (var item in ledgerOfEpics.Where(i => i.Source == source && i.EpicId == epic).OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                if (!byId.TryGetValue(item.Id, out var seen))
                {
                    problems.Add($"the ledger holds {item.Id} of the epic {epic} ({source}), which is not listed");
                }
                else if (!seen.Delivered)
                {
                    problems.Add($"{item.Id} of the epic {epic} ({source}) was not delivered");
                }
            }
        }
        foreach (var plan in plans)
        {
            foreach (var story in plan.PlanStories)
            {
                if (!byId.TryGetValue(story, out var seen))
                {
                    problems.Add($"{story}, a story of the plan {plan.Id}, is not listed");
                }
                else if (!seen.Delivered)
                {
                    problems.Add($"{story}, a story of the plan {plan.Id}, was not delivered");
                }
            }
        }
        return [.. problems.Distinct()];
    }

    /// <summary>One new test of a P5-AT7 item: the item's repository and id, the platform its target runs on, and its base result.</summary>
    public sealed record RealTest(RepoRef Repo, string Id, string Test, string? Platform, string Base);

    /// <summary>
    /// Null when one of <paramref name="tests"/> is of <paramref name="iosRepo"/>, its target runs on an iOS Simulator, and it was RUN
    /// on the base and failed there (<see cref="TestCaseResult.Failed"/>): only an executed test proves the simulator ran it — a
    /// <see cref="NewTestsCheck.NotBuilt"/> base never reached one. Else why not (pure).
    /// </summary>
    public static string? IosProblem(IReadOnlyList<RealTest> tests, RepoRef iosRepo)
    {
        var ios = tests.Where(t => t.Repo.Is(iosRepo) && t.Platform == XcodeProject.IOS).ToList();
        if (ios.Any(t => t.Base == TestCaseResult.Failed))
        {
            return null;
        }
        return ios.Count == 0
            ? $"no listed item of {iosRepo} has a new test whose target runs on an iOS Simulator"
            : $"no new test of {iosRepo} on an iOS target was run and failed on the base (so the simulator never ran one there): "
                + string.Join(", ", ios.Select(t => $"{t.Id} {t.Test} {t.Base}"));
    }

    /// <summary>
    /// How a <c>gate</c> row's CodeGraph inputs name a Swift file CodeGraph does not index (<see cref="CodeGraphRisk.Describe"/>,
    /// <see cref="UnindexedFile.Reason"/>): recorded as no CodeGraph data, never blocking (sc-26254).
    /// </summary>
    public const string UnindexedSwiftMark = CodeGraphRisk.NoDataPrefix + "its language (Swift) is not indexed by CodeGraph";

    /// <summary>
    /// Why an item's ledger does not show the real panel passing the head the gate merged (pure; null when it does), for P5-AT7: its
    /// last verdict passed, is for the head of its last <c>gate-passed</c> row, and every review and second opinion of it was served
    /// on the router's high class with no panel-rule problem (as <c>ReviewGateTests.AssertHighClassPanel</c> asserts).
    /// </summary>
    public static string? PanelProblem(List<LedgerEntry> history)
    {
        var verdicts = RunPipeline.Verdicts(history);
        if (verdicts.Count == 0)
        {
            return "no review verdict";
        }
        var verdict = verdicts[^1];
        var passed = history.LastOrDefault(e => e.Step == RunPipeline.Steps.GatePassed)?.Detail;
        if (!verdict.Passed)
        {
            return $"the last verdict did not pass: {verdict.Summary}";
        }
        if (verdict.HeadSha != passed)
        {
            return $"the last verdict is for {verdict.HeadSha}, not the head the gate passed ({passed ?? "none"})";
        }
        if (verdict.Reviews.Count == 0)
        {
            return "the last verdict has no review";
        }
        var off = verdict.Reviews.Where(r => r.ServedClass != ReviewModels.Class).Select(r => $"{r.Role} on class {r.ServedClass ?? "(none)"}").ToList();
        if (off.Count > 0)
        {
            return $"not served on the {ReviewModels.Class} class: {string.Join(", ", off)}";
        }
        var problems = verdict.Reviews.SelectMany(ReviewModels.Problems).ToList();
        return problems.Count > 0 ? $"panel problems: {string.Join("; ", problems)}" : null;
    }

    /// <summary>
    /// Why a <c>gate</c> row does not show CodeGraph's blast radius of <paramref name="file"/> read at the PR head with data (pure; null
    /// when it does), for P5-AT5b: the file must be an entry of the at-head list of <see cref="CodeGraphRisk.Describe"/> — the text
    /// between <c>codegraph blast radius at &lt;head&gt;: </c> and the first <c>;</c> after it, so neither the default-branch list nor the
    /// hotspots nor the no-data section counts — that entry must not end <c> (no data)</c> (CodeGraph's <c>found: false</c>), and no
    /// <c>no CodeGraph data:</c> entry may name the file.
    /// </summary>
    public static string? BlastRadiusAtHeadProblem(string gate, string file)
    {
        const string AtHead = "codegraph blast radius at ";
        var start = gate.IndexOf(AtHead, StringComparison.Ordinal);
        if (start < 0)
        {
            return $"the gate row has no CodeGraph inputs: {gate}";
        }
        var list = gate.IndexOf(": ", start + AtHead.Length, StringComparison.Ordinal);
        var end = list < 0 ? -1 : gate.IndexOf(';', list);
        var atHead = list < 0 ? "" : gate[(list + 2)..(end < 0 ? gate.Length : end)];
        var entry = atHead.Split(", ").FirstOrDefault(e => e.StartsWith(file + " ", StringComparison.Ordinal));
        if (entry is null)
        {
            return $"{file} is not in the blast radius read at the head ({atHead}): {gate}";
        }
        if (entry.EndsWith(" (no data)", StringComparison.Ordinal))
        {
            return $"CodeGraph had no data for {file} at the head ({entry}): {gate}";
        }
        var noData = gate.IndexOf(CodeGraphRisk.NoDataMark, StringComparison.Ordinal);
        if (noData >= 0 && gate[noData..].Contains(file, StringComparison.Ordinal))
        {
            return $"the gate row records no CodeGraph data for {file}: {gate[noData..]}";
        }
        return null;
    }
}
