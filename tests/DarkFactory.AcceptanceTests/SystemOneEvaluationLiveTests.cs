using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.AcceptanceTests;

public class SystemOneEvaluationLiveTests
{
    /// <summary>
    /// S6's once-at-the-end campaign entry point. S5 builds/tests it without running inference.
    /// 15 counterbalanced pairs, 30 fresh sandbox triage sessions (2-minute/12-turn limit each), <=15 classifier requests;
    /// no model retry campaign, no real issue-board posts, no implementation worker or pushes.
    /// </summary>
    [Fact]
    public async Task Frozen_corpus_paired_disabled_vs_advisory_live_campaign()
    {
        Harness.RequireOptIn();
        if (Environment.GetEnvironmentVariable("FACTORY_E2E_SYSTEMONE_EVALUATION") != "1")
        { Assert.Skip("Set FACTORY_E2E_SYSTEMONE_EVALUATION=1 for the held-out live campaign (spends router tokens)."); }
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "systemone");
        // Validate before E2e, router prerequisite reads, sandbox probes or inference-client construction.
        EvaluationCorpus.Load(root);
        var output = Path.GetFullPath(Harness.RequireEnv("FACTORY_E2E_SYSTEMONE_REPORT", "a NEW JSON output file outside the frozen corpus"));
        Assert.False(output.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        // Reserve without overwriting earlier evidence. Nothing writes raw transcripts to this export.
        using var reportFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var model = Harness.RequireEnv("Issues__Classification__Model", "the SystemOne catalog identifier; workers retain production low-class routing");
        // SandboxTriageRunner itself refuses an unsandboxed triage (covered by a non-live self-test).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(100));
        var result = await SystemOneEvaluation.RunAsync(root, corpus => new LiveEvaluationArm(corpus, model), timeout.Token);
        await JsonSerializer.SerializeAsync(reportFile, result, new JsonSerializerOptions { WriteIndented = true }, timeout.Token);
        Assert.Equal(15, result.Pairs.Count);
        // Failed performance/quality goals remain measured evidence, not a reason to relabel or rerun the corpus.
        Console.WriteLine($"SystemOne paired report: corpus {result.CorpusIdentity}; {result.Pairs.Count} pairs; optimization not established.");
    }
}

internal sealed class LiveEvaluationArm(EvaluationCorpus corpus, string model) : IEvaluationArm
{
    public async Task<EvaluationObservation> RunAsync(EvaluationInput input, string mode, CancellationToken ct)
    {
        // Every arm gets its own migrated database, controls, work root, checkout and fresh Claude session.
        await using var e2e = await E2e.StartAsync("df_systemone_eval", ItemNaming.GitHubIssue);
        var options = e2e.Options(new Dictionary<string, string?>
        {
            ["GitHub:Watch:Repos"] = input.PermissionContext.IssueRepository,
            ["Issues:Classification:Mode"] = mode,
            ["Issues:Classification:Model"] = model,
            ["Issues:Classification:MaxInputChars"] = "4096",
            ["Issues:Classification:TimeoutSeconds"] = "5",
            ["Worker:TimeoutMinutes"] = "2",
        });
        var snapshot = Path.Combine(e2e.WorkRoot, "synthetic-snapshot");
        var sha = await corpus.MaterializeRepositoryAsync(snapshot, ct);
        var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(e2e.Ledger));
        var triage = new SandboxTriageRunner(options, TextWriter.Null) { EvaluationSnapshot = snapshot };
        // Disabled baseline constructs no classifier client at all. Advisory uses the normal outbound gateway and router key.
        using var http = mode == "disabled" ? null : OutboundHttp.RouterApi(options.RouterBaseUrl);
        var classifierSession = mode == "disabled" ? null : Guid.NewGuid().ToString();
        if (http is not null) { http.DefaultRequestHeaders.Add(RouterReviewer.SessionHeader, classifierSession); }
        return await EvaluationIntake.RunAsync(input, mode, sha,
            await File.ReadAllTextAsync(Path.Combine(snapshot, "factory", "gate.yaml"), ct), contexts,
            new PostgresRunLocks(e2e.Ledger), new LedgerControls(contexts, TimeProvider.System), triage,
            () => mode == "disabled" ? null : IssueClassifier.Create(options, http!, () => options.RouterKey), ct,
            classifierSession, http is null ? null : new RouterClient(http, options.RouterKey));
    }

}
