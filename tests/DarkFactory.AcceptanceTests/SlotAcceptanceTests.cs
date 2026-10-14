using System.Diagnostics;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4b's epic P4b-AT5 and P4b-AT6, live (sc-25989; run by the epic's terminal story): worker slots on a machine set up with
/// two sandbox users (<c>sudo scripts/setup-worker-user.sh --slots 2</c>, <c>Worker__Slots=2</c>). The real CLI as child
/// processes against a throwaway ledger and work root (<see cref="E2e"/>); a test kills only a child it started, by its own pid
/// (<see cref="OwnProcess"/>), never a tree and never a sandbox user's process. Both skip unless <c>FACTORY_E2E=1</c> and two
/// slots are configured. Runbook: docs/acceptance.md, "Phase 4b".
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SlotAcceptanceTests
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(90);

    /// <summary>Two To Do stories named by <paramref name="variable"/> (<c>sc-1,sc-2</c>), on a machine with at least two worker slots.</summary>
    private static (int First, int Second) TwoStories(string variable, string purpose)
    {
        var value = Harness.RequireEnv(variable, purpose);
        var ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(ids.Length == 2 && ids[0] != ids[1] && ids.All(id => StoryId.TryParse(id, out _)), $"{variable}='{value}' is not two story ids (sc-1,sc-2)");
        StoryId.TryParse(ids[0], out var first);
        StoryId.TryParse(ids[1], out var second);
        if (Harness.Options.WorkerSlotCount < 2)
        {
            Assert.Skip("Worker slots: set Worker__Slots=2 on a machine set up with `sudo scripts/setup-worker-user.sh --slots 2`.");
        }
        return (first, second);
    }

    /// <summary>The item's recorded worker slot (<see cref="RunPipeline.Steps.Slot"/> rows' details, oldest first).</summary>
    private static async Task<List<string?>> SlotsAsync(E2e e2e, int storyId, CancellationToken ct) =>
        [.. (await e2e.HistoryAsync(storyId, ct)).Where(e => e.Step == RunPipeline.Steps.Slot).Select(e => e.Detail)];

    private static async Task<List<WorkerSession>> SessionsAsync(E2e e2e, int storyId, CancellationToken ct)
    {
        await using var db = e2e.Db();
        var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(storyId), ct);
        return await db.WorkerSessions.AsNoTracking().Where(s => s.WorkItemId == item.Id).OrderBy(s => s.Id).ToListAsync(ct);
    }

    private static async Task StopAsync(Process? child, OwnProcess? own)
    {
        // Only a child this test started, by its own pid.
        if (child is { HasExited: false } && own is not null)
        {
            own.KillIfStillRunning();
            await child.WaitForExitAsync(CancellationToken.None);
        }
        child?.Dispose();
    }

    /// <summary>
    /// P4b-AT5 "Two at once": with two slots and <c>Intake:MaxConcurrentRuns=2</c>, <c>factory work</c> has two items in a worker
    /// session at the same time — overlapping session rows in the ledger, each item in its own slot — and both merge.
    /// </summary>
    [Fact]
    public async Task P4b_AT5_two_items_are_in_a_worker_session_at_the_same_time_each_in_its_own_slot_and_both_merge()
    {
        Harness.RequireOptIn();
        var (first, second) = TwoStories("FACTORY_E2E_P4B_SLOT_STORIES",
            "two To Do stories in the watch scope (sc-1,sc-2), each a small fix that takes the worker a few minutes and can merge");
        var scope = E2e.WatchScope();
        await using var e2e = await E2e.StartAsync("df_e2e_p4b_at5");
        var ct = TestContext.Current.CancellationToken;
        var environment = e2e.ChildEnvironment(new Dictionary<string, string?>(scope)
        {
            ["Worker:Slots"] = "2",
            ["Intake:MaxConcurrentRuns"] = "2",
        }, CrashRestartTests.FreePort());

        var host = CrashRestartTests.StartFactory(environment, "work");
        var own = OwnProcess.Of(host);
        try
        {
            foreach (var story in new[] { first, second })
            {
                await E2e.WaitForAsync($"{StoryId.Format(story)} merged", RunTimeout, async () =>
                {
                    Assert.False(host.HasExited, $"factory work exited early with {(host.HasExited ? host.ExitCode : 0)}");
                    var transitions = await e2e.TransitionsAsync(story, ct);
                    Assert.DoesNotContain(transitions, t => t.State is WorkState.Escalated or WorkState.Cancelled);
                    return transitions.Any(t => t.State == WorkState.Merge);
                }, ct);
            }

            // Each item in its own slot, recorded once.
            var slots = new[] { Assert.Single(await SlotsAsync(e2e, first, ct)), Assert.Single(await SlotsAsync(e2e, second, ct)) };
            Assert.Equal(["1 _factory", "2 _factory2"], slots.Order()); // with the default Worker:RunAs
            // Two worker sessions at the same time: a session row of one item overlaps one of the other.
            var (a, b) = (await SessionsAsync(e2e, first, ct), await SessionsAsync(e2e, second, ct));
            Assert.True(a.Any(x => b.Any(y => x.StartedAt < (y.EndedAt ?? DateTimeOffset.MaxValue) && y.StartedAt < (x.EndedAt ?? DateTimeOffset.MaxValue))),
                "no worker session of one item overlaps one of the other: " + string.Join("; ",
                    a.Concat(b).Select(s => $"item {s.WorkItemId} {s.StartedAt:HH:mm:ss}–{s.EndedAt:HH:mm:ss}")));
            // Both merged, each by its own pull request.
            foreach (var story in new[] { first, second })
            {
                await using var db = e2e.Db();
                var repo = RepoRef.Parse((await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(story), ct)).Repo);
                var pull = Assert.Single(await E2e.PullRequestsAsync(repo, story, ct));
                Assert.True(pull.GetProperty("merged_at").ValueKind == System.Text.Json.JsonValueKind.String, $"{StoryId.Format(story)}'s pull request is not merged");
            }
        }
        finally
        {
            await StopAsync(host, own);
        }
    }

    /// <summary>
    /// P4b-AT6 "Slot affinity": two <c>factory run</c> processes, one item each, both in a worker session in different slots. The
    /// first is SIGKILLed during Implement and started again: it resumes the same session in the same slot, while the other item
    /// goes on in the other slot — nothing of the other item is deleted or killed.
    /// </summary>
    [Fact]
    public async Task P4b_AT6_a_run_killed_during_implement_resumes_in_the_same_slot_while_another_item_runs_in_the_other()
    {
        Harness.RequireOptIn();
        var (killed, other) = TwoStories("FACTORY_E2E_P4B_AFFINITY_STORIES",
            "two To Do stories (sc-1,sc-2): the first is killed and resumed, the second runs beside it; each fix takes the worker several minutes");
        await using var e2e = await E2e.StartAsync("df_e2e_p4b_at6");
        var ct = TestContext.Current.CancellationToken;
        var environment = e2e.ChildEnvironment(new Dictionary<string, string?> { ["Worker:Slots"] = "2" }, CrashRestartTests.FreePort());
        Process Run(int story) => CrashRestartTests.StartFactory(environment, "run", StoryId.Format(story), "--ignore-scope");
        async Task InSessionAsync(Process run, int story) =>
            await E2e.WaitForAsync($"{StoryId.Format(story)}'s session checkpoint", SessionTimeout, async () =>
            {
                Assert.False(run.HasExited, $"factory run {StoryId.Format(story)} exited early with {(run.HasExited ? run.ExitCode : 0)}");
                return (await e2e.HistoryAsync(story, ct)).Any(e => e.Step == RunPipeline.Steps.Session);
            }, ct);

        var firstRun = Run(killed);
        var firstOwn = OwnProcess.Of(firstRun);
        Process? otherRun = null, resumed = null;
        OwnProcess? otherOwn = null, resumedOwn = null;
        try
        {
            await InSessionAsync(firstRun, killed);
            otherRun = Run(other);
            otherOwn = OwnProcess.Of(otherRun);
            await InSessionAsync(otherRun, other);

            // Both are in a worker session, in different slots.
            var killedSlot = Assert.Single(await SlotsAsync(e2e, killed, ct));
            var otherSlot = Assert.Single(await SlotsAsync(e2e, other, ct));
            Assert.NotEqual(killedSlot, otherSlot);
            var before = await e2e.HistoryAsync(killed, ct);
            Assert.DoesNotContain(before, e => e.Step is null && e.State == WorkState.Review);
            var session = before.Last(e => e.Step == RunPipeline.Steps.Session).ClaudeSessionId;
            var otherWorker = int.Parse((await e2e.HistoryAsync(other, ct)).Last(e => e.Step == RunPipeline.Steps.WorkerStarted).Detail!);
            Assert.False(otherRun.HasExited);

            firstOwn.KillIfStillRunning(); // SIGKILL to this child's own pid only
            await firstRun.WaitForExitAsync(ct);
            resumed = Run(killed);
            resumedOwn = OwnProcess.Of(resumed);
            await FactoryWorkTests.WaitForStateAsync(e2e, killed, WorkState.Review, RunTimeout, ct);

            // The killed item: the same session, resumed in the same slot (never recorded again, never another one).
            var history = await e2e.HistoryAsync(killed, ct);
            Assert.All(history.Where(e => e.Step == RunPipeline.Steps.Session), e => Assert.Equal(session, e.ClaudeSessionId));
            Assert.Equal([killedSlot], await SlotsAsync(e2e, killed, ct));
            Assert.Equal([session], (await SessionsAsync(e2e, killed, ct)).Select(s => s.ClaudeSessionId).Distinct());

            // The other item, in the other slot, was not touched by the kill, the resume or the leftover worker's stop: its one
            // session went on (or finished by itself), its worktree was never lost, and it reaches Review with its one pull request.
            var otherBefore = await e2e.HistoryAsync(other, ct);
            Assert.True(otherBefore.Any(e => e.Step == RunPipeline.Steps.WorkerDone) || CrashRestartTests.IsRunning(otherWorker),
                $"the other item's worker (sudo pid {otherWorker}) is gone without having finished");
            await FactoryWorkTests.WaitForStateAsync(e2e, other, WorkState.Review, RunTimeout, ct);
            var otherHistory = await e2e.HistoryAsync(other, ct);
            Assert.Equal([otherSlot], await SlotsAsync(e2e, other, ct));
            Assert.Single(otherHistory, e => e.Step == RunPipeline.Steps.WorkerStarted);
            Assert.Single((await SessionsAsync(e2e, other, ct)).Select(s => s.ClaudeSessionId).Distinct());
            Assert.DoesNotContain(otherHistory, e => e.Step is RunPipeline.Steps.OrphanKilled or RunPipeline.Steps.WorktreeLost);
            Assert.DoesNotContain(otherHistory, e => e.Step is null && e.State is WorkState.Escalated or WorkState.Paused);
            foreach (var story in new[] { killed, other })
            {
                await using var db = e2e.Db();
                var repo = RepoRef.Parse((await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(story), ct)).Repo);
                Assert.Single(await E2e.PullRequestsAsync(repo, story, ct));
            }
        }
        finally
        {
            await StopAsync(firstRun, firstOwn);
            await StopAsync(otherRun, otherOwn);
            await StopAsync(resumed, resumedOwn);
        }
    }
}
