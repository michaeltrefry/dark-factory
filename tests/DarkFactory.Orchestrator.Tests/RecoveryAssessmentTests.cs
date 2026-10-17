using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using Xunit;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryAssessmentTests
{
    [Theory]
    [InlineData("historical", false)]
    [InlineData("forged", true)]
    [InlineData("main", true)]
    public async Task Historical_base_is_typed_authority_but_content_and_live_refs_are_not(string kind, bool refused)
    {
        var historical = new string('c', 40);
        var forged = new string('d', 40);
        var captured = Snapshot with { Evidence = [.. Snapshot.Evidence,
            new RecoveryEvidence("merge:1", "merged", "API PR:1", Tip, "hash", $"BaseSha={forged}") { BaseCommit = historical }] };
        var files = new Files();
        var turns = 0;
        var commit = kind == "historical" ? historical : kind == "forged" ? forged : "main";
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(++turns == 1
            ? SseAnswers.ToolTurn(("read", "read_file", JsonSerializer.Serialize(new { commit, path = "src/a.cs" })))
            : SseAnswers.Answer(Answer("inconclusive"))));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(captured)), files: files).AssessAsync(Trigger, "historical", CancellationToken.None);
        Assert.Equal(refused, Assert.Single(result.Tools).Error);
        Assert.Equal(refused ? 0 : 1, files.Reads.Count);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("http-timeout")]
    [InlineData("deadline")]
    public async Task Failed_later_turn_persists_partial_provenance_cost_and_semantic_attempt(string failure)
    {
        var fixture = new HotFileRecoveryTests.Fixture();
        for (var n = 0; n < 8; n++) await fixture.Merge();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var turns = 0;
        var api = new FakeApi().On("POST /v1/messages", _ =>
        {
            if (++turns == 1) return SseAnswers.Response(SseAnswers.ToolTurn(("read", "read_file", $$"""{"commit":"{{Tip}}","path":"src/a.cs"}""")));
            if (failure == "deadline") time.Advance(TimeSpan.FromMinutes(21));
            if (failure == "transport") throw new HttpRequestException("fixture transport failure");
            throw new TaskCanceledException("fixture timeout");
        });
        using var http = api.Client("http://router.test/");
        http.Timeout = TimeSpan.FromMinutes(20);
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test", time: time), (trigger, _) =>
            Task.FromResult(JsonSerializer.Serialize(Snapshot with { IncidentSnapshot = trigger.Id, Generation = trigger.Generation })),
            new Costs(), new Files(), costSettleDelay: TimeSpan.Zero);
        await using var recovery = new HotFileRecovery(fixture.Contexts, fixture.Locks, fixture.Options, fixture.Time, assessor);
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        await using var db = fixture.Contexts.CreateDbContext();
        var incident = Assert.Single(db.HotFileIncidents);
        Assert.NotNull(incident.Answer);
        var stored = JsonSerializer.Deserialize<HotFileAssessment>(incident.Answer)!;
        Assert.Equal("inconclusive", stored.Decision);
        Assert.NotNull(stored.Problem);
        Assert.Single(stored.Tools);
        Assert.Single(stored.Turns);
        Assert.Equal(0.125m, stored.CostUsd);
        Assert.Equal(1, incident.Attempts);
        Assert.Single(db.LedgerEntries.Where(e => e.Step == HotFileAssessment.Step));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_twenty_minute_session_survives_eleven_minutes_but_shutdown_propagates(bool shutdown)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        var turns = 0;
        var api = new FakeApi().On("POST /v1/messages", _ =>
        {
            if (++turns == 1) return SseAnswers.Response(SseAnswers.ToolTurn(("evidence", "read_evidence", "{\"id\":\"diff:1\"}")));
            time.Advance(TimeSpan.FromMinutes(11));
            if (shutdown) { stop.Cancel(); throw new OperationCanceledException(stop.Token); }
            return SseAnswers.Response(SseAnswers.Answer(Answer()));
        });
        using var http = api.Client("http://router.test/");
        http.Timeout = TimeSpan.FromMinutes(20);
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test", time: time),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)));
        if (shutdown) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assessor.AssessAsync(Trigger, "bound", stop.Token));
        else Assert.Equal("safe", (await assessor.AssessAsync(Trigger, "bound", stop.Token)).Decision);
    }

    private sealed class SequencedCosts(params DarkFactory.Orchestrator.Router.SessionCost?[] values) : DarkFactory.Orchestrator.Router.ISessionCostSource
    {
        public int Reads;
        public Task<DarkFactory.Orchestrator.Router.SessionCost?> GetSessionCostAsync(string id, CancellationToken ct) =>
            Task.FromResult(values[Math.Min(Reads++, values.Length - 1)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lagging_cost_retries_then_settles_and_unknown_is_bounded(bool unknown)
    {
        var costs = new SequencedCosts(null, new("cost", 0, 0), unknown ? null : new("cost", 1, 0), unknown ? null : new("cost", 2, 250000));
        var delays = new List<TimeSpan>();
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(Answer())));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)), costs,
            costRetryDelays: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], costSettleDelay: TimeSpan.FromSeconds(7),
            costDelay: (d, _) => { delays.Add(d); return Task.CompletedTask; }).AssessAsync(Trigger, "cost", CancellationToken.None);
        Assert.Equal(unknown ? null : 0.25m, result.CostUsd);
        Assert.Equal(unknown ? 3 : 4, costs.Reads);
        Assert.Equal(unknown ? new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }
            : new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7) }, delays);
    }

    [Fact]
    public async Task Measured_zero_is_known_spend_and_does_not_keep_retrying()
    {
        var costs = new SequencedCosts(new DarkFactory.Orchestrator.Router.SessionCost("zero", 1, 0));
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(Answer())));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)), costs, costSettleDelay: TimeSpan.Zero, costRetryDelays: [])
            .AssessAsync(Trigger, "zero", CancellationToken.None);
        Assert.Equal(0m, result.CostUsd);
        Assert.Equal(2, costs.Reads);
    }

    private sealed class HangingCosts(Microsoft.Extensions.Time.Testing.FakeTimeProvider time, Action? shutdown) : DarkFactory.Orchestrator.Router.ISessionCostSource
    {
        public bool Cancelled;
        public Task<DarkFactory.Orchestrator.Router.SessionCost?> GetSessionCostAsync(string id, CancellationToken ct)
        {
            shutdown?.Invoke();
            time.Advance(TimeSpan.FromMinutes(3));
            Cancelled = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Accounting was not bounded.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accounting_has_its_own_bound_and_never_swallows_shutdown(bool shutdown)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(Answer())));
        using var http = api.Client("http://router.test/");
        var costs = new HangingCosts(time, shutdown ? stop.Cancel : null);
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)), costs, time: time, costRetryDelays: []);
        if (shutdown) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assessor.AssessAsync(Trigger, "bound-cost", stop.Token));
        else
        {
            Assert.Null((await assessor.AssessAsync(Trigger, "bound-cost", stop.Token)).CostUsd);
            Assert.True(costs.Cancelled);
        }
    }

    [Fact]
    public void Production_recovery_reader_protects_shared_locks_and_uses_configured_settle_delay()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DarkFactory.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var production = File.ReadAllText(Path.Combine(root.FullName, "src/DarkFactory.Orchestrator/FactoryRunner.cs"));
        var recovery = production[production.IndexOf("private static Recovery.HotFileRecovery CreateRecovery", StringComparison.Ordinal)..];
        recovery = recovery[..recovery.IndexOf("private static LedgerDbContextFactory Contexts", StringComparison.Ordinal)];
        Assert.Contains("protectCloneLocks: true", recovery);
        Assert.Contains("costSettleDelay: options.CostSettleDelay", recovery);
    }

    internal const string Repo = "owner/alpha";
    internal static readonly string Tip = new('a', 40);
    internal static readonly HotFileSnapshot Trigger = new("incident", Repo, 1, [], "trigger-hash");
    internal static RecoverySnapshot Snapshot => new("material", Trigger.Id, Repo, 1,
        new Dictionary<string, string> { ["main"] = Tip },
        [new("diff:1", "merged-diff", "PR:1", Tip, "hash", "diff"),
         new("pending:sc-2", "pending", "ledger:2", new string('b', 40), "hash", "{\"ExternalId\":\"sc-2\"}")], []);
    internal static string Answer(string decision = "safe", string kind = "merged", string citation = "diff:1", bool finding = false) =>
        JsonSerializer.Serialize(new { version = 1, snapshot = Trigger.Id, generation = 1, repo = Repo,
            decision, evidenceHash = Trigger.EvidenceHash, materialSnapshot = Snapshot.Id,
            citations = new[] { citation }, findings = finding ? new[] { new { kind, blocking = true,
                issue = "Wrong result", expectedBehavior = "Preserve requirements", tests = new[] { "Assert the specified result" },
                paths = new[] { "src/a.cs", "tests/a.cs" }, citations = new[] { citation },
                pendingItems = kind == "pending" ? new[] { "sc-2" } : Array.Empty<string>() } } : [] });

    private sealed class Files : IReviewFiles
    {
        public readonly List<(string Repo, string Sha, string Path)> Reads = [];
        public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct)
        { Reads.Add((repo.ToString(), sha, path)); return Task.FromResult<string?>("pinned code"); }
        public Task<Bounded<string>> ListAsync(RepoRef r, string s, string? d, CancellationToken ct) => throw new NotSupportedException();
        public Task<Bounded<GrepMatch>> GrepAsync(RepoRef r, string s, string p, string? d, int n, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("unsafe")]
    [InlineData("inconclusive")]
    public async Task Assessment_reads_the_snapshot_commit_and_persists_its_typed_cited_record(string decision)
    {
        var files = new Files();
        var turns = 0;
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(++turns == 1
            ? SseAnswers.ToolTurn(("read", "read_file", $$"""{"commit":"{{Tip}}","path":"src/a.cs"}"""))
            : SseAnswers.Answer(Answer(decision, finding: decision == "unsafe"))));
        using var http = api.Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)), files: files);
        var result = await assessor.AssessAsync(Trigger, "fresh-assessment", CancellationToken.None);
        // Same serialized record used by HotFileRecovery's incident.Answer, not a separate test-only result.
        var persisted = JsonSerializer.Deserialize<HotFileAssessment>(JsonSerializer.Serialize(result))!;
        Assert.Equal(decision, persisted.Decision);
        Assert.Equal("material", persisted.MaterialSnapshot);
        Assert.Equal(new[] { "diff:1" }, persisted.Citations);
        Assert.Equal("fresh-assessment", persisted.Session);
        Assert.Contains("hot-file-assessment.md@sha256:", persisted.Prompt);
        Assert.Equal((Repo, Tip, "src/a.cs"), Assert.Single(files.Reads));
        var call = Assert.Single(persisted.Tools);
        Assert.Equal(Tip, call.Commit);
        Assert.False(call.Error);
        Assert.Equal(2, persisted.Turns.Count);
        Assert.All(api.Requests, r => Assert.Equal("high", r.Headers["x-weave-model-class"]));
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("unsafe")]
    [InlineData("inconclusive")]
    public async Task Recovery_incident_persists_the_actual_assessment_session_tools_cost_and_citations(string decision)
    {
        var fixture = new HotFileRecoveryTests.Fixture();
        for (var n = 0; n < 8; n++) await fixture.Merge();
        var files = new Files();
        var turns = 0;
        var response = "";
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(++turns == 1
            ? SseAnswers.ToolTurn(("read", "read_file", $$"""{"commit":"{{Tip}}","path":"src/a.cs"}"""))
            : SseAnswers.Answer(response)));
        using var http = api.Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test"), (trigger, _) =>
        {
            var captured = Snapshot with { IncidentSnapshot = trigger.Id, Generation = trigger.Generation };
            var node = System.Text.Json.Nodes.JsonNode.Parse(Answer(decision, finding: decision == "unsafe"))!;
            node["snapshot"] = trigger.Id;
            node["generation"] = trigger.Generation;
            node["evidenceHash"] = trigger.EvidenceHash;
            response = node.ToJsonString();
            return Task.FromResult(JsonSerializer.Serialize(captured));
        }, new Costs(), files, costSettleDelay: TimeSpan.Zero);
        await using var recovery = new HotFileRecovery(fixture.Contexts, fixture.Locks, fixture.Options, fixture.Time, assessor);
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        await using var db = fixture.Contexts.CreateDbContext();
        var incident = Assert.Single(db.HotFileIncidents);
        var stored = JsonSerializer.Deserialize<HotFileAssessment>(incident.Answer!)!;
        Assert.Equal(decision, stored.Decision);
        Assert.Equal(incident.Session, stored.Session);
        Assert.Equal(incident.Prompt, stored.Prompt);
        Assert.Equal(0.125m, stored.CostUsd);
        Assert.Equal(new[] { "diff:1" }, stored.Citations);
        Assert.Equal(Tip, Assert.Single(stored.Tools).Commit);
        Assert.Equal("read_file", stored.Tools[0].Tool);
        Assert.Equal(2, stored.Turns.Count);
        Assert.Equal(decision == "safe" ? "Resolved" : "Held", incident.State);
        var history = Assert.Single(db.LedgerEntries.Where(e => e.Step == HotFileAssessment.Step));
        Assert.Equal(incident.Answer, history.Detail);
        Assert.Equal(decision == "safe" ? DarkFactory.Orchestrator.Ledger.StepOutcome.Passed
            : decision == "unsafe" ? DarkFactory.Orchestrator.Ledger.StepOutcome.GateRejected
            : DarkFactory.Orchestrator.Ledger.StepOutcome.Failed, history.Outcome);
    }

    private sealed class Costs(Action? onRead = null) : DarkFactory.Orchestrator.Router.ISessionCostSource
    {
        public Task<DarkFactory.Orchestrator.Router.SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct)
        {
            onRead?.Invoke();
            return Task.FromResult<DarkFactory.Orchestrator.Router.SessionCost?>(new(sessionId, 1, 125000));
        }
    }

    private static string OversizedDecision()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Answer(finding: true))!;
        var finding = root["findings"]![0]!;
        finding["blocking"] = false;
        finding["issue"] = new string('i', 1999);
        finding["expectedBehavior"] = new string('e', 1999);
        finding["tests"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 5).Select(n => $"{n}" + new string('t', 298)));
        root["findings"] = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(1, 8).Select(_ => finding.DeepClone()).ToArray());
        var text = root.ToJsonString();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) > 32768);
        return text;
    }

    [Fact]
    public async Task Final_freshness_read_follows_cost_telemetry_so_it_cannot_accept_a_tip_changed_during_accounting()
    {
        var changed = false;
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(Answer())));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot) + (changed ? " " : "")),
            new Costs(() => changed = true), costSettleDelay: TimeSpan.Zero).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Empty(result.CorrectionFindings);
        Assert.Equal(0.125m, result.CostUsd);
    }

    [Theory]
    [InlineData("mid")]
    [InlineData(null)]
    public async Task Wrong_final_class_cannot_accept_a_safe_decision(string? modelClass)
    {
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(Answer()), modelClass: modelClass));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot))).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Empty(result.CorrectionFindings);
    }

    [Theory]
    [InlineData("mid")]
    [InlineData(null)]
    public async Task Wrong_served_class_is_inconclusive_before_any_tool_runs(string? modelClass)
    {
        var files = new Files();
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(
            SseAnswers.ToolTurn(("read", "read_file", $$"""{"commit":"{{Tip}}","path":"src/a.cs"}""")), modelClass: modelClass));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)), files: files).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Empty(files.Reads);
        Assert.Empty(result.CorrectionFindings);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("safe-blocking")]
    [InlineData("wrong-material")]
    [InlineData("wrong-generation")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("pending-as-merged")]
    [InlineData("no-disposition")]
    public async Task Invalid_decisions_are_inconclusive_without_actionable_dispositions(string scenario)
    {
        var text = scenario switch
        {
            "forged" => Answer(citation: "not-in-snapshot"),
            "safe-blocking" => Answer(finding: true),
            "wrong-material" => Answer().Replace("\"material\"", "\"other\""),
            "wrong-generation" => Answer().Replace("\"generation\":1", "\"generation\":2"),
            "unknown" => Answer()[..^1] + ",\"command\":\"continue\"}",
            "duplicate" => Answer()[..^1] + ",\"version\":1}",
            "oversized" => OversizedDecision(),
            "pending-as-merged" => Answer("unsafe", citation: "pending:sc-2", finding: true),
            _ => Answer("unsafe")
        };
        var result = await Run(text);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Empty(result.CorrectionFindings);
        Assert.Empty(result.PendingDispositions);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task Pending_only_defect_has_a_pending_disposition_and_no_merged_correction_request()
    {
        var result = await Run(Answer("unsafe", "pending", "pending:sc-2", true));
        Assert.Equal("unsafe", result.Decision);
        Assert.Empty(result.CorrectionFindings);
        var disposition = Assert.Single(result.PendingDispositions);
        Assert.Equal("sc-2", disposition.Item);
        Assert.Equal("hold-and-reassess", disposition.Action);
        Assert.Equal("Wrong result", disposition.Issue);
    }

    [Theory]
    [InlineData("other-commit", "src/a.cs")]
    [InlineData("main", "src/a.cs")]
    [InlineData("tip", "../secret")]
    public async Task Repository_tools_refuse_unpinned_commits_and_escaping_paths(string commit, string path)
    {
        var files = new Files();
        var turns = 0;
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(++turns == 1
            ? SseAnswers.ToolTurn(("read", "read_file", JsonSerializer.Serialize(new { commit = commit == "tip" ? Tip : commit, path })))
            : SseAnswers.Answer(Answer("inconclusive"))));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot)), files: files).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Empty(files.Reads);
        Assert.True(Assert.Single(result.Tools).Error);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("nested-unknown")]
    [InlineData("nested-duplicate")]
    [InlineData("missing-tests")]
    [InlineData("too-many-tests")]
    [InlineData("too-many-paths")]
    [InlineData("escaping-path")]
    [InlineData("wrong-pending-item")]
    [InlineData("long-issue")]
    public async Task Finding_schema_and_disposition_bounds_refuse_unusable_requests(string scenario)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Answer("unsafe", "pending", "pending:sc-2", true))!;
        var finding = node["findings"]![0]!;
        switch (scenario)
        {
            case "kind": finding["kind"] = "execute"; break;
            case "nested-unknown": finding["command"] = "continue"; break;
            case "missing-tests": finding["tests"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "too-many-tests": finding["tests"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 6).Select(n => $"test {n}")); break;
            case "too-many-paths": finding["paths"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 21).Select(n => $"src/{n}.cs")); break;
            case "escaping-path": finding["paths"] = new System.Text.Json.Nodes.JsonArray("../owner"); break;
            case "wrong-pending-item": finding["pendingItems"] = new System.Text.Json.Nodes.JsonArray("sc-999"); break;
            case "long-issue": finding["issue"] = new string('x', 2001); break;
        }
        var text = node.ToJsonString();
        if (scenario == "nested-duplicate") text = text.Replace("\"blocking\":true", "\"blocking\":true,\"blocking\":false");
        var result = await Run(text);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Empty(result.PendingDispositions);
        Assert.Empty(result.CorrectionFindings);
    }

    [Fact]
    public async Task Changed_material_revokes_unsafe_dispositions_as_well_as_safe_resolution()
    {
        var captures = 0;
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(Answer("unsafe", finding: true))));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot) + (++captures == 1 ? "" : " ")))
            .AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Empty(result.CorrectionFindings);
        Assert.Empty(result.PendingDispositions);
    }

    [Theory]
    [InlineData("read_evidence", "{\"id\":\"diff:1\"}", false)]
    [InlineData("read_evidence", "{\"id\":\"forged\"}", true)]
    [InlineData("Bash", "{}", true)]
    [InlineData("analyze_impact_file", "{}", true)]
    [InlineData("update_work_item", "{}", true)]
    public async Task Tool_surface_is_only_pinned_files_and_bounded_snapshot_lookup(string name, string input, bool error)
    {
        var calls = 0;
        var api = new FakeApi().On("POST /v1/messages", r =>
        {
            using var doc = JsonDocument.Parse(r.Body!);
            Assert.Equal(new[] { "read_evidence" }, doc.RootElement.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()));
            return SseAnswers.Response(++calls == 1 ? SseAnswers.ToolTurn(("lookup", name, input)) : SseAnswers.Answer(Answer("inconclusive")));
        });
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot))).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal(error, Assert.Single(result.Tools).Error);
        Assert.NotEmpty(result.Tools[0].ResultSha256);
    }

    [Fact]
    public async Task Broken_final_stream_is_inconclusive_and_keeps_completed_tool_provenance()
    {
        var calls = 0;
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(++calls == 1
            ? SseAnswers.ToolTurn(("lookup", "read_evidence", "{\"id\":\"diff:1\"}"))
            : SseAnswers.Answer(Answer()).Replace(SseAnswers.MessageStop, "")));
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot))).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal("inconclusive", result.Decision);
        Assert.Single(result.Tools);
        Assert.Empty(result.CorrectionFindings);
        Assert.Null(result.CostUsd);
        Assert.Contains("message_stop", result.Problem);
    }

    [Fact]
    public async Task Assessment_uses_the_shared_turn_cap_and_forces_a_final_JSON_answer_without_more_tools()
    {
        var calls = 0;
        var api = new FakeApi().On("POST /v1/messages", r =>
        {
            calls++;
            using var doc = JsonDocument.Parse(r.Body!);
            var final = calls == RouterReviewer.MaxTurns + 1;
            Assert.Equal(final ? "none" : "auto", doc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
            return SseAnswers.Response(final ? SseAnswers.Answer(Answer("inconclusive"))
                : SseAnswers.ToolTurn(($"lookup{calls}", "read_evidence", "{\"id\":\"diff:1\"}")));
        });
        using var http = api.Client("http://router.test/");
        var result = await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot))).AssessAsync(Trigger, "fresh", CancellationToken.None);
        Assert.Equal(RouterReviewer.MaxTurns + 1, calls);
        Assert.Equal(RouterReviewer.MaxTurns, result.Tools.Count);
        Assert.True(result.Tools[^1].Error);
        Assert.Equal("inconclusive", result.Decision);
    }

    internal static async Task<HotFileAssessment> Run(string text)
    {
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(text)));
        using var http = api.Client("http://router.test/");
        return await new RouterHotFileAssessor(new RouterReviewer(http, "test"),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(Snapshot))).AssessAsync(Trigger, "fresh", CancellationToken.None);
    }
}
