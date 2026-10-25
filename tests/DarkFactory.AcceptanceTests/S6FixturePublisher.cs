using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.AcceptanceTests;

/// <summary>Publishes data through the existing App/gateway boundaries; no source checkout, executable or owner PAT.</summary>
internal interface IS6FixtureRepository
{
    Task VerifyAuthorityAsync(CancellationToken ct);
    Task CreateBranchAsync(string branch, string? start, CancellationToken ct);
    Task<string> HeadAsync(string branch, CancellationToken ct);
    Task PutIfDifferentAsync(string branch, string path, string content, CancellationToken ct);
    Task DeleteIfUnchangedAsync(string branch, string expected, CancellationToken ct);
}

internal sealed record S6FixturePlan(string Kind, IReadOnlyDictionary<string, string> BaseFiles,
    IReadOnlyDictionary<string, string> HeadChanges, IReadOnlyList<string> Tests);
internal sealed record S6PublishedFixture(HeadlessRemoteAcceptanceTests.Fixture Fixture, string BaseBranch, string HeadBranch);
internal sealed record S6Publication(string Repository, string Campaign, IReadOnlyList<S6PublishedFixture> Rows);

/// <summary>
/// Source fixture publication, never acceptance evidence. Base targets are committed before head-only tests. Repeats use
/// the same campaign refs and skip identical files, preserving actual immutable commit identities after a lost answer.
/// </summary>
internal sealed class S6FixturePublisher(RepoRef repo, GuiWorkflowSettings settings, IS6FixtureRepository repository)
{
    internal static readonly string[] MandatoryKinds = ["hosted-ios", "gui-ios-ui", "gui-macos-apphost", "gui-test-env", "gui-host-launch-error"];

    public async Task<S6Publication> PublishAsync(string campaign, IReadOnlyList<S6FixturePlan> plans, CancellationToken ct)
    {
        if (!Regex.IsMatch(campaign, "^[0-9a-f]{32}$")) throw new ArgumentException("The fixture campaign must be 32 lowercase hex characters.");
        if (!settings.Allows(repo)) throw new InvalidOperationException("S6 fixture publication requires explicit Gate:Gui:TrustedRepos source authority.");
        if (!MandatoryKinds.Order(StringComparer.Ordinal).SequenceEqual(plans.Select(p => p.Kind).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The complete five-fixture S6 publication is required.");
        foreach (var plan in plans)
        {
            if (plan.Tests.Count == 0 || plan.HeadChanges.Count == 0) throw new InvalidOperationException("A fixture must add tests and change its head.");
            foreach (var path in plan.BaseFiles.Keys.Concat(plan.HeadChanges.Keys))
                if (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or "..")
                    || path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("factory/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture publication cannot write sealed paths or paths outside its source tree.");
        }
        // The actual default workflows and protected deployment settings are checked BEFORE the first branch/write.
        await repository.VerifyAuthorityAsync(ct);
        var rows = new List<S6PublishedFixture>();
        foreach (var plan in plans)
        {
            var prefix = $"factory/s6-{campaign}/{plan.Kind}";
            var baseBranch = prefix + "-base";
            var headBranch = prefix + "-head";
            await repository.CreateBranchAsync(baseBranch, null, ct);
            foreach (var (path, content) in plan.BaseFiles.OrderBy(f => f.Key, StringComparer.Ordinal))
                await repository.PutIfDifferentAsync(baseBranch, path, content, ct);
            var baseSha = await repository.HeadAsync(baseBranch, ct);
            if (!RemoteIosWorkflow.Sha(baseSha)) throw new InvalidOperationException("The fixture base has no immutable remote commit.");
            await repository.CreateBranchAsync(headBranch, baseSha, ct);
            foreach (var (path, content) in plan.HeadChanges.OrderBy(f => f.Key, StringComparer.Ordinal))
                await repository.PutIfDifferentAsync(headBranch, path, content, ct);
            var headSha = await repository.HeadAsync(headBranch, ct);
            if (!RemoteIosWorkflow.Sha(headSha) || headSha == baseSha) throw new InvalidOperationException("The fixture head has no distinct immutable remote commit.");
            rows.Add(new(new(plan.Kind, repo.FullName, baseSha, headSha, [.. plan.Tests]), baseBranch, headBranch));
        }
        return new(repo.FullName, campaign, rows);
    }

    /// <summary>Explicit post-run cleanup only. A receipt never grants deletion outside this campaign or over a moved ref.</summary>
    public async Task CleanupAsync(S6Publication publication, CancellationToken ct)
    {
        if (publication.Repository != repo.FullName || !Regex.IsMatch(publication.Campaign, "^[0-9a-f]{32}$")
            || !MandatoryKinds.Order(StringComparer.Ordinal).SequenceEqual(publication.Rows.Select(r => r.Fixture.Kind).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Unrecognized S6 fixture publication receipt.");
        // Validate the whole receipt before deleting anything, including branch names which came back from a caller.
        foreach (var row in publication.Rows)
        {
            var prefix = $"factory/s6-{publication.Campaign}/{row.Fixture.Kind}";
            if (row.BaseBranch != prefix + "-base" || row.HeadBranch != prefix + "-head" || row.Fixture.Repository != repo.FullName
                || !RemoteIosWorkflow.Sha(row.Fixture.Base) || !RemoteIosWorkflow.Sha(row.Fixture.Head))
                throw new InvalidOperationException("The receipt does not authorize these fixture refs.");
        }
        await repository.VerifyAuthorityAsync(ct);
        foreach (var row in publication.Rows)
        {
            await repository.DeleteIfUnchangedAsync(row.HeadBranch, row.Fixture.Head, ct);
            await repository.DeleteIfUnchangedAsync(row.BaseBranch, row.Fixture.Base, ct);
        }
    }

    internal static IReadOnlyList<S6FixturePlan> Plans()
    {
        const string projectPath = "Tally.xcodeproj/project.pbxproj";
        const string schemePath = "Tally.xcodeproj/xcshareddata/xcschemes/Tally.xcscheme";
        var seed = SwiftSeeds.Files(SwiftSeeds.IosSeed, "").ToDictionary(f => f.Path.TrimStart('/'), f => f.Content, StringComparer.Ordinal);
        string Extra(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Seeds", "headless-remote", name));
        Dictionary<string, string> Baseline(bool mac, bool ui, bool host)
        {
            var files = new Dictionary<string, string>(seed, StringComparer.Ordinal);
            if (mac) files[projectPath] = files[projectPath].Replace("SDKROOT = iphoneos;", "SDKROOT = macosx;\n\t\t\t\tENABLE_APP_SANDBOX = NO;")
                .Replace("IPHONEOS_DEPLOYMENT_TARGET = 18.0;", "MACOSX_DEPLOYMENT_TARGET = 14.0;");
            if (!host)
            {
                files[projectPath] = files[projectPath].Replace("BUNDLE_LOADER = \"$(TEST_HOST)\";", "")
                    .Replace("TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/Tally.app/$(BUNDLE_EXECUTABLE_FOLDER_PATH)/Tally\";", "");
                files["TallyTests/TallyTests.swift"] = "import XCTest\nfinal class TallyTests: XCTestCase { func testFixtureBaseline() { XCTAssertTrue(true) } }\n";
            }
            if (!ui)
            {
                var xml = XDocument.Parse(files[schemePath], LoadOptions.PreserveWhitespace);
                foreach (var target in xml.Descendants("TestableReference").Where(t => t.Descendants("BuildableReference")
                    .Any(r => (string?)r.Attribute("BlueprintName") == "TallyUITests")).ToList()) target.Remove();
                files[schemePath] = xml.ToString(SaveOptions.DisableFormatting);
            }
            return files;
        }
        string AddSource(string text, string target, string path)
        {
            var project = XcodeProject.Parse("Tally.xcodeproj", text);
            var edit = project.WithClassicSources(text, project.Targets.Single(t => t.Name == target), [path], []);
            return edit.Text ?? throw new InvalidOperationException("Cannot construct S6 fixture membership: " + edit.Problem);
        }
        const string fixedCounter = "enum Counter {\n    static let start = 0\n    static func next(_ value: Int) -> Int { value + 1 }\n}\n";
        const string unit = "import XCTest\n@testable import Tally\nfinal class IncrementTests: XCTestCase {\n    func testNextAddsOne() { XCTAssertEqual(Counter.next(41), 42) }\n}\n";
        const string uiTest = "import XCTest\nfinal class IncrementUITests: XCTestCase {\n    func testTappingIncrementShowsOne() {\n        let app = XCUIApplication()\n        app.launch()\n        let label = app.staticTexts[\"count\"]\n        XCTAssertTrue(label.waitForExistence(timeout: 30))\n        app.buttons[\"increment\"].tap()\n        XCTAssertEqual(label.label, \"Count: 1\")\n    }\n}\n";
        var hostedBase = Baseline(mac: false, ui: false, host: false);
        const string modeRecipe = "version: 1\nsteps: [{name: ready, argv: [/bin/echo, ready]}]\ntest_env: {REPO_MODE: ";
        hostedBase[".factory/prepare.yaml"] = modeRecipe + "base}\n";
        var iosUiBase = Baseline(mac: false, ui: true, host: true);
        var macBase = Baseline(mac: true, ui: false, host: true);
        var environmentBase = Baseline(mac: true, ui: false, host: true);
        environmentBase[".factory-e2e/probe-env"] = Extra("probe-env");
        environmentBase[".factory/prepare.yaml"] = Extra("base-prepare.yaml");
        var hostFailureBase = Baseline(mac: true, ui: false, host: true);
        hostFailureBase["Tally/Tally.swift"] = "// The dedicated negative fixture's entry point is .factory-e2e/HostLaunchFailure.swift.\n";
        hostFailureBase[".factory-e2e/HostLaunchFailure.swift"] = Extra("HostLaunchFailure.swift");
        hostFailureBase[projectPath] = AddSource(hostFailureBase[projectPath], "Tally", ".factory-e2e/HostLaunchFailure.swift");
        return
        [
            new("hosted-ios", hostedBase, new Dictionary<string, string>
            {
                [".factory/prepare.yaml"] = modeRecipe + "head}\n",
                ["TallyTests/HeadlessContractTests.swift"] = "import Foundation\nimport XCTest\nfinal class HeadlessContractTests: XCTestCase {\n    func testCommitOwnedEnvironment() { XCTAssertEqual(ProcessInfo.processInfo.environment[\"REPO_MODE\"], \"head\") }\n}\n"
            }, ["TallyTests/HeadlessContractTests/testCommitOwnedEnvironment()"]),
            new("gui-ios-ui", iosUiBase, new Dictionary<string, string> { ["Tally/Counter.swift"] = fixedCounter, ["TallyUITests/IncrementUITests.swift"] = uiTest },
                ["TallyUITests/IncrementUITests/testTappingIncrementShowsOne()"]),
            new("gui-macos-apphost", macBase, new Dictionary<string, string> { ["Tally/Counter.swift"] = fixedCounter, ["TallyTests/IncrementTests.swift"] = unit },
                ["TallyTests/IncrementTests/testNextAddsOne()"]),
            new("gui-test-env", environmentBase, new Dictionary<string, string>
            {
                [".factory/prepare.yaml"] = Extra("head-prepare.yaml"),
                [".factory-e2e/EnvironmentProcessTests.swift"] = Extra("EnvironmentProcessTests.swift"),
                [projectPath] = AddSource(environmentBase[projectPath], "TallyTests", ".factory-e2e/EnvironmentProcessTests.swift")
            }, ["TallyTests/EnvironmentProcessTests/testRepositoryEnvironmentReachesChild()"]),
            new("gui-host-launch-error", hostFailureBase, new Dictionary<string, string>
            {
                [".factory-e2e/HostLaunchProbeTests.swift"] = Extra("HostLaunchProbeTests.swift"),
                [projectPath] = AddSource(hostFailureBase[projectPath], "TallyTests", ".factory-e2e/HostLaunchProbeTests.swift")
            }, ["TallyTests/HostLaunchProbeTests/testHostMustActuallyLaunchBeforeThisCanCount()"])
        ];
    }
}

/// <summary>Real App transport over existing gateway-backed helpers; never executes or checks out source code locally.</summary>
internal sealed class S6AppFixtureRepository : IS6FixtureRepository, IDisposable
{
    private readonly RepoRef repo;
    private readonly GuiWorkflowSettings gui;
    private readonly HttpClient http = OutboundHttp.GitHubApi();
    private readonly SandboxRepo sandbox;
    private readonly GitWorkspace git;
    private readonly GitHubApp gate;
    public S6AppFixtureRepository(FactoryOptions options, RepoRef repo, GuiWorkflowSettings gui, string root)
    {
        (this.repo, this.gui) = (repo, gui);
        sandbox = new(options, repo);
        gate = new(http, options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem, TimeProvider.System);
        git = new(root, GitRemoteReads.GitHubRemote, async (_, ct) => await sandbox.AppTokenAsync(ct));
    }
    public void Dispose() { sandbox.Dispose(); http.Dispose(); }
    private static void FixtureBranch(string branch)
    {
        if (!Regex.IsMatch(branch, "^factory/s6-[0-9a-f]{32}/(?:hosted-ios|gui-ios-ui|gui-macos-apphost|gui-test-env|gui-host-launch-error)-(?:base|head)$"))
            throw new InvalidOperationException("The S6 transport operates on its fixture refs only.");
    }
    public async Task VerifyAuthorityAsync(CancellationToken ct)
    {
        if (!gui.Allows(repo)) throw new InvalidOperationException("The fixture source is not explicitly trusted.");
        var hosted = await new IosWorkflowGitHub(http, gate).ReadDefaultWorkflowAsync(repo, ct);
        if (hosted is null || !RemoteIosWorkflow.Trusted(hosted)) throw new InvalidOperationException("Install the exact current sealed hosted workflow before fixture publication.");
        var collector = await new IosWorkflowGitHub(http, gate, gui: true).ReadDefaultWorkflowAsync(gui.Collector, ct);
        if (collector is null || !RemoteGuiWorkflow.Trusted(collector)) throw new InvalidOperationException("Install the exact current sealed GUI collector workflow before fixture publication.");
        // Fail closed if pointed at an operational repository. Publication starts only on a dedicated fixture scaffold;
        // existing sealed workflows/docs are kept. No unrelated source/settings are silently included in a test build.
        var files = await sandbox.TreeFilesAsync(await sandbox.AppTokenAsync(ct), hosted.Commit, ct);
        if (files.Any(path => !path.StartsWith(".github/", StringComparison.Ordinal) && path is not "README.md" and not "LICENSE" and not "LICENSE.md" and not ".gitignore"))
            throw new InvalidOperationException("S6 publication requires a dedicated fixture repository containing only its workflows, README, license and gitignore on the default branch.");
    }
    public async Task CreateBranchAsync(string branch, string? start, CancellationToken ct)
    {
        FixtureBranch(branch);
        var point = await git.BranchStartAsync(repo, branch, ct);
        await git.CreateBranchAsync(repo, branch, start ?? point.BaseSha, ct);
    }
    public async Task<string> HeadAsync(string branch, CancellationToken ct)
    {
        FixtureBranch(branch);
        return await sandbox.BranchShaAsync(await sandbox.AppTokenAsync(ct), branch, ct);
    }
    public async Task PutIfDifferentAsync(string branch, string path, string content, CancellationToken ct)
    {
        FixtureBranch(branch);
        var token = await sandbox.AppTokenAsync(ct);
        var files = await sandbox.TreeFilesAsync(token, branch, ct);
        if (files.Contains(path, StringComparer.Ordinal) && await sandbox.FileTextAsync(token, branch, path, ct) == content) return;
        await sandbox.PutFileAsync(token, branch, path, content, "[dark-factory S6 fixture] " + path, ct);
    }
    public async Task DeleteIfUnchangedAsync(string branch, string expected, CancellationToken ct)
    {
        FixtureBranch(branch);
        var point = await git.BranchStartAsync(repo, branch, ct);
        if (point.Existing is null) return;
        if (point.Existing != expected) throw new InvalidOperationException("The S6 fixture ref moved; nothing deletes it: " + branch);
        await sandbox.DeleteBranchAsync(await sandbox.AppTokenAsync(ct), branch);
        if ((await git.BranchStartAsync(repo, branch, ct)).Existing is not null) throw new InvalidOperationException("S6 fixture cleanup did not remove " + branch);
    }
}

/// <summary>Automatic opt-in provisioning used by the live rows; a user supplies authority/resources, never hand-made SHAs.</summary>
internal static class S6FixtureProvisioning
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static (string Success, string Failure)? manifests;
    internal static RepoRef FixtureRepository(GuiWorkflowSettings settings, string? configured) => string.IsNullOrWhiteSpace(configured)
        ? new RepoRef(settings.Collector.Owner, "dark-factory-s6-fixtures") : RepoRef.Parse(configured);
    public static async Task<string> ManifestAsync(bool failure, CancellationToken ct)
    {
        var explicitPath = Environment.GetEnvironmentVariable(failure ? "FACTORY_E2E_S6_FAILURE_FIXTURES" : "FACTORY_E2E_S6_REMOTE_FIXTURES");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        if (Environment.GetEnvironmentVariable("FACTORY_E2E_S6_PROVISION") != "1")
            Assert.Skip("S6 fixtures are not provisioned: set FACTORY_E2E_S6_PROVISION=1 after deployment approval; the harness publishes actual pairs into the generated dark-factory-s6-fixtures repository (FACTORY_E2E_S6_FIXTURE_REPO overrides it).");
        Harness.RequireOptIn();
        await Gate.WaitAsync(ct);
        try
        {
            if (manifests is { } existing) return failure ? existing.Failure : existing.Success;
            var settings = Harness.Options.GuiWorkflow;
            if (settings is null) Assert.Skip("Automatic S6 publication needs the deployed protected GUI collector and all Gate:Gui settings.");
            var configuredRepo = Environment.GetEnvironmentVariable("FACTORY_E2E_S6_FIXTURE_REPO");
            var repo = FixtureRepository(settings!, configuredRepo);
            Harness.RequireSecret(o => o.GitHubAppId);
            Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
            Harness.RequireSecret(o => o.GitHubGateAppId);
            Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
            var campaign = Environment.GetEnvironmentVariable("FACTORY_E2E_S6_CAMPAIGN") ?? Guid.NewGuid().ToString("N");
            if (!Regex.IsMatch(campaign, "^[0-9a-f]{32}$")) throw new InvalidOperationException("FACTORY_E2E_S6_CAMPAIGN must be 32 lowercase hex characters.");
            var root = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-fixtures", campaign);
            Directory.CreateDirectory(root);
            using var transport = new S6AppFixtureRepository(Harness.Options, repo, settings!, Path.Combine(root, "clones"));
            var receipt = await new S6FixturePublisher(repo, settings!, transport).PublishAsync(campaign, S6FixturePublisher.Plans(), ct);
            var success = Path.Combine(root, "remote-fixtures.json");
            var negative = Path.Combine(root, "failure-fixtures.json");
            await SaveAsync(Path.Combine(root, "publication.json"), receipt, ct);
            await SaveAsync(success, receipt.Rows.Where(r => r.Fixture.Kind != "gui-host-launch-error").Select(r => r.Fixture).ToArray(), ct);
            await SaveAsync(negative, receipt.Rows.Where(r => r.Fixture.Kind == "gui-host-launch-error").Select(r => r.Fixture).ToArray(), ct);
            manifests = (success, negative);
            return failure ? negative : success;
        }
        finally { Gate.Release(); }
    }
    private static async Task SaveAsync<T>(string path, T value, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
