using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;

using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public class IncompleteReviewRetryTests
{
    private sealed class TruncatedPanel(bool legacy = false) : IReviewer
    {
        public bool Truncate { get; set; } = true;
        public int TurnCount { get; set; } = 1;
        public int MaxOutputTokens { get; set; } = 24576;
        public List<ReviewRequest> Requests { get; } = [];
        public Task<RoleReview> ReviewAsync(ReviewRequest r, CancellationToken ct)
        {
            Requests.Add(r);
            var stop = Truncate && r.Role != ReviewRoles.Security ? "max_tokens" : "end_turn";
            var review = RouterReviewer.InterpretReview(r.Role, "router-selected", "high", stop, "{\"findings\":[]}")
                with { Session = r.Session, Prompt = r.Prompt.Id,
                    Turns = Enumerable.Range(1, TurnCount).Select(n => new ServedTurn(n, "router-selected", "high",
                        StopReason: legacy ? null : n == TurnCount ? stop : "tool_use",
                        MaxOutputTokens: legacy ? null : r.MaxOutputTokens ?? MaxOutputTokens)).ToList() };
            if (TurnCount > RouterReviewer.MaxTurns && review.Error is not null)
                review = review with { Error = "No usable final answer after the turn cap: " + review.Error };
            if (legacy) review = review with { Failure = null };
            return Task.FromResult(review);
        }
        public Task<Confirmation> ConfirmAsync(ConfirmRequest r, CancellationToken ct) => throw new InvalidOperationException("No finding to confirm");
    }

    private static async Task<(Harness H, TruncatedPanel Panel)> Failed(bool legacy = false, bool security = true, int turns = 1)
    {
        var panel = new TruncatedPanel(legacy) { TurnCount = turns };
        var h = new Harness { Panel = panel };
        if (security) h.GitHub.PolicyText = Support.TestPolicies.Standard(normalChecks: "ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base");
        Assert.Equal(WorkState.Escalated, (await h.Run()).State);
        return (h, panel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_retry_consumes_linked_complete_evidence_and_repeats_full_panel_without_implementation(bool legacy)
    {
        var (h, panel) = await Failed(legacy);
        var before = await h.Rows();
        var verdict = before.Last(r => r.Step == RunPipeline.Steps.Verdict);
        var escalation = before.Last(r => r.Step is null && r.State == WorkState.Escalated);
        var head = h.GitHub.Head;
        var workerCount = h.WorkerCalls.Count;
        panel.Truncate = false;
        var result = await h.Pipeline().RecoverAsync(77, head, "owner", default, "main", retryIncompleteReview: true);
        Assert.Null(result.Error);
        Assert.Equal(WorkState.Watch, result.State);
        var rows = await h.Rows();
        var audit = RunPipeline.OwnerRecovery.Of(rows)!;
        Assert.Equal("incomplete-review", audit.Kind);
        Assert.Equal(verdict.Id, audit.SourceVerdictId);
        Assert.Equal(escalation.Id, audit.SourceEscalationId);
        Assert.Equal(head, audit.Head);
        Assert.Equal(panel.MaxOutputTokens, audit.MaxOutputTokens);
        Assert.Equal(workerCount, h.WorkerCalls.Count);
        var boundary = rows.Single(r => r.State == WorkState.Recovering && r.Step is null);
        Assert.Contains(rows, r => r.Id > boundary.Id && r.Step == RunPipeline.Steps.Verdict);
        Assert.All(panel.Requests.Skip(before.Count(r => r.Step == RunPipeline.Steps.ReviewSession)), r => Assert.Equal(panel.MaxOutputTokens, r.MaxOutputTokens));
        var fresh = RunPipeline.Verdicts(rows).Single();
        Assert.True(fresh.Passed);
        Assert.All(fresh.Reviews, r => Assert.Null(r.CarriedFrom));
        Assert.Empty(fresh.Reviews.Select(r => r.Session).Intersect(panel.Requests.Take(2).Select(r => r.Session)));
    }

    [Fact]
    public async Task Ordinary_recovery_still_rejects_unchanged_head()
    {
        var (h, _) = await Failed();
        var before = await h.Rows();
        var result = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main");
        Assert.Contains("unchanged", result.Error);
        Assert.Equal(before.Count, (await h.Rows()).Count);
    }

    [Theory]
    [InlineData("blocking")]
    [InlineData("carried")]
    [InlineData("class")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("duplicate-json")]
    [InlineData("summary")]
    [InlineData("unrelated-escalation")]
    [InlineData("gate")]
    [InlineData("push")]
    [InlineData("verdict")]
    [InlineData("head")]
    [InlineData("clean")]
    [InlineData("wrong-stop")]
    [InlineData("wrong-budget")]
    [InlineData("confirmation")]
    [InlineData("risky-null")]
    [InlineData("failure-numeric")]
    [InlineData("session")]
    [InlineData("turn-class")]
    [InlineData("consumed")]
    [InlineData("stale-attempt")]
    public async Task Ineligible_or_ambiguous_evidence_never_admits(string problem)
    {
        var (h, _) = await Failed();
        var rows = await h.Rows();
        var vrow = rows.Last(r => r.Step == RunPipeline.Steps.Verdict);
        var erow = rows.Last(r => r.Step is null && r.State == WorkState.Escalated);
        var v = ReviewVerdict.FromDetail(vrow.Detail)!;
        var reviews = v.Reviews.ToList();
        switch (problem)
        {
            case "blocking": reviews[0] = reviews[0] with { Findings = [new Finding("blocking", "bug", null, null, "bad")] }; break;
            case "carried": reviews[0] = reviews[0] with { CarriedFrom = ShaA }; break;
            case "class": reviews[0] = reviews[0] with { ServedClass = "mid" }; break;
            case "missing": reviews.RemoveAt(0); break;
            case "duplicate": reviews.Add(reviews[0]); break;
            case "summary": v = v with { Summary = "unrelated failure" }; break;
            case "head": v = v with { HeadSha = ShaD }; break;
            case "clean": reviews = reviews.Select(r => r with { Error = null, Failure = null }).ToList(); break;
            case "session": reviews[0] = reviews[0] with { Session = "unrecorded-session" }; break;
            case "turn-class": reviews[0] = reviews[0] with { Turns = [new ServedTurn(1, "router-selected", "mid", StopReason: "max_tokens", MaxOutputTokens: 24576)] }; break;
            case "consumed":
                await h.Ledger.RecordAsync(await h.Db.WorkItems.SingleAsync(), WorkState.Recovering, null,
                    new RunPipeline.OwnerRecovery("owner", Sandbox.FullName, "factory/sc-77", "main", PrUrl, h.GitHub.Head, h.GitHub.Head,
                        Kind: "incomplete-review", SourceVerdictId: vrow.Id, SourceEscalationId: erow.Id, MaxOutputTokens: 24576).ToDetail(), default);
                await h.Ledger.RecordAsync(await h.Db.WorkItems.SingleAsync(), WorkState.Escalated, null, erow.Detail, default);
                break;
            case "stale-attempt":
                await h.Ledger.RecordAsync(await h.Db.WorkItems.SingleAsync(), WorkState.Intake, null, "rerun", default);
                await h.Ledger.RecordAsync(await h.Db.WorkItems.SingleAsync(), WorkState.Implement, null, "new attempt", default);
                await h.Ledger.RecordAsync(await h.Db.WorkItems.SingleAsync(), WorkState.Escalated, null, erow.Detail, default);
                break;
            case "wrong-stop": reviews[0] = reviews[0] with { Turns = [new ServedTurn(1, "router-selected", "high", StopReason: "end_turn", MaxOutputTokens: 24576)] }; break;
            case "wrong-budget": reviews[0] = reviews[0] with { Turns = [new ServedTurn(1, "router-selected", "high", StopReason: "max_tokens", MaxOutputTokens: 0)] }; break;
            case "confirmation": reviews[0] = reviews[0] with { Findings = [new Finding("optional", "bug", null, null, "bad", new Confirmation("unusable", "model", "high", "session", "prompt", "error"))] }; break;
            case "unrelated-escalation": erow.Detail = "operator says max_tokens"; break;
            case "gate":
            case "push":
            case "verdict":
                await h.Ledger.CheckpointAsync(await h.Db.WorkItems.SingleAsync(), problem, null, problem == "verdict" ? v.ToDetail() : ShaD, default); break;
        }
        v = v with { Reviews = reviews };
        // Keep the factory's summary/escalation linkage valid for each isolated invalid-panel mutation.
        if (problem is not ("summary" or "unrelated-escalation" or "malformed" or "duplicate-json"))
        {
            v = v with { Summary = ReviewPanel.Decide(v.HeadSha, v.RiskyPaths, reviews).Summary };
            erow.Detail = $"ReviewFailedException: The review panel failed {PrUrl} at {Ci.Short(v.HeadSha)}: {v.Summary}";
        }
        vrow.Detail = v.ToDetail();
        if (problem == "risky-null") vrow.Detail = vrow.Detail.Replace("\"risky\":[", "\"risky-null\":[").Insert(1, "\"risky\":null,");
        if (problem == "failure-numeric") vrow.Detail = vrow.Detail.Replace("\"failure\":\"OutputTruncated\"", "\"failure\":0");
        if (problem == "malformed") vrow.Detail = "{}";
        if (problem == "duplicate-json") vrow.Detail = vrow.Detail.Insert(1, "\"sha\":\"" + ShaD + "\",");
        await h.Db.SaveChangesAsync();
        var before = await h.Rows();
        var result = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        Assert.StartsWith("Recovery refused:", result.Error);
        Assert.Equal(before.Count, (await h.Rows()).Count);
    }

    [Theory]
    [InlineData("item:sc-77")]
    [InlineData("factory")]
    [InlineData("freeze")]
    public async Task Retry_never_overrides_controls(string scope)
    {
        var (h, _) = await Failed();
        if (scope == "freeze") await h.Controls.FreezeAsync("test", "held", null, default);
        else await h.Controls.SetAsync(scope, ControlState.Paused, "owner", default);
        var before = (await h.Rows()).Count;
        var result = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        Assert.StartsWith("Recovery refused:", result.Error);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Fact]
    public async Task Repeated_truncation_needs_new_explicit_invocation_and_new_evidence()
    {
        var (h, _) = await Failed();
        await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        var rows = await h.Rows();
        Assert.Single(rows, r => r.State == WorkState.Recovering && r.Step is null);
        var first = RunPipeline.OwnerRecovery.Of(rows)!;
        Assert.Equal(WorkState.Escalated, (await h.Db.WorkItems.SingleAsync()).State);
        await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        rows = await h.Rows();
        Assert.Equal(2, rows.Count(r => r.State == WorkState.Recovering && r.Step is null));
        Assert.NotEqual(first.SourceVerdictId, RunPipeline.OwnerRecovery.Of(rows)!.SourceVerdictId);
    }
    [Theory]
    [InlineData("ownership")]
    [InlineData("ownership-race")]
    [InlineData("taint")]
    [InlineData("scope")]
    [InlineData("worker")]
    [InlineData("lock")]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("base-sha")]
    [InlineData("control-race")]
    public async Task Retry_reuses_every_authority_boundary_and_refuses_races_without_admission(string problem)
    {
        var (h, panel) = await Failed();
        var item = await h.Db.WorkItems.SingleAsync();
        var reads = 0;
        IAsyncDisposable? held = null;
        var head = h.GitHub.Head;
        switch (problem)
        {
            case "ownership": h.Stories.RecoveryClaimRefusal = "another claimant"; break;
            case "ownership-race": h.GitHub.OnPullRead = _ => h.Stories.RecoveryClaimRefusal = "another claimant"; break;
            case "scope": h.Stories.InScope = false; break;
            case "taint":
                await h.Ledger.TaintSessionAsync(item, (await h.Rows()).Last(r => r.Step == RunPipeline.Steps.Pushed).ClaudeSessionId!, "web:WebFetch", default); break;
            case "worker": await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, "unfinished", "123", default); break;
            case "lock": held = await h.Locks.TryAcquireAsync(item.Id, default); break;
            default:
                h.GitHub.OnPullRead = _ =>
                {
                    if (++reads != 2) return;
                    if (problem == "head") h.GitHub.Head = ShaD;
                    if (problem == "base") h.GitHub.BaseRef = "other";
                    if (problem == "base-sha") h.GitHub.CurrentBaseSha = ShaD;
                    if (problem == "control-race") h.Controls.SetAsync("item:sc-77", ControlState.Stopping, "owner", default).GetAwaiter().GetResult();
                };
                break;
        }
        try
        {
            var before = (await h.Rows()).Count;
            var sessions = panel.Requests.Count;
            var result = await h.Pipeline().RecoverAsync(77, head, "owner", default, "main", retryIncompleteReview: true);
            Assert.StartsWith("Recovery refused:", result.Error);
            Assert.Equal(before, (await h.Rows()).Count);
            Assert.Equal(sessions, panel.Requests.Count);
            Assert.Empty(h.Merges);
        }
        finally { if (held is not null) await held.DisposeAsync(); }
    }

    [Fact]
    public async Task Interruption_after_atomic_admission_resumes_fresh_full_sessions_with_pinned_budget()
    {
        var (h, panel) = await Failed();
        var oldSessions = panel.Requests.Select(r => r.Session).ToList();
        var workers = h.WorkerCalls.Count;
        var reads = 0;
        h.GitHub.OnPullRead = _ =>
        {
            if (++reads == 3) throw new FactoryUnavailableException("simulated crash just after admission", new IOException("test"));
        };
        await Assert.ThrowsAsync<FactoryUnavailableException>(() => h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true));
        var rows = await h.Rows();
        Assert.Empty(RunPipeline.Verdicts(rows));
        Assert.Equal(WorkState.Recovering, (await h.Db.WorkItems.SingleAsync()).State);
        var audit = RunPipeline.OwnerRecovery.Of(rows)!;
        Assert.Equal(24576, audit.MaxOutputTokens);
        panel.Truncate = false;
        panel.MaxOutputTokens = 49152; // A resumed process configuration cannot silently change the admitted budget.
        h.GitHub.OnPullRead = null;
        var result = await h.Run();
        Assert.Null(result.Error);
        Assert.Equal(WorkState.Watch, result.State);
        rows = await h.Rows();
        var boundary = rows.Single(r => r.State == WorkState.Recovering && r.Step is null);
        var fresh = RunPipeline.Verdicts(rows).Single();
        Assert.Equal(ReviewRoles.All.Order(), fresh.Reviews.Select(r => r.Role).Order());
        Assert.Empty(oldSessions.Intersect(fresh.Reviews.Select(r => r.Session)));
        Assert.All(panel.Requests.Skip(oldSessions.Count), r => Assert.Equal(24576, r.MaxOutputTokens));
        Assert.Contains(rows, r => r.Id > boundary.Id && r.Step == RunPipeline.Steps.NewTests);
        Assert.Contains(rows, r => r.Id > boundary.Id && r.Step == RunPipeline.Steps.GatePassed);
        Assert.Equal(0, (await h.Ledger.ContextAsync(await h.Db.WorkItems.SingleAsync(), default)).FixRounds);
        Assert.Equal(workers, h.WorkerCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_admission_a_moved_head_or_pause_cannot_run_the_old_panel(bool pause)
    {
        var (h, panel) = await Failed();
        var sessions = panel.Requests.Count;
        var reads = 0;
        h.GitHub.OnPullRead = _ =>
        {
            if (++reads != 3) return;
            if (pause) h.Controls.SetAsync("item:sc-77", ControlState.Paused, "owner", default).GetAwaiter().GetResult();
            else h.GitHub.Head = ShaD;
        };
        var result = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        Assert.Equal(pause ? WorkState.Paused : WorkState.Escalated, result.State);
        Assert.Equal(sessions, panel.Requests.Count);
        Assert.Empty(h.Merges);
        Assert.Single((await h.Rows()), r => r.State == WorkState.Recovering && r.Step is null);
    }

    [Fact]
    public async Task Non_risky_two_role_failure_can_explicitly_retry_a_second_three_role_truncation()
    {
        var (h, _) = await Failed(security: false);
        Assert.Equal(2, RunPipeline.Verdicts(await h.Rows()).Single().Reviews.Count);
        var first = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        Assert.DoesNotContain("Recovery refused", first.Error ?? "");
        Assert.Equal(3, RunPipeline.Verdicts(await h.Rows()).Single().Reviews.Count);
        var second = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        Assert.DoesNotContain("Recovery refused", second.Error ?? "");
        Assert.Equal(2, (await h.Rows()).Count(r => r.State == WorkState.Recovering && r.Step is null));
    }

    [Theory]
    [InlineData(9, true, true)]
    [InlineData(9, false, false)]
    [InlineData(1, true, false)]
    public async Task Historical_final_turn_error_is_reconstructed_exactly_from_turn_count(int turns, bool prefix, bool admitted)
    {
        var (h, panel) = await Failed(legacy: true, turns: turns);
        var rows = await h.Rows();
        var vrow = rows.Last(r => r.Step == RunPipeline.Steps.Verdict);
        var v = ReviewVerdict.FromDetail(vrow.Detail)!;
        var reviews = v.Reviews.Select(r => r.Error is null ? r : r with
            { Error = (prefix ? "No usable final answer after the turn cap: " : "") + IncompleteReviewEvidence.LegacyTruncation }).ToList();
        v = ReviewPanel.Decide(v.HeadSha, v.RiskyPaths, reviews);
        vrow.Detail = v.ToDetail();
        rows.Last(r => r.Step is null && r.State == WorkState.Escalated).Detail = $"ReviewFailedException: The review panel failed {PrUrl} at {Ci.Short(v.HeadSha)}: {v.Summary}";
        await h.Db.SaveChangesAsync();
        panel.Truncate = false;
        var result = await h.Pipeline().RecoverAsync(77, h.GitHub.Head, "owner", default, "main", retryIncompleteReview: true);
        Assert.Equal(admitted, !(result.Error ?? "").StartsWith("Recovery refused:", StringComparison.Ordinal));
        Assert.Equal(admitted ? 1 : 0, (await h.Rows()).Count(r => r.State == WorkState.Recovering && r.Step is null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sanitized_actual_canary_panel_and_checkpoints_require_exact_linkage(bool corruptSession)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "reviews", "sc-26252-incomplete-panel.json")));
        var root = fixture.RootElement;
        var detail = root.GetProperty("verdict").GetRawText();
        var v = ReviewVerdict.FromDetail(detail)!;
        Assert.Equal(8192, root.GetProperty("historicalMaxOutputTokens").GetInt32());
        Assert.Equal(new[] { 6, 5, 1 }, v.Reviews.Select(r => r.Turns!.Count));
        Assert.Equal(new[] { 21, 9, 0 }, v.Reviews.Select(r => r.Tools!.Count));
        Assert.All(v.Reviews, r => Assert.Null(r.Failure));
        Assert.All(v.Reviews.SelectMany(r => r.Turns!), t => { Assert.Null(t.StopReason); Assert.Null(t.MaxOutputTokens); });
        var sessions = root.GetProperty("sessions").EnumerateArray().Select(s => s.GetString()!).ToList();
        if (corruptSession) sessions[0] += " incorrect";
        // Only admission's structured evidence context is replayed; no worker-session bodies or operational calls.
        var rows = new List<LedgerEntry>
        {
            new() { Id = 1000, State = WorkState.Implement, Outcome = StepOutcome.Passed },
            new() { Id = 1071, State = WorkState.Review, Detail = "https://github.com/SceneWorks/SceneWorks-mobile/pull/7", Outcome = StepOutcome.Passed },
        };
        rows.AddRange(sessions.Select((session, i) => new LedgerEntry { Id = 1076 + i, State = WorkState.Review,
            Step = RunPipeline.Steps.ReviewSession, Detail = session, Outcome = StepOutcome.Passed }));
        rows.Add(new() { Id = root.GetProperty("verdictId").GetInt64(), State = WorkState.Review,
            Step = RunPipeline.Steps.Verdict, Detail = detail, Outcome = StepOutcome.Failed });
        rows.Add(new() { Id = root.GetProperty("escalationId").GetInt64(), State = WorkState.Escalated,
            Detail = root.GetProperty("escalation").GetString(), Outcome = StepOutcome.Escalated });
        if (corruptSession)
            Assert.Throws<InvalidOperationException>(() => IncompleteReviewEvidence.Require(rows, v.HeadSha, "https://github.com/SceneWorks/SceneWorks-mobile/pull/7", ReviewRoles.All));
        else
        {
            var proof = IncompleteReviewEvidence.Require(rows, v.HeadSha, "https://github.com/SceneWorks/SceneWorks-mobile/pull/7", ReviewRoles.All);
            Assert.Equal(1079, proof.VerdictId);
            Assert.Equal(1080, proof.EscalationId);
        }
    }

}
