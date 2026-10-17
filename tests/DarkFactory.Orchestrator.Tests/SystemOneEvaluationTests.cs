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
                mode == "disabled" ? null : 30, null, 0m, new Dictionary<string, int> { ["sha256:model"] = 1 }, "sha256:prompt", "sha256:repo", null));
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
            new EvaluationObservation(r.CaseId, "disabled", 100, 0, corpus.Labels.Single(l => l.CaseId == r.CaseId).AcceptableAuthoritativeRoutes[0], false, null, null, null, null, 0m, new Dictionary<string, int>(), "p", "r", null),
            new EvaluationObservation(r.CaseId, "advisory", 50, 5, "Build", true, "timeout", null, null, null, null, new Dictionary<string, int>(), "p", "r", null)
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

    [Theory]
    [InlineData("null")]
    [InlineData("17")]
    [InlineData("\"PRIVATE_MODEL_STDOUT\"")]
    [InlineData("[]")]
    public void Nonobject_stdout_roots_are_ignored_without_aborting_evidence(string line) =>
        Assert.Empty(EvaluationIntake.ReadModelAllocation([line]));

    [Theory]
    [InlineData("null")]
    [InlineData("17")]
    [InlineData("\"PRIVATE_MODEL_STDOUT\"")]
    [InlineData("[]")]
    public void Nonobject_nested_messages_are_ignored_without_aborting_evidence(string message) =>
        Assert.Empty(EvaluationIntake.ReadModelAllocation(["{\"type\":\"assistant\",\"message\":" + message + "}"]));

    private static string Assistant(string id, string model) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id, model, content = "PRIVATE_MODEL_STDOUT" } });

    [Fact]
    public void Assistant_message_chunks_are_deduplicated_and_only_hashed_counts_are_exported()
    {
        var counts = EvaluationIntake.ReadModelAllocation([Assistant("a", "PRIVATE_MODEL_A"), Assistant("a", "PRIVATE_MODEL_A"), Assistant("b", "PRIVATE_MODEL_A")]);
        Assert.Equal(new Dictionary<string, int> { [EvaluationCorpus.Hash("PRIVATE_MODEL_A")] = 2 }, counts);
    }

    [Fact]
    public void Changed_model_allocation_is_a_confound_even_when_model_sets_are_identical()
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var observations = corpus.Reports.SelectMany(r => new[]
        {
            Sample(r, "disabled") with { ServedModelMessageCounts = new Dictionary<string, int> { [EvaluationCorpus.Hash("A")] = 1, [EvaluationCorpus.Hash("B")] = 10 } },
            Sample(r, "advisory") with { ServedModelMessageCounts = new Dictionary<string, int> { [EvaluationCorpus.Hash("A")] = 10, [EvaluationCorpus.Hash("B")] = 1 } },
        }).ToList();
        Assert.All(EvaluationReport.Create(corpus, observations).Pairs, pair => Assert.True(pair.ServedModelsDiffer));
    }

    [Fact]
    public void Missing_model_identity_is_an_explicit_unknown_confound()
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var observations = corpus.Reports.SelectMany(r => new[] { Sample(r, "disabled"), Sample(r, "advisory") }).ToList();
        Assert.All(EvaluationReport.Create(corpus, observations).Pairs, pair => Assert.Null(pair.ServedModelsDiffer));
    }

    [Theory]
    [InlineData("{\"id\":\"a\"}")]
    [InlineData("{\"model\":\"PRIVATE_MODEL_A\"}")]
    public void An_assistant_identity_gap_makes_the_allocation_unknown_instead_of_partial(string message) =>
        Assert.Empty(EvaluationIntake.ReadModelAllocation([Assistant("first", "PRIVATE_MODEL_A"), "{\"type\":\"assistant\",\"message\":" + message + "}"]));

    private sealed class TeardownFailureArm : IEvaluationArm
    {
        public async Task RunAsync(EvaluationInput report, string mode, Func<EvaluationObservation, Task> completed, CancellationToken ct)
        {
            await completed(Sample(report, mode));
            throw new InvalidOperationException("Simulated database teardown failure after an arm completed");
        }
    }

    [Fact]
    public async Task Completed_arm_is_durable_even_when_its_ledger_teardown_fails()
    {
        var directory = NewCancellationArtifactDirectory(); // Not CreateTempSubdirectory: macOS /var is a symlinked ancestor, which the artifact guard refuses.
        var output = Path.Combine(directory, "evidence.json");
        try
        {
            try { await SystemOneEvaluation.RunToFileAsync(CorpusPath, output, _ => new TeardownFailureArm(), CancellationToken.None); }
            catch (InvalidOperationException) { }
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(await File.ReadAllTextAsync(output))!;
            Assert.Equal(("incomplete", 1, (EvaluationReport?)null), (evidence.Status, evidence.Observations.Count, evidence.Summary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Prerequisite_skip_after_one_arm_is_a_non_skip_failure_without_retry_or_summary()
    {
        var directory = NewCancellationArtifactDirectory();
        var output = Path.Combine(directory, "evidence.json");
        var calls = 0;
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => new DelegateEvaluationArm((input, mode, _) => ++calls == 1
                    ? Task.FromResult(Sample(input, mode))
                    : throw Xunit.Sdk.SkipException.ForSkip("PRIVATE_ROUTER_BODY_KEY")), CancellationToken.None));
            Assert.Contains("prerequisite", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PRIVATE_ROUTER_BODY_KEY", failure.ToString());
            Assert.Equal(2, calls);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(await File.ReadAllTextAsync(output))!;
            Assert.Equal(("incomplete", 1, (EvaluationReport?)null), (evidence.Status, evidence.Observations.Count, evidence.Summary));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class OutageArm(string parent, List<string> trace) : IEvaluationArm
    {
        private int calls;
        public async Task RunAsync(EvaluationInput input, string mode, Func<EvaluationObservation, Task> completed, CancellationToken ct)
        {
            if (++calls == 2)
            {
                await EvaluationSetup.RequireRouterAsync(_ => throw new HttpRequestException("PRIVATE_ROUTER_BODY_KEY"), ct);
            }
            var root = EvaluationWorkRoot.Create(parent, "arm-" + calls);
            try
            {
                await root.RunAsync(() => { trace.Add("runner-stopped"); return Task.CompletedTask; });
                await completed(Sample(input, mode));
                trace.Add("checkpoint");
            }
            finally
            {
                await root.DisposeAsync(() => { trace.Add("ledger"); return Task.CompletedTask; },
                    new CallbackDisposable(() => { Assert.False(Directory.Exists(root.Path)); trace.Add("unlock"); }));
            }
        }
    }

    [Fact]
    public async Task Router_outage_after_one_arm_preserves_incomplete_evidence_and_unwinds_owned_resources_in_order()
    {
        var parent = NewCancellationArtifactDirectory();
        var output = Path.Combine(parent, "evidence.json");
        var trace = new List<string>();
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => new OutageArm(parent, trace), CancellationToken.None));
            Assert.Contains("router", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PRIVATE_ROUTER_BODY_KEY", failure.ToString());
            Assert.Equal(new[] { "runner-stopped", "checkpoint", "ledger", "unlock" }, trace);
            Assert.Empty(Directory.GetDirectories(parent));
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(await File.ReadAllTextAsync(output))!;
            Assert.Equal(("incomplete", 1, (EvaluationReport?)null), (evidence.Status, evidence.Observations.Count, evidence.Summary));
        }
        finally { Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task Evaluation_root_removes_only_its_owned_tree_after_runner_and_ledger_before_unlock()
    {
        var parent = NewCancellationArtifactDirectory();
        var trace = new List<string>();
        try
        {
            var root = EvaluationWorkRoot.Create(parent, "arm", workerSlots: 2);
            var foreign = Directory.CreateDirectory(Path.Combine(parent, "foreign")).FullName;
            var sentinel = Path.Combine(foreign, "sentinel");
            await File.WriteAllTextAsync(sentinel, "owner");
            var nested = Directory.CreateDirectory(Path.Combine(root.Path, "nested")).FullName;
            await File.WriteAllTextAsync(Path.Combine(nested, "owned"), "inert");
            Directory.CreateSymbolicLink(Path.Combine(nested, "outside"), foreign);
            File.CreateSymbolicLink(Path.Combine(nested, "file-link"), sentinel);
            File.CreateSymbolicLink(Path.Combine(nested, "dangling"), Path.Combine(foreign, "missing"));
            await root.RunAsync(async () => { await Task.Yield(); trace.Add("runner-stopped"); });
            await root.DisposeAsync(() =>
            {
                Assert.True(Directory.Exists(root.Path));
                Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(root.Path));
                Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(root.Path, 2));
                trace.Add("ledger");
                return Task.CompletedTask;
            }, new CallbackDisposable(() =>
            {
                Assert.False(Directory.Exists(root.Path));
                trace.Add("unlock");
            }));
            Assert.Equal(new[] { "runner-stopped", "ledger", "unlock" }, trace);
            Assert.Equal("owner", await File.ReadAllTextAsync(sentinel));
            Assert.Throws<IOException>(() => EvaluationWorkRoot.Create(parent, "foreign"));
            Assert.Throws<InvalidDataException>(() => EvaluationWorkRoot.Create(parent, "../foreign"));
        }
        finally { Directory.Delete(parent, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluation_root_rejects_linked_ancestor_or_replaced_root_and_releases_safe_locks(bool ancestor)
    {
        var parent = NewCancellationArtifactDirectory();
        var released = false;
        try
        {
            var root = EvaluationWorkRoot.Create(parent, "arm");
            var outside = Directory.CreateDirectory(Path.Combine(parent, "foreign")).FullName;
            var sentinel = Path.Combine(outside, "sentinel");
            await File.WriteAllTextAsync(sentinel, "owner");
            var alias = Path.Combine(parent, "alias");
            Directory.CreateSymbolicLink(alias, outside);
            Assert.Throws<InvalidDataException>(() => EvaluationWorkRoot.Create(alias, "child"));
            if (ancestor)
            {
                Directory.Move(root.Path, Path.Combine(parent, "saved"));
                Directory.CreateSymbolicLink(root.Path, outside);
            }
            var failure = await Record.ExceptionAsync(() => root.DisposeAsync(() => throw new IOException("fake-ledger-teardown"),
                new CallbackDisposable(() => released = true)));
            if (ancestor) { Assert.IsType<InvalidOperationException>(failure); }
            else { Assert.IsType<IOException>(failure); }
            Assert.Equal(!ancestor, released); // Unknown quiescence at a replaced root retains the machine locks.
            Assert.Equal("owner", await File.ReadAllTextAsync(sentinel));
            if (ancestor) { Assert.NotNull(new DirectoryInfo(root.Path).LinkTarget); }
            else { Assert.False(Directory.Exists(root.Path)); }
        }
        finally { Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task Evaluation_root_cleanup_failure_surfaces_and_unlocks_but_unstopped_worker_retains_everything()
    {
        var parent = NewCancellationArtifactDirectory();
        var released = false;
        var ledger = false;
        try
        {
            var root = EvaluationWorkRoot.Create(parent, "arm");
            var foreign = Directory.CreateDirectory(Path.Combine(parent, "foreign")).FullName;
            await Assert.ThrowsAsync<InvalidDataException>(() => root.DisposeAsync(() =>
            {
                // A fake deletion failure after quiescence is established must still release locks.
                Directory.Move(root.Path, Path.Combine(parent, "saved"));
                Directory.CreateSymbolicLink(root.Path, foreign);
                return Task.CompletedTask;
            }, new CallbackDisposable(() => released = true)));
            Assert.True(released);
            var running = EvaluationWorkRoot.Create(parent, "running");
            released = false;
            var triage = new IssueIntakeTests.FakeTriage();
            triage.Answers.Add(() => throw DarkFactory.Orchestrator.Worker.WorkerStillRunning.Mark(new InvalidOperationException("fake-worker-running")));
            // Simulate intake swallowing the exception: the guarded runner must remember it independently.
            await Assert.ThrowsAsync<InvalidOperationException>(() => running.Guard(triage).RunAsync(new WorkItem { ExternalId = "gh-1", Source = "github", Title = "Synthetic", Repo = "synthetic/repo" },
                RepoRef.Parse("synthetic/repo"), "synthetic", (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => running.DisposeAsync(() => { ledger = true; return Task.CompletedTask; },
                new CallbackDisposable(() => released = true)));
            Assert.False(ledger);
            Assert.False(released);
            Assert.True(Directory.Exists(running.Path));
        }
        finally { Directory.Delete(parent, true); } // Inert fake only; no worker exists.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Evaluation_teardown_refuses_an_isolated_slot_still_held_without_a_worker_exception_marker(int busySlot)
    {
        var parent = NewCancellationArtifactDirectory();
        var released = false;
        var ledgerDropped = false;
        try
        {
            var root = EvaluationWorkRoot.Create(parent, "arm", workerSlots: 2);
            using var busy = WorkerLock.Acquire(root.Path, busySlot); // Inert real file lock; no process/helper.
            // The outer production call can hide a marker or return cancellation; the underlying hold is the authority.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.RunAsync(() => throw new OperationCanceledException()));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => root.DisposeAsync(
                () => { ledgerDropped = true; return Task.CompletedTask; }, new CallbackDisposable(() => released = true)));
            Assert.Contains("retained", failure.Message);
            Assert.False(ledgerDropped);
            Assert.False(released);
            Assert.True(Directory.Exists(root.Path));
            if (busySlot == 2)
            {
                // An unsuccessful acquire-all releases its own partial hold, never the real machine locks.
                using var first = WorkerLock.Acquire(root.Path, 1);
            }
        }
        finally { Directory.Delete(parent, true); }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluation_router_outage_fails_with_sanitized_diagnostic_and_preserves_caller_cancellation(bool cancelled)
    {
        using var cancel = new CancellationTokenSource();
        if (cancelled) { cancel.Cancel(); }
        Task Probe(CancellationToken ct) => cancelled ? Task.FromCanceled(ct) : throw new HttpRequestException("PRIVATE_BODY_KEY_URL");
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvaluationSetup.RequireRouterAsync(Probe, cancel.Token));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => EvaluationSetup.RequireRouterAsync(Probe, cancel.Token));
            Assert.Contains("router", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PRIVATE_BODY_KEY_URL", failure.ToString());
        }
    }

    private static EvaluationObservation Sample(EvaluationInput input, string mode) =>
        new(input.CaseId, mode, 1, 0, "Question", false, null, null, null, null, null,
            new Dictionary<string, int>(), "prompt-sha", "repository-sha", null);

    [Theory]
    [InlineData(0, "setup")]
    [InlineData(1, "cancel")]
    [InlineData(3, "skip")]
    [InlineData(4, "database")]
    public async Task Every_completed_arm_survives_campaign_interruption_with_an_explicit_incomplete_report(int completed, string failure)
    {
        var directory = NewCancellationArtifactDirectory(); // Not CreateTempSubdirectory: macOS /var is a symlinked ancestor, which the artifact guard refuses.
        var output = Path.Combine(directory, "evidence.json");
        using var cancel = new CancellationTokenSource();
        var count = 0;
        try
        {
            try
            {
                await SystemOneEvaluation.RunToFileAsync(CorpusPath, output, _ => new DelegateEvaluationArm((input, mode, ct) =>
                {
                    if (count++ == completed)
                    {
                        if (failure == "cancel") { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
                        if (failure == "skip") { throw Xunit.Sdk.SkipException.ForSkip("PRIVATE_SETUP_DETAIL"); }
                        throw new InvalidOperationException("PRIVATE_SETUP_DETAIL");
                    }
                    return Task.FromResult(Sample(input, mode));
                }), cancel.Token);
            }
            catch (Exception) { } // The evidence, not exception text, is the assertion's observable.
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(await File.ReadAllTextAsync(output))!;
            Assert.Equal(("incomplete", completed, 30, (EvaluationReport?)null),
                (evidence.Status, evidence.Observations.Count, evidence.ExpectedArms, evidence.Summary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Final_checkpoint_contains_complete_paired_summary_and_cannot_overwrite_earlier_evidence()
    {
        var directory = NewCancellationArtifactDirectory(); // Not CreateTempSubdirectory: macOS /var is a symlinked ancestor, which the artifact guard refuses.
        var output = Path.Combine(directory, "evidence.json");
        try
        {
            await SystemOneEvaluation.RunToFileAsync(CorpusPath, output, _ => new DelegateEvaluationArm((input, mode, _) => Task.FromResult(Sample(input, mode))), CancellationToken.None);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(await File.ReadAllTextAsync(output))!;
            Assert.Equal(("complete", 30, 15), (evidence.Status, evidence.Observations.Count, evidence.Summary?.Pairs.Count));
            await Assert.ThrowsAsync<IOException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output, _ => throw new Exception("must not construct a client"), CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("invalid_response")]
    [InlineData("http_error")]
    [InlineData("skipped_oversize")]
    public async Task Failed_and_skipped_classification_preserves_the_persisted_requested_identity_separately_from_served_model(string status)
    {
        var corpus = EvaluationCorpus.Load(CorpusPath);
        var input = status == "skipped_oversize" ? corpus.Reports.Single(r => r.Title.Length + r.Body.Length > 4096) : corpus.Reports[0];
        var contexts = new LedgerDbContextFactory(new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var triage = new IssueIntakeTests.FakeTriage();
        triage.Answers.Add(() => IssueIntakeTests.FakeTriage.Says("{\"type\":\"question\",\"title\":\"Synthetic\",\"summary\":\"Synthetic\",\"confidence\":0.9}"));
        using var router = new IssueClassificationTests.Router
        {
            Answer = "null",
            Respond = status == "http_error" ? _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : status == "timeout" ? _ => throw new TaskCanceledException() : null,
        };
        using var http = new HttpClient(router) { BaseAddress = new("http://router.test/") };
        var observation = await EvaluationIntake.RunAsync(input, "advisory", corpus.RepositorySha,
            File.ReadAllText(Path.Combine(CorpusPath, "repository/factory/gate.yaml")), contexts, new InProcessRunLocks(),
            new LedgerControls(contexts, TimeProvider.System), triage,
            () => new IssueClassifier(http, "router-secret", "PRIVATE_REQUESTED_MODEL", mode: "advisory"), CancellationToken.None);
        Assert.Equal((status, EvaluationCorpus.Hash("PRIVATE_REQUESTED_MODEL"), (string?)null),
            (observation.ClassifierStatus, observation.ClassifierRequestedModelSha, observation.ClassifierModelSha));
    }

    private static string NewCancellationArtifactDirectory()
    {
        // macOS /var is a symlink: exercise the same canonical external-artifact rule as live callers.
        var root = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        return Directory.CreateDirectory(Path.Combine(root, "df-evaluation-cancel-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    [Theory]
    [InlineData("file")]
    [InlineData("caller")]
    public async Task Evaluation_cancel_interrupts_running_arm_and_drains_cleanup(string trigger)
    {
        var directory = NewCancellationArtifactDirectory();
        var output = Path.Combine(directory, "evidence.json");
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var tornDown = false;
        var calls = 0;
        var campaign = SystemOneEvaluation.RunToFileAsync(CorpusPath, output, _ => new DelegateEvaluationArm(async (input, mode, ct) =>
        {
            if (++calls == 1) { return Sample(input, mode); }
            try
            {
                try { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally
                {
                    cleanupEntered.SetResult();
                    await allowCleanup.Task;
                    cleaned = true; // Fake sandbox finally: no helper, probe or process signal.
                }
            }
            finally { tornDown = cleaned; } // Fake ledger/lock teardown follows runner cleanup.
            return Sample(input, mode);
        }), caller.Token, output + ".cancel");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (trigger == "file") { File.WriteAllText(output + ".cancel", "body is never read"); }
            else { caller.Cancel(); }
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(campaign.IsCompleted);
            Assert.False(tornDown);
            allowCleanup.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => campaign.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(cleaned);
            Assert.True(tornDown);
            Assert.Equal(2, calls);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(File.ReadAllText(output))!;
            Assert.Equal(("incomplete", 1, (EvaluationReport?)null), (evidence.Status, evidence.Observations.Count, evidence.Summary));
        }
        finally
        {
            caller.Cancel(); allowCleanup.TrySetResult();
            try { await campaign; } catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Evaluation_preexisting_cancel_preserves_initial_evidence_before_client_construction(bool file)
    {
        var directory = NewCancellationArtifactDirectory();
        var output = Path.Combine(directory, "evidence.json");
        using var caller = new CancellationTokenSource();
        var constructed = false;
        try
        {
            if (file) { File.WriteAllText(output + ".cancel", ""); } else { caller.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => { constructed = true; throw new Exception("must not construct clients"); }, caller.Token, output + ".cancel"));
            Assert.False(constructed);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(File.ReadAllText(output))!;
            Assert.Equal(("incomplete", 0, 30, (EvaluationReport?)null),
                (evidence.Status, evidence.Observations.Count, evidence.ExpectedArms, evidence.Summary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task Evaluation_cancel_at_arm_boundary_never_starts_another_arm_or_completes_campaign(int stopAfter)
    {
        var directory = NewCancellationArtifactDirectory();
        var output = Path.Combine(directory, "evidence.json");
        var calls = 0;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => new DelegateEvaluationArm((input, mode, _) =>
                {
                    if (++calls == stopAfter) { File.WriteAllText(output + ".cancel", ""); }
                    return Task.FromResult(Sample(input, mode));
                }), CancellationToken.None, output + ".cancel"));
            Assert.Equal(stopAfter, calls);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(File.ReadAllText(output))!;
            Assert.Equal(("incomplete", stopAfter, (EvaluationReport?)null), (evidence.Status, evidence.Observations.Count, evidence.Summary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Evaluation_cancel_watch_finishes_normally_before_artifact_teardown()
    {
        var directory = NewCancellationArtifactDirectory();
        var output = Path.Combine(directory, "evidence.json");
        try
        {
            await SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => new DelegateEvaluationArm((input, mode, _) => Task.FromResult(Sample(input, mode))), CancellationToken.None, output + ".cancel");
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(File.ReadAllText(output))!;
            Assert.Equal(("complete", 30, 15), (evidence.Status, evidence.Observations.Count, evidence.Summary?.Pairs.Count));
            File.WriteAllText(output + ".cancel", "");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Evaluation_cancel_file_is_scoped_to_its_own_external_artifact()
    {
        var directory = NewCancellationArtifactDirectory();
        try
        {
            var output = Path.Combine(directory, "evidence.json");
            foreach (var flag in new[] { Path.Combine(directory, "shared.cancel"), Path.Combine(CorpusPath, "stop") })
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                    _ => throw new Exception("must not construct clients"), CancellationToken.None, flag));
                Assert.False(File.Exists(output));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Evaluation_session_artifacts_inside_owner_checkout_are_allowed_without_overwriting_evidence()
    {
        var owner = NewCancellationArtifactDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(owner, ".git"));
            var artifacts = Directory.CreateDirectory(Path.Combine(owner, ".claude", "session", "artifacts")).FullName;
            var output = Path.Combine(artifacts, "evidence.json");
            var workers = Path.Combine(owner, "worker-root");
            await SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => new DelegateEvaluationArm((input, mode, _) => Task.FromResult(Sample(input, mode))),
                CancellationToken.None, output + ".cancel", workers);
            var original = File.ReadAllText(output);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(original)!;
            Assert.Equal(("complete", 30), (evidence.Status, evidence.Observations.Count));
            await Assert.ThrowsAsync<IOException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => throw new Exception("must not construct clients"), CancellationToken.None, output + ".cancel", workers));
            Assert.Equal(original, File.ReadAllText(output));
        }
        finally { Directory.Delete(owner, true); }
    }

    [Theory]
    [InlineData("worker-root")]
    [InlineData("worker-root/e2e/isolated/synthetic-snapshot")]
    [InlineData("scoring")]
    [InlineData(".git")]
    [InlineData(".git/worktrees/session")]
    public async Task Evaluation_actual_composition_protects_worker_fixture_and_metadata_before_clients(string location)
    {
        var owner = NewCancellationArtifactDirectory();
        var constructed = false;
        try
        {
            var corpus = Path.Combine(owner, "scoring");
            foreach (var file in Directory.GetFiles(CorpusPath, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(corpus, Path.GetRelativePath(CorpusPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            var artifacts = Directory.CreateDirectory(Path.Combine(owner, location)).FullName;
            var output = Path.Combine(artifacts, "evidence.json");
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(corpus, output,
                _ => { constructed = true; throw new Exception("must not construct clients"); },
                CancellationToken.None, output + ".cancel", Path.Combine(owner, "worker-root")));
            Assert.False(constructed);
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(owner, true); }
    }

    [Fact]
    public async Task Evaluation_worker_root_alias_cannot_hide_materialized_repository_from_guard()
    {
        var owner = NewCancellationArtifactDirectory();
        var constructed = false;
        try
        {
            var workers = Directory.CreateDirectory(Path.Combine(owner, "worker-root")).FullName;
            var alias = Path.Combine(owner, "worker-alias");
            Directory.CreateSymbolicLink(alias, workers);
            var output = Path.Combine(workers, "evidence.json");
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => { constructed = true; throw new Exception("must not construct clients"); },
                CancellationToken.None, output + ".cancel", alias));
            Assert.False(constructed);
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(owner, true); }
    }

    [Fact]
    public async Task Evaluation_owner_session_artifact_symlink_is_refused_before_clients()
    {
        var owner = NewCancellationArtifactDirectory();
        var constructed = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(owner, ".git"));
            var artifacts = Directory.CreateDirectory(Path.Combine(owner, "artifacts")).FullName;
            var link = Path.Combine(owner, "linked-artifacts");
            Directory.CreateSymbolicLink(link, artifacts);
            var output = Path.Combine(link, "evidence.json");
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => { constructed = true; throw new Exception("must not construct clients"); },
                CancellationToken.None, output + ".cancel", Path.Combine(owner, "worker-root")));
            Assert.False(constructed);
            Assert.False(File.Exists(Path.Combine(artifacts, "evidence.json")));
        }
        finally { Directory.Delete(owner, true); }
    }

    // No cancellation flag: the artifact-location rules still run (they are not flag configuration).
    [Theory]
    [InlineData("worker-root")]
    [InlineData("worker-root/e2e/isolated/synthetic-snapshot")]
    [InlineData("scoring")]
    [InlineData(".git")]
    [InlineData(".git/worktrees/session")]
    public async Task Evaluation_null_cancellation_flag_still_refuses_protected_artifact_locations_before_clients(string location)
    {
        var owner = NewCancellationArtifactDirectory();
        var constructed = false;
        try
        {
            var corpus = Path.Combine(owner, "scoring");
            foreach (var file in Directory.GetFiles(CorpusPath, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(corpus, Path.GetRelativePath(CorpusPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            var artifacts = Directory.CreateDirectory(Path.Combine(owner, location)).FullName;
            var output = Path.Combine(artifacts, "evidence.json");
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(corpus, output,
                _ => { constructed = true; throw new Exception("must not construct clients"); },
                CancellationToken.None, cancelFile: null, Path.Combine(owner, "worker-root")));
            Assert.False(constructed);
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(owner, true); }
    }

    [Fact]
    public async Task Evaluation_without_cancellation_flag_never_watches_the_cancel_path()
    {
        var directory = NewCancellationArtifactDirectory();
        var output = Path.Combine(directory, "evidence.json");
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var campaign = SystemOneEvaluation.RunToFileAsync(CorpusPath, output, _ => new DelegateEvaluationArm(async (input, mode, ct) =>
        {
            if (++calls == 1) { return Sample(input, mode); }
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Sample(input, mode);
        }), caller.Token, cancelFile: null, Path.Combine(directory, "worker-root"));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.WriteAllText(output + ".cancel", "would cancel a watched campaign");
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // A wrong watcher polls every 100ms.
            Assert.False(campaign.IsCompleted);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => campaign.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, calls);
        }
        finally
        {
            caller.Cancel();
            try { await campaign; } catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Evaluation_null_cancellation_flag_still_refuses_corpus_alias_output_before_clients()
    {
        var owner = NewCancellationArtifactDirectory();
        var constructed = false;
        try
        {
            var corpus = Path.Combine(owner, "scoring");
            foreach (var file in Directory.GetFiles(CorpusPath, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(corpus, Path.GetRelativePath(CorpusPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            // The corpus reached through a symlinked ancestor: RunToFileAsync's lexical root check
            // compares the alias spelling against the physical output path and cannot see it.
            var alias = Path.Combine(owner, "scoring-link");
            Directory.CreateSymbolicLink(alias, owner);
            var output = Path.Combine(corpus, "evidence.json");
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(Path.Combine(alias, "scoring"), output,
                _ => { constructed = true; throw new Exception("must not construct clients"); },
                CancellationToken.None, cancelFile: null, Path.Combine(owner, "worker-root")));
            Assert.False(constructed);
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(owner, true); }
    }

    [Fact]
    public async Task Evaluation_null_cancellation_flag_still_refuses_symlinked_artifact_ancestor_before_clients()
    {
        var owner = NewCancellationArtifactDirectory();
        var constructed = false;
        try
        {
            var artifacts = Directory.CreateDirectory(Path.Combine(owner, "artifacts")).FullName;
            var link = Path.Combine(owner, "linked-artifacts");
            Directory.CreateSymbolicLink(link, artifacts);
            var output = Path.Combine(link, "evidence.json");
            await Assert.ThrowsAsync<InvalidDataException>(() => SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => { constructed = true; throw new Exception("must not construct clients"); },
                CancellationToken.None, cancelFile: null, Path.Combine(owner, "worker-root")));
            Assert.False(constructed);
            Assert.False(File.Exists(Path.Combine(artifacts, "evidence.json")));
        }
        finally { Directory.Delete(owner, true); }
    }

    [Fact]
    public async Task Evaluation_null_cancellation_flag_still_completes_owner_session_artifact_campaign()
    {
        var owner = NewCancellationArtifactDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(owner, ".git"));
            var artifacts = Directory.CreateDirectory(Path.Combine(owner, ".claude", "session", "artifacts")).FullName;
            var output = Path.Combine(artifacts, "evidence.json");
            var workers = Path.Combine(owner, "worker-root");
            await SystemOneEvaluation.RunToFileAsync(CorpusPath, output,
                _ => new DelegateEvaluationArm((input, mode, _) => Task.FromResult(Sample(input, mode))),
                CancellationToken.None, cancelFile: null, workers);
            var evidence = JsonSerializer.Deserialize<EvaluationCampaign>(await File.ReadAllTextAsync(output))!;
            Assert.Equal(("complete", 30, 15), (evidence.Status, evidence.Observations.Count, evidence.Summary?.Pairs.Count));
            Assert.False(File.Exists(output + ".cancel")); // No flag, no watcher, no flag file created.
        }
        finally { Directory.Delete(owner, true); }
    }

    private sealed class RouterOnlySecrets : ISecretStore
    {
        public string? Get(string account) => account == SecretAccounts.RouterKey ? "synthetic-key" : throw new InvalidOperationException("Unrelated credential read: " + account);
        public void Set(string account, string value) => throw new NotSupportedException();
    }
    private sealed class FakeHeldLock(List<string> trace) : IDisposable
    {
        public void Dispose() => trace.Add("released");
    }
    private sealed class FakeIsolated(IDisposable held) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { held.Dispose(); return ValueTask.CompletedTask; }
    }
    private static FactoryOptions EvaluationOptions(string runAs = "_factory") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:RunAs"] = runAs }).Build(), new RouterOnlySecrets(), _ => null);

    [Fact]
    public async Task Evaluation_setup_cancellation_unwinds_fake_probe_and_releases_lock()
    {
        using var cancel = new CancellationTokenSource();
        var trace = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvaluationSetup.StartAsync(EvaluationOptions(),
            () => { trace.Add("locked"); return new FakeHeldLock(trace); },
            async _ => { trace.Add("fake-probe"); cancel.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, cancel.Token); },
            held => Task.FromResult(new FakeIsolated(held)), cancel.Token));
        Assert.Equal(new[] { "locked", "fake-probe", "released" }, trace);
    }

    [Fact]
    public async Task Evaluation_setup_needs_no_App_credentials_and_holds_locks_for_isolated_ledger_and_sandbox_probe()
    {
        var trace = new List<string>();
        await using var held = await EvaluationSetup.StartAsync(EvaluationOptions(),
            () => { trace.Add("locked"); return new FakeHeldLock(trace); },
            _ => { trace.Add("probed"); return Task.CompletedTask; },
            ownership => { trace.Add("isolated"); return Task.FromResult(new FakeIsolated(ownership)); });
        Assert.Equal(new[] { "locked", "isolated", "probed" }, trace);
    }

    [Fact]
    public async Task Evaluation_setup_refuses_unsandboxed_even_with_fake_probe_and_ledger() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvaluationSetup.StartAsync(EvaluationOptions("none"),
            () => new FakeHeldLock([]), _ => Task.CompletedTask, ownership => Task.FromResult(new FakeIsolated(ownership))));

    [Theory]
    [InlineData("lock")]
    [InlineData("probe")]
    [InlineData("ledger")]
    public async Task Evaluation_setup_cannot_continue_past_failed_safety_prerequisites_and_releases_its_locks(string failure)
    {
        var trace = new List<string>();
        try
        {
            await EvaluationSetup.StartAsync<FakeIsolated>(EvaluationOptions(),
                () => { trace.Add("locked"); return failure == "lock" ? throw new InvalidOperationException() : new FakeHeldLock(trace); },
                _ => { trace.Add("probed"); return failure == "probe" ? throw new InvalidOperationException() : Task.CompletedTask; },
                ownership => { trace.Add("isolated"); return failure == "ledger" ? throw new InvalidOperationException() : Task.FromResult(new FakeIsolated(ownership)); });
        }
        catch (InvalidOperationException) { }
        Assert.Equal(failure == "lock" ? new[] { "locked" } : failure == "probe" ? new[] { "locked", "isolated", "probed", "released" }
            : new[] { "locked", "isolated", "released" }, trace);
    }

}
