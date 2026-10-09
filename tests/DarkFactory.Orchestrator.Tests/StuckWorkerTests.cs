using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Replays a recorded stream-json transcript as a worker would stream it, through the callbacks the pipeline gives it. Like the
/// pause hook (a PreToolUse hook: the tool call streams, then the hook denies it and ends the session), it stops at the first
/// tool call after <paramref name="paused"/> turns true, ending the session as a success with <c>terminal_reason: hook_stopped</c>.
/// No process and no signal: the interrupt is the pause flag seam (<see cref="IWorker.RequestPause"/>) only.
/// </summary>
internal sealed class TranscriptReplay(string[] lines, Func<bool> paused)
{
    /// <summary>Silence before the line with this index (the session streams nothing meanwhile).</summary>
    public Dictionary<int, TimeSpan> Silences { get; init; } = [];

    /// <summary>A worker that ignores the pause hook (e.g. a repo setting turned hooks off): it never stops on its own.</summary>
    public bool IgnoresPause { get; init; }

    /// <summary>How many lines were streamed.</summary>
    public int Streamed { get; private set; }

    /// <summary>Whether the session ended at a tool boundary because it was asked to stop.</summary>
    public bool StoppedAtToolBoundary { get; private set; }

    public async Task<WorkerResult> RunAsync(WorkerCallbacks callbacks, CancellationToken ct)
    {
        var state = new StreamJsonState();
        string? reported = null;
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
        {
            if (Silences.TryGetValue(i, out var silence))
            {
                await Task.Delay(silence, ct);
            }
            await callbacks.OnLine!(line, ct);
            Streamed++;
            state.Accept(line);
            if (state.SessionId is { } sid && sid != reported)
            {
                reported = sid;
                await callbacks.OnSession!(sid, ct);
            }
            if (line.Contains("\"type\": \"tool_use\"") && paused() && !IgnoresPause)
            {
                StoppedAtToolBoundary = true;
                return new WorkerResult(state.SessionId, 0, false, "success", "", "", WorkerResult.HookStoppedReason);
            }
        }
        if (IgnoresPause && paused())
        {
            await Task.Delay(Timeout.Infinite, ct); // runs on until the pipeline stops it
        }
        return new WorkerResult(state.SessionId, 0, state.ResultIsError, state.ResultSubtype, state.ResultText, "", state.TerminalReason);
    }
}

/// <summary>
/// sc-25388: stuck-worker detection over replayed stream-json transcripts, through the real pipeline (in-memory ledger, real
/// control table). Interrupting a worker only ever goes through the pause-flag seam of a fake worker, never a signal.
/// </summary>
public class StuckWorkerTests
{
    private static readonly WorkStory Story = new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");

    /// <summary>A worker that replays one transcript per session, in order; the pause flag is per run, as ClaudeWorker's is.</summary>
    private sealed class ReplayWorker(params TranscriptReplay[] sessions) : IWorker
    {
        private volatile bool _pause;
        public List<string?> Resumes { get; } = [];
        public List<string> Prompts { get; } = [];
        public int PauseRequests;
        public bool Paused => _pause;

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, WorkerCallbacks? callbacks, CancellationToken ct)
        {
            _pause = false;
            Resumes.Add(resumeSessionId);
            Prompts.Add(prompt);
            await callbacks!.OnStarted!(WorkerPid, ct);
            return await sessions[Resumes.Count - 1].RunAsync(callbacks, ct);
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);

        public void RequestPause(string workingDirectory)
        {
            Interlocked.Increment(ref PauseRequests);
            _pause = true;
        }

        public void CancelPause(string workingDirectory) => _pause = false;
    }

    private sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Harness() => Db = new LedgerDbContext(_options);

        public LedgerDbContext Db { get; }
        public IControls Controls => new LedgerControls(new LedgerDbContextFactory(_options), TimeProvider.System);
        public FakeWorkSource Stories { get; } = new(Story);
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public InProcessRunLocks Locks { get; } = new();
        public TimeSpan PauseGrace { get; init; } = TimeSpan.FromSeconds(30);
        public StuckDetection? Stuck { get; init; }

        public Task<RunOutcome> Run(IWorker worker, CancellationToken ct = default) =>
            new RunPipeline(Stories, new WorkLedger(Db, TimeProvider.System), Locks, Workspaces, worker, Prs, Sandbox, TextWriter.Null,
                    controls: Controls, pauseGrace: PauseGrace, controlPollInterval: TimeSpan.FromMilliseconds(10), stuck: Stuck)
                .RunAsync(77, ct);

        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        public async Task<List<WorkState>> Transitions() => (await Rows()).Where(r => r.Step is null).Select(r => r.State).ToList();
        public async Task<List<string?>> Steps() => (await Rows()).Where(r => r.Step is not null).Select(r => r.Step).ToList();
    }

    [Fact]
    public async Task A_replayed_transcript_that_loops_is_interrupted_at_its_next_tool_call_and_records_a_failed_round_then_a_fresh_session_retries()
    {
        var h = new Harness();
        ReplayWorker worker = null!;
        var looping = new TranscriptReplay(StuckFixtures.Loop, () => worker.Paused);
        var fresh = new TranscriptReplay(StuckFixtures.EditTestCycle, () => worker.Paused);
        worker = new ReplayWorker(looping, fresh);

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Review, outcome.State);
        // Interrupted through the pause seam, at the tool call after the fifth repetition's result (the sixth Edit), and the
        // session ended there: nothing after that tool call was streamed.
        Assert.Equal(1, worker.PauseRequests);
        Assert.True(looping.StoppedAtToolBoundary);
        Assert.Equal(StuckFixtures.ToolUseLines(StuckFixtures.Loop)[6] + 1, looping.Streamed);
        // The failed round is on the ledger: the detection (before the interrupt), then the retry with a fresh session in a fresh
        // worktree — the looping session is never resumed and its edits are never pushed.
        var rows = await h.Rows();
        var stuck = rows.Single(r => r.Step == RunPipeline.Steps.Stuck);
        Assert.Equal(StuckFixtures.LoopSession, stuck.ClaudeSessionId);
        Assert.Contains("the last 5 turns were near-identical", stuck.Detail);
        Assert.Contains("repeating tool calls: Edit", stuck.Detail);
        var retry = rows.Single(r => r.Step == RunPipeline.Steps.StuckRetry);
        Assert.Contains($"session {StuckFixtures.LoopSession} stuck", retry.Detail);
        Assert.Contains("stuck session 1 of 2", retry.Detail);
        Assert.Equal([null, null], worker.Resumes);
        // The fresh session is told the earlier one looped, and on which tool calls — tool names only, no transcript content.
        Assert.DoesNotContain("stuck in a loop", worker.Prompts[0]);
        Assert.Contains("stuck in a loop, repeating the same tool calls (Edit) with the same results", worker.Prompts[1]);        Assert.Equal(
            ["prepare michaeltrefry/dark-factory-sandbox factory/sc-77", "remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77",
             "prepare michaeltrefry/dark-factory-sandbox factory/sc-77", "push michaeltrefry/dark-factory-sandbox factory/sc-77 sc-77: Whitespace counts as a word",
             "remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77"],
            h.Workspaces.Calls);
        // Only the fresh session's work is pushed: its grant names it alone.
        Assert.Equal([StuckFixtures.CycleSession], h.Workspaces.Grants.Single().Sessions);
        Assert.True(rows.FindIndex(r => r.Step == RunPipeline.Steps.Stuck) < rows.FindIndex(r => r.Step == RunPipeline.Steps.StuckRetry));
        Assert.Equal(StuckFixtures.CycleSession, rows.Single(r => r.Step == RunPipeline.Steps.WorkerDone).ClaudeSessionId);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], await h.Transitions());
    }

    [Fact]
    public async Task An_implementer_stuck_again_after_its_retry_escalates_and_does_not_loop_forever()
    {
        var h = new Harness();
        ReplayWorker worker = null!;
        worker = new ReplayWorker(new TranscriptReplay(StuckFixtures.Loop, () => worker.Paused), new TranscriptReplay(StuckFixtures.Loop, () => worker.Paused));

        var outcome = await h.Run(worker);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(2, worker.Resumes.Count); // the first session and one retry; no third
        Assert.Equal(2, (await h.Steps()).Count(s => s == RunPipeline.Steps.Stuck));
        Assert.Single(await h.Steps(), s => s == RunPipeline.Steps.StuckRetry);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
        Assert.Empty(h.Prs.Opened);
        var comment = h.Stories.Comments.Single();
        Assert.Contains("stuck in a loop in 2 sessions in a row", comment);
        Assert.Contains("repeating tool calls: Edit", comment);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Escalated], await h.Transitions());
    }

    [Fact]
    public async Task A_replayed_transcript_with_a_long_silence_and_then_progress_is_not_interrupted()
    {
        var h = new Harness();
        ReplayWorker worker = null!;
        var lines = StuckFixtures.SilenceThenProgress;
        // The slow test run's result arrives after a silence of fifty control polls.
        var replay = new TranscriptReplay(lines, () => worker.Paused) { Silences = { [StuckFixtures.ResultLines(lines)[1]] = TimeSpan.FromMilliseconds(500) } };
        worker = new ReplayWorker(replay);

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Review, outcome.State);
        Assert.Equal(0, worker.PauseRequests);
        Assert.False(replay.StoppedAtToolBoundary);
        Assert.Equal(lines.Length, replay.Streamed);
        Assert.DoesNotContain(RunPipeline.Steps.Stuck, await h.Steps());
        Assert.Equal(StuckFixtures.SilenceSession, (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.WorkerDone).ClaudeSessionId);
    }

    [Fact]
    public async Task A_replayed_transcript_that_runs_the_same_test_command_after_each_different_edit_is_not_interrupted()
    {
        var h = new Harness();
        ReplayWorker worker = null!;
        var replay = new TranscriptReplay(StuckFixtures.EditTestCycle, () => worker.Paused);
        worker = new ReplayWorker(replay);

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(0, worker.PauseRequests);
        Assert.Equal(StuckFixtures.EditTestCycle.Length, replay.Streamed);
        Assert.DoesNotContain(RunPipeline.Steps.Stuck, await h.Steps());
    }

    [Fact]
    public async Task Worker_StuckRepeats_above_the_loops_length_lets_the_session_run_to_its_end()
    {
        var h = new Harness { Stuck = new StuckDetection(Repeats: 7) };
        ReplayWorker worker = null!;
        var replay = new TranscriptReplay(StuckFixtures.Loop, () => worker.Paused);
        worker = new ReplayWorker(replay);

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(0, worker.PauseRequests);
        Assert.Equal(StuckFixtures.Loop.Length, replay.Streamed);
    }

    [Fact]
    public async Task A_stuck_worker_that_ignores_the_tool_boundary_is_stopped_after_the_grace_and_its_round_still_fails()
    {
        var h = new Harness { PauseGrace = TimeSpan.FromMilliseconds(200) };
        ReplayWorker worker = null!;
        var ignoring = new TranscriptReplay(StuckFixtures.Loop, () => worker.Paused) { IgnoresPause = true };
        worker = new ReplayWorker(ignoring, new TranscriptReplay(StuckFixtures.EditTestCycle, () => worker.Paused));

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(StuckFixtures.Loop.Length, ignoring.Streamed); // it ran on past the boundary until the grace stopped it
        Assert.Single(await h.Steps(), s => s == RunPipeline.Steps.StuckRetry);
        Assert.Equal([null, null], worker.Resumes);
        Assert.Equal(WorkState.Review, outcome.State);
    }

    [Fact]
    public async Task A_run_stopped_after_the_detection_never_resumes_the_looping_session()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        ReplayWorker worker = null!;
        // Ctrl-C lands as the interrupt is asked for: the run stops with the detection on the ledger and nothing after it.
        var looping = new TranscriptReplay(StuckFixtures.Loop, () =>
        {
            if (worker.Paused)
            {
                cts.Cancel();
            }
            return false;
        });
        worker = new ReplayWorker(looping, new TranscriptReplay(StuckFixtures.EditTestCycle, () => worker.Paused));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(worker, cts.Token));
        Assert.Contains(RunPipeline.Steps.Stuck, await h.Steps());
        Assert.Equal(WorkState.Paused, (await h.Transitions())[^1]);

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([null, null], worker.Resumes); // a fresh session, not --resume of the looping one
        Assert.Single(await h.Steps(), s => s == RunPipeline.Steps.StuckRetry);
        Assert.Equal(WorkState.Review, outcome.State);
    }

    [Fact]
    public async Task A_pause_and_continue_while_the_stuck_worker_is_still_mid_turn_do_not_withdraw_the_interrupt()
    {
        var h = new Harness();
        ReplayWorker worker = null!;
        var lines = StuckFixtures.Loop;
        // The model takes its time over the turn after the fifth repetition: a Pause and a Continue land meanwhile.
        var looping = new TranscriptReplay(lines, () => worker.Paused) { Silences = { [StuckFixtures.ToolUseLines(lines)[6]] = TimeSpan.FromMilliseconds(600) } };
        worker = new ReplayWorker(looping, new TranscriptReplay(StuckFixtures.EditTestCycle, () => worker.Paused));
        var controls = h.Controls;
        var meddle = Task.Run(async () =>
        {
            await Until(() => worker.PauseRequests >= 1);                 // the stuck interrupt
            await controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Paused, "tester", CancellationToken.None);
            await Until(() => worker.PauseRequests >= 2);                 // the Pause, seen by the watch
            await controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Running, "tester", CancellationToken.None);
        });

        var outcome = await h.Run(worker);
        await meddle;

        Assert.True(looping.StoppedAtToolBoundary); // the Continue withdrew the Pause, not the stuck interrupt
        Assert.Contains(RunPipeline.Steps.StuckRetry, await h.Steps());
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Review, outcome.State);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }
}

/// <summary>sc-25388: the dashboard's read of when a running session last spoke, which marks a silent one quiet (and nothing more).</summary>
public class QuietSessionDataTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_running_sessions_last_event_is_read_and_it_is_quiet_only_past_the_threshold()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new LedgerDbContext(options);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0);
        var item = await new WorkLedger(db, time).GetOrCreateAsync(RunPipeline.Source, "sc-1", "One", Sandbox.FullName, null, CancellationToken.None);
        var running = new WorkerSession { WorkItemId = item.Id, ClaudeSessionId = "live", Attempt = 2, StartedAt = T0 };
        var ended = new WorkerSession { WorkItemId = item.Id, ClaudeSessionId = "done", Attempt = 1, StartedAt = T0, EndedAt = T0 };
        db.WorkerSessions.AddRange(ended, running);
        await db.SaveChangesAsync();
        db.SessionEvents.AddRange(
            new SessionEvent { WorkerSessionId = running.Id, WorkItemId = item.Id, Sequence = 1, Type = "system", Payload = "{}", ReceivedAt = T0 },
            new SessionEvent { WorkerSessionId = running.Id, WorkItemId = item.Id, Sequence = 2, Type = "assistant", Payload = "{}", ReceivedAt = T0.AddMinutes(3) },
            new SessionEvent { WorkerSessionId = ended.Id, WorkItemId = item.Id, Sequence = 1, Type = "result", Payload = "{}", ReceivedAt = T0 });
        await db.SaveChangesAsync();
        var data = new DashboardData(new LedgerDbContextFactory(options), time, TimeSpan.FromMinutes(10));

        var sessions = (await data.ActiveItemsAsync(CancellationToken.None)).Single().Sessions;

        Assert.Equal(TimeSpan.FromMinutes(10), data.QuietThreshold);
        Assert.Equal(T0.AddMinutes(3), sessions.Single(s => s.ClaudeSessionId == "live").LastEventAt);
        Assert.Null(sessions.Single(s => s.ClaudeSessionId == "done").LastEventAt); // only running sessions are read
        Assert.Equal(T0.AddMinutes(3), (await data.SessionAsync("live", CancellationToken.None))!.Session.LastEventAt);
        var live = sessions.Single(s => s.ClaudeSessionId == "live");
        Assert.False(live.QuietAt(T0.AddMinutes(12), data.QuietThreshold));
        Assert.True(live.QuietAt(T0.AddMinutes(13), data.QuietThreshold));
        Assert.False(sessions.Single(s => s.ClaudeSessionId == "done").QuietAt(T0.AddDays(1), data.QuietThreshold));
        // No event yet: silent since it started.
        Assert.True((live with { LastEventAt = null }).QuietAt(T0.AddMinutes(10), data.QuietThreshold));
    }
}

/// <summary>sc-25388: a fix round whose fixer is found looping is a failed round that counts against the fix-round cap.</summary>
public class StuckFixRoundTests
{
    private static Func<RunPipelineTests.WorkerCall, Task<WorkerResult>> Replays(GatePipelineTests.Harness h, string[] lines) =>
        call => new TranscriptReplay(lines, () => h.PauseFlag).RunAsync(call.Callbacks, CancellationToken.None);

    private static async Task<List<FixProgress>> Progress(GatePipelineTests.Harness h) =>
        (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.FixProgress).Select(r => FixProgress.FromDetail(r.Detail)!).ToList();

    [Fact]
    public async Task A_stuck_fixer_fails_its_round_pushes_nothing_and_the_next_round_gets_a_fresh_fixer()
    {
        var h = new GatePipelineTests.Harness
        {
            Reviewer = new GatePipelineTests.FakeReviewer
            {
                Findings = r => r.Role == ReviewRoles.Correctness && r.Pull.HeadSha == GatePipelineTests.Sha1
                    ? [new Finding(Finding.Blocking, "whitespace-only input", "src/x.cs", 1, "WordCount(\"  \") returns 1")]
                    : [],
            },
        };
        h.WorkerOverrides[1] = Replays(h, StuckFixtures.Loop); // fix round 1's fixer loops

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.Review, WorkState.Fixing, WorkState.Review,
            WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch], await h.Transitions());
        var rows = await h.Rows();
        Assert.Contains("fix round 1 stuck: the last 5 turns were near-identical", rows.Where(r => r.Step is null && r.State == WorkState.Review).ElementAt(1).Detail);
        // Round 1 failed (recorded before the next round starts) with the head unchanged; round 2 made progress.
        var progress = await Progress(h);
        Assert.Equal([(1, FixProgress.Failed), (2, FixProgress.Progress)], progress.Select(p => (p.Round, p.Outcome)));
        Assert.Equal((GatePipelineTests.Sha1, GatePipelineTests.Sha1, 1, 1), (progress[0].FromSha, progress[0].ToSha, progress[0].BlockingBefore, progress[0].BlockingAfter));
        Assert.Contains("the fixer was stuck in a loop", progress[0].Reason);
        // The looping fixer's work was not pushed (the implementer's push and round 2's only), and round 2's fixer is a fresh session.
        Assert.Equal(2, h.Workspaces.Calls.Count(c => c.StartsWith("push")));
        Assert.Null(h.WorkerCalls[2].Resume);
        Assert.Equal([$"merge 1 {GatePipelineTests.ShaA}"], h.Merges);
        // The unchanged head kept its verdict: Sha1 was reviewed once, then only ShaA's correctness.
        Assert.Equal([GatePipelineTests.Sha1, GatePipelineTests.Sha1, GatePipelineTests.ShaA], h.Reviewer.Requests.Select(r => r.Pull.HeadSha));
    }

    [Fact]
    public async Task Stuck_fixers_count_against_the_fix_round_cap_and_escalate_there()
    {
        var h = new GatePipelineTests.Harness { Reviewer = GatePipelineTests.FakeReviewer.Blocking() };
        for (var round = 1; round <= Lifecycle.MaxFixRounds; round++)
        {
            h.WorkerOverrides[round] = Replays(h, StuckFixtures.Loop);
        }

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(Lifecycle.MaxFixRounds, (await h.Transitions()).Count(s => s == WorkState.Fixing));
        Assert.Equal(1 + Lifecycle.MaxFixRounds, h.WorkerCalls.Count); // no fourth fixer
        Assert.All(await Progress(h), p => Assert.Contains("stuck in a loop", p.Reason));
        Assert.Equal(1, h.Workspaces.Calls.Count(c => c.StartsWith("push"))); // the implementer's only
        Assert.Contains("after 3 fix rounds", h.Stories.Comments.Single());
        Assert.Contains($"; {Lifecycle.MaxFixRounds} round(s) failed because the fixer was stuck in a loop (the last 5 turns were near-identical",
            h.Stories.Comments.Single());
    }

    [Fact]
    public async Task A_stuck_ci_fixer_fails_its_round_and_ci_dispatches_the_next_one()
    {
        var h = new GatePipelineTests.Harness();
        h.GitHub.Ci[GatePipelineTests.Sha1] = new CiFacts(GatePipelineTests.Sha1, [new CheckFact("build-test", true, "failure")]);
        h.WorkerOverrides[1] = Replays(h, StuckFixtures.Loop); // CI fix round 1's fixer loops

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.CIHealing, WorkState.CI, WorkState.CIHealing,
            WorkState.CI, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch], await h.Transitions());
        var rows = await h.Rows();
        Assert.StartsWith("ci fix round 1 stuck:", rows.Where(r => r.Step is null && r.State == WorkState.CI).ElementAt(1).Detail);
        Assert.Equal(2, h.Workspaces.Calls.Count(c => c.StartsWith("push")));
        Assert.Null(h.WorkerCalls[2].Resume);
        // The stuck round's row is a failed step (sc-25389).
        Assert.Equal(StepOutcome.Failed, rows.Where(r => r.Step is null && r.State == WorkState.CI).ElementAt(1).Outcome);
    }

    [Fact]
    public async Task Stuck_ci_fixers_at_the_fix_round_cap_escalate_saying_the_ci_fixer_was_stuck()
    {
        var h = new GatePipelineTests.Harness();
        h.GitHub.Ci[GatePipelineTests.Sha1] = new CiFacts(GatePipelineTests.Sha1, [new CheckFact("build-test", true, "failure")]);
        for (var round = 1; round <= Lifecycle.MaxFixRounds; round++)
        {
            h.WorkerOverrides[round] = Replays(h, StuckFixtures.Loop);
        }

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(Lifecycle.MaxFixRounds, (await h.Transitions()).Count(s => s == WorkState.CIHealing));
        Assert.Equal(1, h.Workspaces.Calls.Count(c => c.StartsWith("push"))); // the implementer's only
        Assert.Contains("after 3 fix rounds (the cap is 3, shared by review and CI fixes)", outcome.Error);
        Assert.Contains($"; {Lifecycle.MaxFixRounds} round(s) failed because the CI fixer was stuck in a loop (the last 5 turns were near-identical",
            outcome.Error);
        Assert.Contains("because the CI fixer was stuck in a loop", h.Stories.Comments.Single());
    }

    [Fact]
    public async Task A_ci_cap_with_no_stuck_round_says_nothing_about_loops()
    {
        var h = new GatePipelineTests.Harness();
        foreach (var sha in new[] { GatePipelineTests.Sha1, GatePipelineTests.ShaA, GatePipelineTests.ShaB, GatePipelineTests.ShaC })
        {
            h.GitHub.Ci[sha] = new CiFacts(sha, [new CheckFact("build-test", true, "failure")]);
        }

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("after 3 fix rounds", outcome.Error);
        Assert.DoesNotContain("stuck", outcome.Error);
    }
}
