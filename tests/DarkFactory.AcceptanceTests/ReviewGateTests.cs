using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 2 acceptance harness, v1 (sc-25378, the review → gate → merge walking skeleton; docs/acceptance.md P2-AT1/P2-AT2).
/// Live: skipped unless FACTORY_E2E=1. Each runs <c>factory run</c> (the production wiring, against a throwaway ledger)
/// on a To Do story whose fix lands in the sandbox, and the gate really merges the PR. Needs the merge gate's App
/// (<c>factory github-app setup --gate</c>, installed on the sandbox), the sandbox rulesets re-applied with it
/// (<c>factory github-repo protect</c>) and a committed <c>factory/gate.yaml</c> on the sandbox's main.
/// </summary>
public class ReviewGateTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(60);

    /// <summary>P2-AT1: a sandbox PR with green CI and a pass from a Claude Opus 5.5+ panel is merged by the gate; the ledger holds the merge commit.</summary>
    [Fact]
    public async Task Gate_merges_a_green_pr_a_claude_opus_panel_passed_and_the_ledger_records_the_merge_commit()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_GATE_STORY", "a To Do bug story whose fix lands in the sandbox repo (the gate will merge it)");
        await using var e2e = await E2e.StartAsync("df_e2e_p2at1");
        var options = e2e.Options();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        await RequireGateReadyAsync(options, ct);

        using var shortcutHttp = OutboundHttp.ShortcutApi();
        var outcome = await FactoryRunner.RunAsync(options, FactoryRunner.CreateWorkSource(options, shortcutHttp), storyId, ignoreScope: true,
            Console.Out, ct);
        Assert.True(outcome.Succeeded, outcome.Error);

        var history = await e2e.HistoryAsync(storyId, ct);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch],
            history.Where(e => e.Step is null).Select(e => e.State));
        var mergeCommit = Assert.IsType<string>(history.Single(e => e.Step is null && e.State == WorkState.Merge).Detail);

        var verdict = Assert.Single(RunPipeline.Verdicts(history));
        Assert.True(verdict.Passed, verdict.Summary);
        AssertClaudePanel(verdict);
        // Every panel call went through the router, accounted under its own session, which the ledger named with its prompt hash.
        var named = history.Where(e => e.Step == RunPipeline.Steps.ReviewSession).Select(e => e.Detail!).ToList();
        foreach (var review in verdict.Reviews)
        {
            var session = Assert.IsType<string>(review.Session);
            Assert.Contains($"{session} {review.Model} {verdict.HeadSha} {review.Role} {review.Prompt}", named);
            Assert.Equal(ReviewPrompts.For(review.Role).Id, review.Prompt);
            var cost = await Harness.WaitForCostAsync(session, options.RouterKey, ct);
            Assert.True(cost is { RequestCount: > 0 }, $"the router recorded no request for the {review.Role} review session {session}");
        }

        var pr = await MergedPullRequestAsync(options.DefaultRepo, storyId, outcome.PullRequestUrl!, ct);
        Assert.Equal(mergeCommit, pr.GetProperty("merge_commit_sha").GetString());
        Assert.Equal(verdict.HeadSha, pr.GetProperty("head").GetProperty("sha").GetString());
        Assert.Equal("Done", (await E2e.StoryAsync(storyId, ct)).State);
        // sc-25391 (epic AT2): a typed outcome on every row, the merge through the queue, the PR body and closeout from the ledger.
        await AssertTypedOutcomesQueueAndReportsAsync(e2e, options, storyId, pr, ct);
    }

    /// <summary>P2-AT2: a push after the verdict blocks the merge until the new head is reviewed again (verdict bound to head SHA).</summary>
    [Fact]
    public async Task A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_GATE_PUSH_STORY", "a second To Do bug story whose fix lands in the sandbox repo (the gate will merge it)");
        await using var e2e = await E2e.StartAsync("df_e2e_p2at2");
        var options = e2e.Options();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        await RequireGateReadyAsync(options, ct);

        using var shortcutHttp = OutboundHttp.ShortcutApi();
        var pusher = new PushAfterFirstVerdict(options, options.DefaultRepo, StoryId.BranchName(storyId));
        var outcome = await FactoryRunner.RunAsync(options, FactoryRunner.CreateWorkSource(options, shortcutHttp), storyId, ignoreScope: true,
            Console.Out, ct, gate => gate with { Reviewer = pusher.Wrap(gate.Reviewer) });
        Assert.True(outcome.Succeeded, outcome.Error);

        var history = await e2e.HistoryAsync(storyId, ct);
        var pushed = Assert.IsType<string>(pusher.PushedSha);
        // Reviewed, then the push voided the verdict at CI: back to Review for the new head, and only then on to the gate.
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.Review, WorkState.CI, WorkState.MergeGate,
            WorkState.Merge, WorkState.Watch], history.Where(e => e.Step is null).Select(e => e.State));
        var verdicts = RunPipeline.Verdicts(history);
        Assert.Equal([pusher.ReviewedFirst, pushed], verdicts.Select(v => v.HeadSha));
        Assert.All(verdicts, AssertClaudePanel);
        Assert.Equal([pushed], history.Where(e => e.Step == RunPipeline.Steps.GatePassed).Select(e => e.Detail));

        var pr = await MergedPullRequestAsync(options.DefaultRepo, storyId, outcome.PullRequestUrl!, ct);
        Assert.Equal(pushed, pr.GetProperty("head").GetProperty("sha").GetString());
        Assert.Equal(history.Single(e => e.Step is null && e.State == WorkState.Merge).Detail, pr.GetProperty("merge_commit_sha").GetString());
        await AssertTypedOutcomesQueueAndReportsAsync(e2e, options, storyId, pr, ct);
    }

    /// <summary>
    /// Epic AT2 (sc-25391), on the item's ledger after its merge: every row carries the outcome <see cref="StepOutcomes.Of"/> gives
    /// it (from the item's state before the row, its state, step and detail); the merge went through the merge queue
    /// (<c>queued</c>, then <c>queue-turn</c>, then <c>gate-passed</c>, then Merge); the merged PR's description is exactly
    /// <see cref="LedgerReport.PullRequestBody"/> of the rows written before its rewrite (<c>pr-report</c> <c>merge</c>), and the
    /// story's closeout comment exactly <see cref="LedgerReport.MergedCloseout"/> of the rows written before it was posted.
    /// The session costs are read now: a cost the recorder wrote after the merge would show as a mismatch naming both texts.
    /// </summary>
    internal static async Task AssertTypedOutcomesQueueAndReportsAsync(E2e e2e, FactoryOptions options, int storyId, JsonElement mergedPr,
        CancellationToken ct)
    {
        var history = await e2e.HistoryAsync(storyId, ct);
        AssertTypedOutcomes(history);

        int Index(Func<LedgerEntry, bool> row, string what)
        {
            var index = history.FindIndex(e => row(e));
            Assert.True(index >= 0, $"the ledger has no {what} row");
            return index;
        }
        var queued = Index(e => e.Step == RunPipeline.Steps.Queued, "queued");
        var turn = Index(e => e.Step == RunPipeline.Steps.QueueTurn, "queue-turn");
        var passed = history.FindLastIndex(e => e.Step == RunPipeline.Steps.GatePassed);
        var merge = Index(e => e.Step is null && e.State == WorkState.Merge, "Merge");
        Assert.True(queued < turn && turn < passed && passed < merge, $"queued {queued}, queue-turn {turn}, gate-passed {passed}, Merge {merge}");

        var costs = await e2e.SessionCostsAsync(storyId, ct);
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        var story = (await FactoryRunner.CreateWorkSource(options, shortcutHttp).ReadSpecAsync(storyId, ct)).Story;
        var report = Index(e => e.Step == RunPipeline.Steps.PrReport, "pr-report");
        Assert.Equal("merge", history[report].Detail);
        Assert.Equal(Lines(LedgerReport.PullRequestBody(story, history.Take(report).ToList(), costs)), Lines(mergedPr.GetProperty("body").GetString()));

        var closeout = Index(e => e.Step == RunPipeline.Steps.Closeout && e.Detail == RunPipeline.Posted, "closeout posted");
        var expected = WorkSourceComments.Attributed(LedgerReport.MergedCloseout(StoryId.Format(storyId), history.Take(closeout).ToList(), costs));
        var comments = (await E2e.StoryCommentsAsync(storyId, ct)).Select(Lines).ToList();
        Assert.Contains(Lines(expected), comments);
    }

    /// <summary>Every row's outcome is the one <see cref="StepOutcomes.Of"/> decides for it (E7).</summary>
    internal static void AssertTypedOutcomes(IReadOnlyList<LedgerEntry> history)
    {
        WorkState? state = null;
        foreach (var row in history)
        {
            var expected = StepOutcomes.Of(state, row.State, row.Step, row.Detail);
            Assert.True(expected == row.Outcome,
                $"row {row.Id} ({row.Step ?? $"{state?.ToString() ?? "new"} → {row.State}"}: {row.Detail}) has outcome {StepOutcomes.Name(row.Outcome)}, not {StepOutcomes.Name(expected)}");
            if (row.Step is null)
            {
                state = row.State;
            }
        }
    }

    private static string Lines(string? text) => (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Every reviewer a Claude Opus 5.5 or newer and every second model Claude, each served by the router as pinned.</summary>
    private static void AssertClaudePanel(ReviewVerdict verdict)
    {
        Assert.NotEmpty(verdict.Reviews);
        Assert.All(verdict.Reviews, r => Assert.True(ReviewModels.MeetsReviewFloor(r.Model), $"{r.Role} reviewed by {r.Model}"));
        Assert.Empty(verdict.Reviews.SelectMany(ReviewModels.Problems));
    }

    /// <summary>Skips with the missing owner step unless the gate App is set up, Review:Models is set and the sandbox has a policy on main.</summary>
    internal static async Task RequireGateReadyAsync(FactoryOptions options, CancellationToken ct)
    {
        Harness.RequireSecret(o => o.GitHubGateAppId);
        Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        Harness.RequireReviewPanel(options);
        using var github = OutboundHttp.GitHubApi();
        var gate = new GitHubGate(github, new GitHubApp(github, options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem, TimeProvider.System));
        string? policy;
        try
        {
            policy = await gate.GetPolicyAsync(options.DefaultRepo, "main", ct);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip($"The gate App cannot read {options.DefaultRepo} (install it there): {ex.Message}");
            throw;
        }
        if (policy is null)
        {
            Assert.Skip($"{options.DefaultRepo} has no {GatePolicy.Path} on main: merge the PR that adds it.");
        }
    }

    internal static async Task<JsonElement> MergedPullRequestAsync(RepoRef repo, int storyId, string url, CancellationToken ct)
    {
        var pr = (await E2e.PullRequestsAsync(repo, storyId, ct)).Single(p => p.GetProperty("html_url").GetString() == url);
        Assert.NotEqual(JsonValueKind.Null, pr.GetProperty("merged_at").ValueKind);
        return pr;
    }

    /// <summary>
    /// After the first verdict is recorded-to-be, pushes one more commit to the PR's factory/* branch (as the workers' App,
    /// which may write only there), as a late push would: that verdict no longer describes the head.
    /// </summary>
    private sealed class PushAfterFirstVerdict(FactoryOptions options, RepoRef repo, string branch)
    {
        public string? ReviewedFirst { get; private set; }
        public string? PushedSha { get; private set; }

        public IReviewer Wrap(IReviewer inner) => new Wrapper(this, inner);

        private sealed class Wrapper(PushAfterFirstVerdict owner, IReviewer inner) : IReviewer
        {
            public async Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct)
            {
                var review = await inner.ReviewAsync(request, ct);
                if (owner.PushedSha is null)
                {
                    owner.ReviewedFirst = request.Pull.HeadSha;
                    owner.PushedSha = await owner.PushAsync(ct);
                }
                return review;
            }

            public Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => inner.ConfirmAsync(request, ct);
        }

        private async Task<string> PushAsync(CancellationToken ct)
        {
            using var github = OutboundHttp.GitHubApi();
            var token = (await new GitHubApp(github, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System)
                .CreateInstallationTokenAsync(repo, ct)).Token;
            const string path = "factory-e2e/push-after-verdict.txt";
            string? existing = null;
            using (var get = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/contents/{path}?ref={Uri.EscapeDataString(branch)}", "Bearer", token))
            using (var response = await github.SendAsync(get, ct))
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    existing = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("sha").GetString();
                }
            }
            using var put = GitHubApp.Request(HttpMethod.Put, $"repos/{repo.Owner}/{repo.Name}/contents/{path}", "Bearer", token);
            var body = new Dictionary<string, string>
            {
                ["message"] = "acceptance: a push after the review verdict",
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes($"pushed after the verdict at {DateTimeOffset.UtcNow:O}\n")),
                ["branch"] = branch,
            };
            if (existing is not null)
            {
                body["sha"] = existing; // updating the file a re-run left on the branch
            }
            put.Content = JsonContent.Create(body);
            using var pushed = await github.SendAsync(put, ct);
            pushed.EnsureSuccessStatusCode();
            return (await pushed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("commit").GetProperty("sha").GetString()!;
        }
    }
}
