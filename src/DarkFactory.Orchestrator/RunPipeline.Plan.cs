using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator;

public sealed partial class RunPipeline
{
    /// <summary>
    /// Plan (sc-25870): a plan item is planned into stories, each step a ledger row before the next (E4). One read-only planner
    /// session proposes a plan (<see cref="PlanSessionAsync"/>); the code checks it (<see cref="PlanParser"/>, E1) — an answer that
    /// breaks a rule is recorded (<see cref="Steps.PlanRejected"/>) and escalates the item with no story created; a valid one is
    /// recorded (<see cref="Steps.Plan"/>), then <see cref="Steps.PlanCreating"/> right before its stories are created on the board
    /// through the work source (<see cref="IWorkSource.CreateChildrenAsync"/>, idempotent: a retry reuses what an earlier call
    /// created) and their ids recorded (<see cref="Steps.PlanStories"/>), and the item moves to <see cref="WorkState.Planned"/>, where
    /// it waits on them and releases them in dependency order (<see cref="PlannedAsync"/>). Which plan a later run uses:
    /// <see cref="ReusesRecordedPlan"/>.
    /// </summary>
    private async Task PlanAsync(Run run, CancellationToken ct)
    {
        var item = run.Item;
        var history = await ledger.HistoryAsync(item, ct);
        var planned = history.FindLastIndex(e => e.Step == Steps.Plan);
        var creating = history.FindLastIndex(e => e.Step == Steps.PlanCreating);
        var created = history.FindLastIndex(e => e.Step == Steps.PlanStories);
        if (planned >= 0 && created > planned)
        {
            // Every story of the plan is on the board and recorded: only the transition is left.
            var recorded = history[created].Detail ?? "";
            await ledger.RecordAsync(item, WorkState.Planned, history[created].ClaudeSessionId,
                $"{recorded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} stories: {recorded}", ct);
            return;
        }
        Plan plan;
        string? session;
        var reused = ReusesRecordedPlan(history);
        if (reused)
        {
            session = history[planned].ClaudeSessionId;
            try
            {
                plan = Plan.FromDetail(history[planned].Detail);
            }
            catch (PlanFormatException ex)
            {
                throw new WorkerFailedException($"The plan recorded at {history[planned].RecordedAt:u} no longer validates, so this run created "
                    + $"no story from it: {ex.Message}. "
                    + (creating > planned
                        ? "Some of its stories may already be on the board, so a re-run would use the same plan and fail the same way: "
                            + "finish or archive those stories by hand."
                        : "None of its stories was created: a re-run plans afresh."));
            }
            log.WriteLine($"[plan] continuing with the plan recorded at {history[planned].RecordedAt:u}");
        }
        else
        {
            (var answer, session) = await PlanSessionAsync(run, history, ct);
            try
            {
                plan = PlanParser.Parse(answer);
            }
            catch (PlanFormatException ex)
            {
                await ledger.CheckpointAsync(item, Steps.PlanRejected, session, ex.Message, ct);
                throw new WorkerFailedException($"The planner's answer is not a valid plan, so no story was created: {ex.Message}");
            }
            await ledger.CheckpointAsync(item, Steps.Plan, session, plan.ToDetail(), ct);
            log.WriteLine($"[plan] {plan.Stories.Count} stories planned");
        }
        // A Stop or Pause that arrived while the planner ran: no story is created for a stopped item.
        await ThrowIfControlledAsync(item, ct);

        // On the ledger before the board is asked for anything: from here on a story of this plan may exist, so the plan is kept.
        if (!reused || creating < planned)
        {
            await ledger.CheckpointAsync(item, Steps.PlanCreating, session, null, ct);
        }
        IReadOnlyList<int> ids;
        try
        {
            ids = await source.CreateChildrenAsync(run.Story.Id, plan.Children(run.Story.Ref, run.Repo), ct);
        }
        catch (ChildrenRefusedException refused)
        {
            throw new WorkerFailedException($"The plan's stories were not created, none of them: {refused.Message}.");
        }
        if (ids.Count != plan.Stories.Count)
        {
            throw new InvalidOperationException($"The board returned {ids.Count} stories for a plan of {plan.Stories.Count}.");
        }
        var stories = string.Join(' ', ids.Select(Naming.Format));
        await ledger.CheckpointAsync(item, Steps.PlanStories, session, stories, ct);
        log.WriteLine($"[plan] stories on the board: {stories}");
        await ledger.RecordAsync(item, WorkState.Planned, session, $"{plan.Stories.Count} stories: {stories}", ct);
    }

    /// <summary>
    /// Why a story of a plan may not start yet (sc-25871, E7), or null when it may: the blockers the board relates to it
    /// (<see cref="IWorkSource.BlockersAsync"/>: stories of its plan or not) that the factory has not merged
    /// (<see cref="PlanRelease.Satisfied"/>), named in a claim refusal (<see cref="PlanRelease.Refusal"/>). Only a plan's story
    /// (<see cref="WorkItem.PlanItemId"/>) is held to its blockers: a story a person wrote keeps the board's relations as the owner's
    /// own business, as before. Reads only.
    /// </summary>
    private async Task<string?> BlockedAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        if (item.PlanItemId is null)
        {
            return null;
        }
        var blockers = (await source.BlockersAsync(storyId, ct)).Select(Naming.Format).ToList();
        if (blockers.Count == 0)
        {
            return null;
        }
        var known = await ledger.ItemsByIdAsync(Naming.Source, blockers, ct);
        var open = blockers.Where(b => !PlanRelease.Satisfied(known.TryGetValue(b, out var k) ? k.History : null)).ToList();
        return open.Count == 0 ? null : PlanRelease.Refusal(open);
    }

    /// <summary>
    /// For an item parked on a claim refused for a blocker (<see cref="PlanRelease.IsBlockedRefusal"/>): the refusal as it stands
    /// now, or null once every blocker has merged (or the item was parked for something else) and the run may go on.
    /// </summary>
    private async Task<string?> StillBlockedAsync(WorkItem item, int storyId, CancellationToken ct) =>
        PlanRelease.IsBlockedRefusal((await ledger.HistoryAsync(item, ct)).LastOrDefault(e => e.Step is null)?.Detail)
            ? await BlockedAsync(item, storyId, ct)
            : null;

    /// <summary>Whether the item is a plan item waiting on its stories: its run needs no worker, so no worker run lock or sandbox.</summary>
    public static async Task<bool> WaitsOnStoriesAsync(WorkLedger ledger, ItemNaming naming, int storyId, CancellationToken ct) =>
        await ledger.StateOfAsync(naming.Source, naming.Format(storyId), ct) == WorkState.Planned;

    /// <summary>
    /// Planned (sc-25871): one evaluation of a plan item waiting on its stories, under its run lock, with no worker. The orchestrator,
    /// not a person, releases the plan's stories: every story that has not merged and was not released yet has its blockers read from
    /// the board, and each one whose blockers have all merged (<see cref="PlanRelease.Decide"/>) is moved to the source's ready state
    /// (<see cref="IWorkSource.MakeReadyAsync"/>) and recorded (<see cref="Steps.PlanReleased"/>) — the board write first, then its
    /// row: a run that died between the two asks again, and the source writes nothing for a story that already left the backlog,
    /// so nothing is released twice (E4). A released story is then picked up like any ready story. A control that pauses the plan
    /// item (the factory, its epic, the freeze) releases nothing. Stories a blocker outside the plan holds are recorded on the plan
    /// item when that changes (<see cref="Steps.PlanHeld"/>). Once every story has merged the plan is closed out
    /// (<see cref="ClosePlanAsync"/>). A story that escalated or was cancelled never satisfies its dependents: they stay in the
    /// backlog and the plan item stays here. A board that cannot be read or written throws: nothing is recorded and the next run
    /// evaluates again.
    /// </summary>
    private async Task<RunOutcome> PlannedAsync(WorkItem item, int storyId, List<LedgerEntry> history, CancellationToken ct)
    {
        var recorded = history.FindLastIndex(e => e.Step == Steps.PlanStories);
        var stories = PlanRelease.StoriesOf(recorded < 0 ? null : history[recorded].Detail);
        if (stories.Count == 0)
        {
            throw new InvalidOperationException($"{item.ExternalId} is {item.State} but its ledger records no story of its plan.");
        }
        var since = history.Skip(recorded + 1).ToList();
        var known = await ledger.ItemsByIdAsync(Naming.Source, stories, ct);
        var released = since.Where(e => e.Step == Steps.PlanReleased).Select(e => PlanRelease.StoriesOf(e.Detail).FirstOrDefault() ?? "")
            .ToHashSet(StringComparer.Ordinal);
        bool Merged(string id) => PlanRelease.Satisfied(known.TryGetValue(id, out var k) ? k.History : null);
        if (stories.All(Merged))
        {
            return await ClosePlanAsync(item, storyId, stories, known, since, ct);
        }

        // What blocks each story still waiting, from the board: the plan's own relations and any a person added since.
        var blockers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var story in stories.Where(s => !Merged(s) && !released.Contains(s)))
        {
            blockers[story] = (await source.BlockersAsync(IdOf(story), ct)).Select(Naming.Format).ToList();
        }
        var outside = blockers.Values.SelectMany(b => b).Where(b => !stories.Contains(b, StringComparer.Ordinal)).ToList();
        var outsiders = outside.Count == 0 ? [] : await ledger.ItemsByIdAsync(Naming.Source, outside, ct);
        var decision = PlanRelease.Decide(stories, released, blockers,
            id => Merged(id) || PlanRelease.Satisfied(outsiders.TryGetValue(id, out var o) ? o.History : null));

        var done = new List<string>();
        foreach (var story in decision.Release)
        {
            // A Pause (the factory's, the epic's, the item's, the freeze) that arrived meanwhile: nothing more is released.
            if (await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) is not ControlState.Running and var control)
            {
                log.WriteLine($"[{item.State}] {item.ExternalId} is {control} by a control; releasing nothing more");
                break;
            }
            var moved = await source.MakeReadyAsync(IdOf(story), ct);
            // Released all the same when it is outside the watch scope: the listing will not find it, so say so.
            var listed = ignoreScope || await source.InScopeAsync(IdOf(story), ct);
            await ledger.CheckpointAsync(item, Steps.PlanReleased, null,
                story + (moved ? "" : " (it had already left the backlog; left where it is)")
                    + (listed ? "" : $" (outside the watch scope: {source.ScopeReturnHint}, or run `factory run {story} --ignore-scope`)"), ct);
            log.WriteLine($"[{item.State}] {item.ExternalId}: released {story}{(listed ? "" : " (outside the watch scope: nothing will pick it up)")}");
            done.Add(story);
        }
        var heldByOutsiders = PlanRelease.HeldByOutsiders(decision.Held);
        if (heldByOutsiders.Length > 0 && heldByOutsiders != since.LastOrDefault(e => e.Step == Steps.PlanHeld)?.Detail)
        {
            await ledger.CheckpointAsync(item, Steps.PlanHeld, null, heldByOutsiders, ct);
        }
        log.WriteLine($"[{item.State}] {item.ExternalId}: {stories.Count(Merged)} of {stories.Count} stories merged"
            + (done.Count == 0 ? "" : $"; released {string.Join(", ", done)}")
            + (decision.Held.Count == 0 ? "" : $"; waiting: {string.Join("; ", decision.Held.Select(h => $"{h.Id} on {string.Join(", ", h.By)}"))}"));
        return await OutcomeAsync(item, null, ct);

        int IdOf(string story) => Naming.TryParse(story, out var id)
            ? id
            : throw new InvalidOperationException($"{item.ExternalId}'s plan records '{story}', which is not a {Naming.Noun} id.");
    }

    /// <summary>
    /// Every story of the plan has merged (sc-25871): the plan item is reported merged on its board (<see cref="Steps.MergedReported"/>),
    /// gets a closeout rendered from the ledger (<see cref="LedgerReport.PlanCloseout"/>: each story, its pull request, its outcome;
    /// <see cref="Steps.Closeout"/>) and goes to Done, each step recorded before the next so a resumed run redoes none. A closeout the
    /// board refuses is recorded <c>failed: …</c> and tried again by the next runs, like a merged item's, up to
    /// <see cref="MaxCloseoutAttempts"/>; after the last the item goes to Done all the same, the row saying the closeout is left to
    /// post by hand (the plan is finished either way).
    /// </summary>
    private async Task<RunOutcome> ClosePlanAsync(WorkItem item, int storyId, IReadOnlyList<string> stories,
        Dictionary<string, (WorkItem Item, List<LedgerEntry> History)> known, List<LedgerEntry> since, CancellationToken ct)
    {
        if (!since.Any(e => e.Step == Steps.MergedReported))
        {
            await source.ReportStateAsync(storyId, BoardState.Merged, null, ct);
            await ledger.CheckpointAsync(item, Steps.MergedReported, null, $"{stories.Count} stories merged", ct);
        }
        var attempts = since.Where(e => e.Step == Steps.Closeout).ToList();
        var posted = attempts.Any(a => a.Detail == Posted);
        var tried = attempts.Count;
        string? error = null;
        if (!posted && tried < MaxCloseoutAttempts)
        {
            try
            {
                await source.CommentAsync(storyId, LedgerReport.PlanCloseout(item.ExternalId,
                    [.. stories.Select(s => new PlanStoryReport(s, known[s].Item.Title, known[s].History))]), ct);
                posted = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message;
                log.WriteLine($"[{item.State}] could not post the plan closeout on {item.ExternalId}: {ex.Message}");
            }
            await ledger.CheckpointAsync(item, Steps.Closeout, null, posted ? Posted : $"failed: {error}", ct);
            tried++;
        }
        if (!posted && tried < MaxCloseoutAttempts)
        {
            return await OutcomeAsync(item, $"{item.ExternalId}: closeout NOT posted: {error}; the next run tries again", ct);
        }
        await ledger.RecordAsync(item, WorkState.Done, null, $"{stories.Count} stories merged"
            + (posted ? "" : $"; closeout NOT posted after {tried} attempts: post it by hand"), ct);
        log.WriteLine($"[{item.State}] {item.ExternalId}: every story of the plan merged; the plan is closed out");
        return await OutcomeAsync(item, null, ct);
    }

    /// <summary>
    /// Whether the newest recorded plan (<see cref="Steps.Plan"/>) is the one this run creates, rather than planning afresh. It is
    /// when a story of it may be on the board — a <see cref="Steps.PlanCreating"/> or <see cref="Steps.PlanStories"/> row follows it —
    /// so no second plan's stories ever land beside the first's (E4); and when no Escalated row follows it (the run crashed, was
    /// paused or was interrupted: the same plan goes on). A plan the item escalated on before any board write (no
    /// <see cref="Steps.PlanCreating"/>) is dropped: the owner may have changed the story before re-queuing it and nothing of that plan
    /// exists, so the re-run plans afresh, and its own <see cref="Steps.Plan"/> row, the newest, is the one used from then on.
    /// </summary>
    internal static bool ReusesRecordedPlan(List<LedgerEntry> history)
    {
        var planned = history.FindLastIndex(e => e.Step == Steps.Plan);
        return planned >= 0
            && (history.FindLastIndex(e => e.Step is Steps.PlanCreating or Steps.PlanStories) > planned
                || !history.Skip(planned + 1).Any(e => e.Step is null && e.State == WorkState.Escalated));
    }

    /// <summary>
    /// One planner session (E2), always a fresh one (a planner session is never resumed: its checkout is throwaway and it has written
    /// nothing): a read-only worker on <see cref="WorkerModelClass.Planner"/> in the seat the <see cref="IPlanRunner"/> gives it, run
    /// like any worker session of the item (<see cref="RunSessionAsync"/>) — pid, session id and model class checkpointed, every line
    /// stored, Pause/Stop and the freeze watched, a usage-limited or class-refused session pausing the factory instead of escalating —
    /// and tainted from its start (<see cref="Taint.Planner"/>), so nothing it did can ever be pushed. A planner a crashed run left
    /// behind (<see cref="Steps.WorkerStarted"/> with no <see cref="Steps.WorkerDone"/>) is stopped first, like Implement's orphan.
    /// Returns its answer and its id.
    /// </summary>
    private async Task<(string? Answer, string? Session)> PlanSessionAsync(Run run, List<LedgerEntry> history, CancellationToken ct)
    {
        if (planner is null)
        {
            throw new InvalidOperationException($"{run.Item.ExternalId} is a plan item, but this pipeline has no planner.");
        }
        // Before the seat is set up: its sweep removes the checkout a crashed run's planner would still be reading.
        var attempt = CurrentWorkerAttempt(history);
        if (OrphanedWorkerPid(attempt) is { } pid && await worker.StopOrphanAsync(pid, ct))
        {
            await ledger.CheckpointAsync(run.Item, Steps.OrphanKilled, attempt.LastOrDefault(e => e.Step == Steps.Session)?.ClaudeSessionId,
                $"pid {pid}", ct);
            log.WriteLine($"[plan] stopped planner pid {pid} left running by an earlier run");
        }
        return await planner.RunAsync(run.Item, run.Repo, async (seat, c) =>
        {
            try
            {
                var (session, result) = await RunSessionAsync(run, seat.Worker, seat.Path, seat.McpConfig, implementing: false, null,
                    _ => PlanPrompt.Build(run.Spec, run.Repo) + PlanPrompt.McpNote(seat.ToolRules), [], [Taint.Planner], "plan",
                    WorkerModelClass.Planner, c);
                return (result.ResultText, session);
            }
            catch (ControlRequestedException request) when (request.WorkerStillRunning)
            {
                // The planner's seat must not be removed under a worker that would not stop.
                throw WorkerStillRunning.Mark(request);
            }
        }, ct);
    }
}
