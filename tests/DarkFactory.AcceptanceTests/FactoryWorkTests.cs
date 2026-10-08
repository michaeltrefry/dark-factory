using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Epic sc-25171 acceptance tests that run the real <c>factory work</c> host (<see cref="FactoryHost.BuildWork"/>) in
/// process, against a throwaway ledger and work root (<see cref="E2e"/>). Live: FACTORY_E2E=1, real Shortcut, GitHub,
/// router and Claude. Runbook: docs/acceptance.md. The watch scope (FACTORY_E2E_WATCH_TEAM / FACTORY_E2E_WATCH_EPIC)
/// must hold no To Do story but the one under test: the host claims every ready story it sees.
/// </summary>
public class FactoryWorkTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(45);

    /// <summary>Longer than <c>Worker:PauseGraceSeconds</c> (default 660 s): a paused worker stops by then at the latest.</summary>
    private static readonly TimeSpan PauseTimeout = TimeSpan.FromMinutes(13);

    /// <summary>
    /// AT2: a To Do story in the watch scope is claimed by the intake loop, implemented through the router and parked at
    /// Review with an open PR; the story is In Progress with the PR and branch links; the ledger shows Intake → Implement →
    /// Review; the dashboard (real login) replays the session and the pipeline shows its cost. Also AT7 for the run.
    /// </summary>
    [Fact]
    public async Task Factory_work_takes_a_ready_story_to_review_with_links_ledger_replay_and_cost()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_STORY", "a To Do bug story in the watch scope whose fix lands in the sandbox repo");
        var scope = E2e.WatchScope();
        await using var e2e = await E2e.StartAsync("df_e2e_at2");
        var ct = TestContext.Current.CancellationToken;
        var startedAt = DateTimeOffset.UtcNow;
        Assert.Equal("To Do", (await E2e.StoryAsync(storyId, ct)).State);

        await using var app = FactoryHost.BuildWork(e2e.Options(scope));
        await app.StartAsync(ct);
        try
        {
            await WaitForStateAsync(e2e, storyId, WorkState.Review, RunTimeout, ct);
            var transitions = await e2e.TransitionsAsync(storyId, ct);
            Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], transitions.Select(t => t.State));
            var review = transitions[2];
            var sessionId = Assert.IsType<string>(review.ClaudeSessionId);
            var prUrl = Assert.IsType<string>(review.Detail);

            // Board: In Progress, carrying the PR and branch links.
            await using var db = e2e.Db();
            var repo = RepoRef.Parse((await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(storyId), ct)).Repo);
            var (state, links) = await E2e.StoryAsync(storyId, ct);
            Assert.Equal("In Progress", state);
            Assert.Contains(prUrl, links);
            Assert.Contains(RunPipeline.BranchUrl(repo, StoryId.BranchName(storyId)), links);
            var pr = Assert.Single(await E2e.PullRequestsAsync(repo, storyId, ct));
            Assert.Equal(prUrl, pr.GetProperty("html_url").GetString());

            // Dashboard, through the real login: the session page and its replay.
            var address = app.Address();
            var cookies = await E2e.LoginAsync(address, ct);
            using var http = E2e.DashboardClient(address, cookies);
            var sessionPage = await http.GetStringAsync($"/sessions/{Uri.EscapeDataString(sessionId)}", ct);
            Assert.Contains(StoryId.Format(storyId), sessionPage);
            await e2e.ReplayAsync(address, cookies, sessionId, ct);

            // The pipeline shows the item's cost (the run waited for the router's cost before it finished); $0 is a
            // recorded cost (the router's local model), so recorded means not null, not > 0.
            var data = app.Services.GetRequiredService<IDashboardData>();
            PipelineRow? row = null;
            await E2e.WaitForAsync("the pipeline row's cost", TimeSpan.FromMinutes(3), async () =>
                (row = (await data.ActiveItemsAsync(ct)).SingleOrDefault(r => r.ExternalId == StoryId.Format(storyId))) is { CostUsd: >= 0 }, ct);
            var pipeline = await http.GetStringAsync("/", ct);
            Assert.Contains($"data-item=\"{row!.Id}\"", pipeline);
            Assert.Contains(Format.Cost(row.CostUsd), pipeline);

            // AT7: the router accounts for every session of this run.
            await E2e.AssertRouterAccountsForSessionsAsync(e2e.Ledger, startedAt, ct);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// AT4: Pause, Continue and Stop on a live run, at item and factory scope, through the same code path as
    /// <c>factory pause|continue|stop</c>. Story 1 (in the watch scope, run by the host): item Pause → Continue, factory
    /// Pause → Continue (each resuming the same Claude session), then item Stop. Story 2 (outside the watch scope, run like
    /// <c>factory run --ignore-scope</c> once the host is stopped): factory Stop. Both stories must take the worker
    /// several minutes, or it may finish before a control lands.
    /// </summary>
    [Fact]
    public async Task Pause_continue_and_stop_at_item_and_factory_scope_on_a_live_run()
    {
        Harness.RequireOptIn();
        var first = E2e.Story("FACTORY_E2E_CONTROL_STORY", "a To Do story in the watch scope whose fix takes the worker several minutes");
        var second = E2e.Story("FACTORY_E2E_CONTROL_STORY_2", "a To Do story OUTSIDE the watch scope whose fix takes the worker several minutes");
        var scope = E2e.WatchScope();
        await using var e2e = await E2e.StartAsync("df_e2e_at4");
        var ct = TestContext.Current.CancellationToken;
        var options = e2e.Options(scope);
        var item = ControlScope.Item(StoryId.Format(first));

        await using (var app = FactoryHost.BuildWork(options))
        {
            await app.StartAsync(ct);
            try
            {
                await WaitForWorkerAsync(e2e, first, after: 0, ct);
                await E2e.WaitForAsync("the session checkpoint", RunTimeout, async () =>
                    (await e2e.HistoryAsync(first, ct)).Any(e => e.Step == RunPipeline.Steps.Session), ct);
                var session = await SessionOfAsync(e2e, first, ct);

                foreach (var pauseScope in new[] { item, ControlScope.Factory })
                {
                    await ControlAsync(options, "pause", pauseScope, ct);
                    var paused = await WaitForUserPausedAsync(e2e, first, ct);
                    await ControlAsync(options, "continue", pauseScope, ct);
                    // The intake loop resumes it, with the same Claude session, and the worker runs again.
                    await WaitForWorkerAsync(e2e, first, after: paused, ct);
                    Assert.Equal(session, await SessionOfAsync(e2e, first, ct));
                }

                await ControlAsync(options, "stop", item, ct);
                await WaitForStateAsync(e2e, first, WorkState.Cancelled, PauseTimeout, ct);
                Assert.Equal("Backlog", (await E2e.StoryAsync(first, ct)).State);
                Assert.Contains(await e2e.HistoryAsync(first, ct), e => e.Step == RunPipeline.Steps.StopReported);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }

        // Factory-scope Stop on a run nothing else drives (the host is gone).
        var run = FactoryRunner.RunAsync(options, second, ignoreScope: true, Console.Out, ct);
        await WaitForWorkerAsync(e2e, second, after: 0, ct);
        var stop = await FactoryRunner.ControlAsync(options, "stop", ControlScope.Factory, "acceptance", Console.Out, ct);
        Console.WriteLine(stop.Message);
        var outcome = await run.WaitAsync(PauseTimeout, ct);
        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Equal("Backlog", (await E2e.StoryAsync(second, ct)).State);
    }

    /// <summary>
    /// AT5: a stub router reports every plan exhausted until T (two minutes out) and proxies everything else to the real
    /// router. The host claims nothing before T, and claims and dispatches the ready story at T (the intake loop wakes at
    /// the pause's resume time, not at its next poll). The run then finishes at Review through the proxy (AT7 for it).
    /// </summary>
    [Fact]
    public async Task Nothing_dispatches_while_the_router_reports_plans_exhausted_and_work_starts_at_the_reset()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_USAGE_STORY", "a To Do bug story in the watch scope whose fix lands in the sandbox repo");
        var scope = E2e.WatchScope();
        await using var e2e = await E2e.StartAsync("df_e2e_at5");
        var ct = TestContext.Current.CancellationToken;
        var startedAt = DateTimeOffset.UtcNow;
        await using var stub = await StubRouter.StartAsync(Harness.Options.RouterBaseUrl, startedAt + TimeSpan.FromMinutes(2), ct);
        var resetAt = stub.ResumesAt;
        // A long poll, so a claim right at T can only come from the loop waking at the pause's resume time.
        var options = e2e.Options(new Dictionary<string, string?>(scope)
        {
            ["Router:BaseUrl"] = stub.BaseUrl.ToString(),
            ["Intake:PollSeconds"] = "600",
        });

        await using var app = FactoryHost.BuildWork(options);
        await app.StartAsync(ct);
        try
        {
            await E2e.WaitForAsync("the usage pause", TimeSpan.FromSeconds(60), async () =>
                await FactoryRunner.Controls(options).UsagePauseAsync(ct) is { } pause && pause.ResumeAt is { } at
                && Math.Abs((at - resetAt).TotalSeconds) < 1, ct);
            Assert.True(stub.UsageReads > 0);

            // Before T: nothing claimed or dispatched.
            while (DateTimeOffset.UtcNow < resetAt - TimeSpan.FromSeconds(10))
            {
                Assert.Empty(await e2e.HistoryAsync(storyId, ct));
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            Assert.Equal("To Do", (await E2e.StoryAsync(storyId, ct)).State);
            Assert.Empty(await e2e.HistoryAsync(storyId, ct));

            // At T: claimed and dispatched.
            await WaitForWorkerAsync(e2e, storyId, after: 0, ct);
            var intake = (await e2e.TransitionsAsync(storyId, ct))[0];
            Assert.Equal(WorkState.Intake, intake.State);
            Assert.True(intake.RecordedAt >= resetAt, $"claimed at {intake.RecordedAt:O}, before the reset {resetAt:O}");
            Assert.True(intake.RecordedAt < resetAt + TimeSpan.FromSeconds(60), $"claimed at {intake.RecordedAt:O}, long after the reset {resetAt:O}");

            await WaitForStateAsync(e2e, storyId, WorkState.Review, RunTimeout, ct);
            await E2e.AssertRouterAccountsForSessionsAsync(e2e.Ledger, startedAt, ct);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task ControlAsync(FactoryOptions options, string action, string scope, CancellationToken ct)
    {
        var result = await FactoryRunner.ControlAsync(options, action, scope, "acceptance", Console.Out, ct);
        Assert.True(result.Ok, $"{action} {scope}: {result.Message}");
    }

    /// <summary>Waits until the item's latest transition is <paramref name="state"/>; fails at once if it escalates instead.</summary>
    internal static Task WaitForStateAsync(E2e e2e, int storyId, WorkState state, TimeSpan timeout, CancellationToken ct) =>
        E2e.WaitForAsync($"{StoryId.Format(storyId)} to reach {state}", timeout, async () =>
        {
            var last = (await e2e.TransitionsAsync(storyId, ct)).LastOrDefault();
            Assert.False(last?.State == WorkState.Escalated && state != WorkState.Escalated, $"{StoryId.Format(storyId)} escalated: {last?.Detail}");
            return last?.State == state;
        }, ct);

    /// <summary>Waits for a <c>worker-started</c> checkpoint after ledger row <paramref name="after"/>, i.e. a running worker.</summary>
    private static Task WaitForWorkerAsync(E2e e2e, int storyId, long after, CancellationToken ct) =>
        E2e.WaitForAsync($"a worker for {StoryId.Format(storyId)}", RunTimeout, async () =>
        {
            var history = await e2e.HistoryAsync(storyId, ct);
            Assert.False(history.LastOrDefault(e => e.Step is null)?.State is WorkState.Review or WorkState.Escalated,
                $"{StoryId.Format(storyId)} finished before the control: pick a story that takes the worker longer");
            return history.Any(e => e.Id > after && e.Step == RunPipeline.Steps.WorkerStarted);
        }, ct);

    /// <summary>Waits for the Paused row a Pause control records; returns its id.</summary>
    private static async Task<long> WaitForUserPausedAsync(E2e e2e, int storyId, CancellationToken ct)
    {
        LedgerEntry? paused = null;
        await E2e.WaitForAsync($"{StoryId.Format(storyId)} to pause", PauseTimeout, async () =>
            (paused = (await e2e.TransitionsAsync(storyId, ct)).LastOrDefault()) is { State: WorkState.Paused, Detail: RunPipeline.UserPaused }, ct);
        return paused!.Id;
    }

    /// <summary>The item's single Claude session: every session checkpoint so far names the same one.</summary>
    private static async Task<string> SessionOfAsync(E2e e2e, int storyId, CancellationToken ct)
    {
        var sessions = (await e2e.HistoryAsync(storyId, ct)).Where(e => e.Step == RunPipeline.Steps.Session)
            .Select(e => e.ClaudeSessionId).Distinct().ToList();
        return Assert.IsType<string>(Assert.Single(sessions));
    }
}
