using System.Text.Json;
using DarkFactory.AcceptanceTests;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.WorkSources;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Gate;
using System.Net;

namespace DarkFactory.Orchestrator.Tests;

public class SystemOneEvaluationTests
{
    private static string CorpusPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "systemone");

    [Fact]
    public void Independently_authored_corpus_is_frozen_and_covers_every_category_and_adversarial_mechanism()
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        Assert.Equal(15, corpus.Reports.Count);
        Assert.All(IssueClassifier.Labels, category => Assert.Equal(3, corpus.Labels.Count(l => l.ExpectedCategory == category)));
        Assert.Contains(corpus.Reports, r => r.Title.Length + r.Body.Length > 4096);
        Assert.Contains(corpus.Reports, r => r.Tags.Any(t => t.Contains("injection")));
        Assert.Contains(corpus.Reports, r => r.Tags.Any(t => t.Contains("ambiguous")));
        Assert.Contains(corpus.Reports, r => r.Tags.Any(t => t.Contains("misleading")));
        Assert.Equal("1972fafabdbab74de63e81cc8f1632f736d79e32e59befc7c126b082850504b5", corpus.Identity);
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusPath, "freeze.json")));
        Assert.Contains("Entirely synthetic", freeze.RootElement.GetProperty("provenance").GetProperty("source").GetString());
        Assert.Contains("No classifier prompts", freeze.RootElement.GetProperty("provenance").GetProperty("independence").GetString());
        Assert.All(Directory.GetFiles(CorpusPath, "*", SearchOption.AllDirectories), file =>
            Assert.DoesNotContain("PRIVATE_ISSUE_BODY_SENTINEL", File.ReadAllText(file)));
        // Provenance plus the exact allowed inventory is the privacy guard; not a claim to detect arbitrary private prose.
    }

    [Theory]
    [InlineData("reports.json")]
    [InlineData("labels.json")]
    [InlineData("freeze.json")]
    [InlineData("repository/src/shelf.py")]
    [InlineData("private-body.txt")]
    public async Task Changed_or_extra_fixture_is_refused_before_runner_factory_can_create_an_inference_client(string changed)
    {
        var temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var file in Directory.GetFiles(CorpusPath, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(temporary, Path.GetRelativePath(CorpusPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            File.AppendAllText(Path.Combine(temporary, changed), "PRIVATE_ISSUE_BODY_SENTINEL");
            var constructed = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunAsync(temporary, _ =>
            {
                constructed = true;
                throw new Exception("inference client construction must not happen");
            }, CancellationToken.None));
            Assert.False(constructed);
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Fact]
    public async Task Paired_report_counts_failures_skips_routes_and_unknown_cost_without_exposing_model_text()
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var calls = new List<(string, string)>();
        var result = await SystemOneEvaluation.RunAsync(CorpusPath, _ => new DelegateEvaluationArm((report, mode, ct) =>
        {
            calls.Add((report.CaseId, mode));
            var expected = corpus.Labels.Single(l => l.CaseId == report.CaseId);
            return Task.FromResult(new EvaluationObservation(report.CaseId, mode, 100 + (mode == "advisory" ? 20 : 0),
                mode == "advisory" ? 20 : 0, expected.AcceptableAuthoritativeRoutes[0], false,
                mode == "disabled" ? null : "completed", mode == "disabled" ? null : expected.ExpectedCategory,
                mode == "disabled" ? null : 30, null, 0m, ["sha256:model"], "sha256:prompt", "sha256:repo", null));
        }), CancellationToken.None);
        Assert.Equal(30, calls.Count);
        Assert.Equal("disabled", calls[0].Item2);
        Assert.Equal("advisory", calls[2].Item2);
        Assert.Equal(15, result.Pairs.Count);
        Assert.Equal(0, result.Baseline.RoutingErrors);
        Assert.Equal(0, result.Advisory.RoutingErrors);
        Assert.Equal(300, result.DurationDeltaMs);
        Assert.Null(result.AdditionalInferenceCostUsd);
        Assert.Equal(15, result.Classification.Sum(c => c.Correct));
        Assert.False(result.OptimizationDemonstrated);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(corpus.Reports[0].Body, json);
        Assert.DoesNotContain("rationale", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FailureRate", json);
        Assert.Contains("AdditionalInferenceCostUsd", json);
    }

    [Fact]
    public async Task Both_arms_get_an_identical_real_git_snapshot_with_no_scoring_material()
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var root = Directory.CreateTempSubdirectory("df-systemone-snapshot-").FullName;
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            Assert.Equal(await corpus.MaterializeRepositoryAsync(first, CancellationToken.None), await corpus.MaterializeRepositoryAsync(second, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(first, "reports.json")));
            Assert.False(File.Exists(Path.Combine(first, "labels.json")));
            Assert.False(File.Exists(Path.Combine(first, "freeze.json")));
            var source = Path.Combine(CorpusPath, "repository");
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(first, Path.GetRelativePath(source, file))));
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => corpus.MaterializeRepositoryAsync(first, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("disabled", 0)]
    [InlineData("advisory", 1)]
    public async Task Runner_exercises_real_intake_routing_and_session_metadata_without_exposing_answers(string mode, int classifierCalls)
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var input = corpus.Reports[0];
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var contexts = new LedgerDbContextFactory(options);
        var triage = new IssueIntakeTests.FakeTriage();
        const string sentinel = "PRIVATE_ISSUE_BODY_SENTINEL";
        // Deliberately disagree with the bug classifier. Production IssueRouting, not the classifier, decides Question.
        triage.Answers.Add(() => IssueIntakeTests.FakeTriage.Says(JsonSerializer.Serialize(new
        {
            type = "question", title = "Synthetic question", summary = sentinel,
            affected_repos = new[] { "synthetic/lantern-shelf" }, confidence = .9,
        })));
        using var router = new IssueClassificationTests.Router();
        using var http = new HttpClient(router) { BaseAddress = new("http://router.test/") };
        var result = await EvaluationIntake.RunAsync(input, mode, corpus.RepositorySha,
            File.ReadAllText(Path.Combine(CorpusPath, "repository/factory/gate.yaml")), contexts, new InProcessRunLocks(),
            new LedgerControls(contexts, TimeProvider.System), triage,
            () => mode == "disabled" ? null : new IssueClassifier(http, "router-secret", "catalog-d3", mode: mode), CancellationToken.None);
        Assert.Equal(classifierCalls, router.Calls);
        Assert.Equal("Question", result.Route);
        Assert.False(result.Failed);
        Assert.Equal(mode == "advisory" ? "bug" : null, result.Category);
        Assert.Null(result.TriageCostUsd); // A fake without measured accounting is NOT a free real session.
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(result));
        var prompt = Assert.Single(triage.Prompts);
        Assert.Contains(input.Body, prompt);
        Assert.DoesNotContain(corpus.Labels[0].ExpectedCategory + " rationale", prompt);
        Assert.DoesNotContain("repositoryEvidence", prompt);
        Assert.Equal(mode == "advisory", prompt.Contains("<classification-hint>"));
        await using var db = contexts.CreateDbContext();
        Assert.Single(await db.SessionTaints.ToListAsync());
        Assert.Contains(await db.LedgerEntries.ToListAsync(), r => r.Step == IssueSteps.TriageSession);
    }

    [Fact]
    public void Evaluation_turn_budget_is_explicit_and_normal_workers_are_unchanged()
    {
        Assert.DoesNotContain("--max-turns", DarkFactory.Orchestrator.Worker.ClaudeWorker.BuildArguments("Synthetic"));
        var args = DarkFactory.Orchestrator.Worker.ClaudeWorker.BuildArguments("Synthetic", maxTurns: 12).ToList();
        Assert.Equal("12", args[args.IndexOf("--max-turns") + 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => DarkFactory.Orchestrator.Worker.ClaudeWorker.BuildArguments("Synthetic", maxTurns: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123456)]
    public async Task Classifier_cost_uses_the_same_correlated_router_session_and_accepts_measured_zero(long micros)
    {
        var session = Guid.NewGuid().ToString();
        using var api = new FakeApi().On("POST /v1/systemone", HttpStatusCode.OK, IssueClassificationTests.Reply("bug"))
            .On($"GET /v1/sessions/{session}/cost", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { session_id = session, request_count = 1, actual_cost_usd_micros = micros }));
        using var http = api.Client("http://router.test/");
        http.DefaultRequestHeaders.Add(RouterReviewer.SessionHeader, session);
        var classifier = new IssueClassifier(http, "router-secret", "catalog-d3", mode: "advisory");
        await classifier.ClassifyAsync(1, "synthetic-version", "Synthetic", "Synthetic body", CancellationToken.None);
        var cost = await EvaluationIntake.ReadClassifierCostAsync(new RouterClient(http, "router-secret"), session, CancellationToken.None, [TimeSpan.Zero]);
        Assert.Equal(micros, cost!.ActualCostUsdMicros);
        Assert.Equal(1, cost.RequestCount);
        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, request =>
        {
            Assert.Equal(session, request.Headers[RouterReviewer.SessionHeader]);
            Assert.Equal("router-secret", request.Headers["X-Weave-Router-Key"]);
            Assert.False(request.Headers.ContainsKey("x-weave-model-class"));
        });
    }

    [Theory]
    [InlineData(404, 0)]
    [InlineData(200, 0)]
    [InlineData(503, 0)]
    public async Task Unrecorded_or_failed_accounting_is_unknown_not_free(int status, long requests)
    {
        var session = Guid.NewGuid().ToString();
        using var api = new FakeApi().On($"GET /v1/sessions/{session}/cost", (HttpStatusCode)status,
            JsonSerializer.Serialize(new { session_id = session, request_count = requests, actual_cost_usd_micros = 0 }));
        using var http = api.Client("http://router.test/");
        Assert.Null(await EvaluationIntake.ReadClassifierCostAsync(new RouterClient(http, "key"), session, CancellationToken.None, [TimeSpan.Zero]));
    }

    [Fact]
    public async Task Actual_sandbox_triage_seat_cannot_run_the_campaign_unsandboxed()
    {
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Worker:RunAs"] = "none", ["Router:Key"] = "synthetic-key" }).Build(), new InMemorySecrets(), _ => null);
        var runner = new SandboxTriageRunner(options, TextWriter.Null) { EvaluationSnapshot = CorpusPath };
        var item = new WorkItem { ExternalId = "gh-1", Source = "github", Title = "Synthetic", Repo = "synthetic/lantern-shelf" };
        await Assert.ThrowsAsync<FactoryUnavailableException>(() => runner.RunAsync(item, RepoRef.Parse("synthetic/lantern-shelf"), "Synthetic",
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public void Report_does_not_hide_classifier_unavailability_or_route_regressions_in_success_averages()
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var observations = corpus.Reports.SelectMany(r => new[]
        {
            new EvaluationObservation(r.CaseId, "disabled", 100, 0, corpus.Labels.Single(l => l.CaseId == r.CaseId).AcceptableAuthoritativeRoutes[0], false, null, null, null, null, 0m, [], "p", "r", null),
            new EvaluationObservation(r.CaseId, "advisory", 50, 5, "Build", true, "timeout", null, null, null, null, [], "p", "r", null)
        }).ToList();
        var result = EvaluationReport.Create(corpus, observations);
        Assert.Equal(1, result.Advisory.FailureRate);
        Assert.True(result.Advisory.RoutingErrors > 0);
        Assert.All(result.Classification, c => { Assert.Equal(3, c.Total); Assert.Equal(0, c.Answered); });
        Assert.Equal(15, result.ClassifierUnavailable);
        Assert.False(result.OptimizationDemonstrated);
        Assert.Null(result.AdditionalInferenceCostUsd);
        var measured = observations.Select(o => o with { TriageCostUsd = o.Mode == "disabled" ? 1m : 1.1m, ClassifierCostUsd = 0.02m }).ToList();
        Assert.Equal(1.8m, EvaluationReport.Create(corpus, measured).AdditionalInferenceCostUsd);
        measured[0] = measured[0] with { RepositorySha = "different-snapshot" };
        Assert.Throws<InvalidDataException>(() => EvaluationReport.Create(corpus, measured));
    }
}
