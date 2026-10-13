using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
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
    /// created); then the plan's changeset branch is created (sc-25872, <see cref="EnsureChangesetAsync"/>) and only then are the
    /// stories' ids recorded (<see cref="Steps.PlanStories"/>) — the row that makes them the plan's, without which none of them is
    /// claimed (<see cref="WithoutPlanAsync"/>), so no story of the plan ever starts before the branch it starts from exists — and
    /// the item moves to <see cref="WorkState.Planned"/>, where it waits on them and releases them in dependency order
    /// (<see cref="PlannedAsync"/>). Which plan a later run uses: <see cref="ReusesRecordedPlan"/>.
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
        await EnsureChangesetAsync(run, session, ct);
        var stories = string.Join(' ', ids.Select(Naming.Format));
        await ledger.CheckpointAsync(item, Steps.PlanStories, session, stories, ct);
        log.WriteLine($"[plan] stories on the board: {stories}");
        await ledger.RecordAsync(item, WorkState.Planned, session, $"{plan.Stories.Count} stories: {stories}", ct);
    }

    /// <summary>
    /// The plan's changeset branch in <paramref name="repo"/> (sc-25872): <c>factory/changeset-&lt;plan item&gt;</c>
    /// (<see cref="ItemNaming.ChangesetBranchName"/>), created from the default branch's head — the ledger first
    /// (<see cref="Steps.Changeset"/>: the branch and that head), then the push, by the orchestrator with the factory App's token
    /// (<see cref="Git.IRepoWorkspace.CreateBranchAsync"/>: no worker, no worktree; under <c>factory/</c> because the rulesets let
    /// that App write nowhere else and only the gate App merges), then <see cref="Steps.ChangesetPushed"/>. Idempotent (E4): a
    /// recorded changeset is pushed again from its recorded commit by a run resumed before the push, a branch origin already has is
    /// adopted as it is (never reset or force-pushed), and one recorded as pushed is left alone. Which repository is a parameter:
    /// a plan has one changeset per repository it changes, and today every story of a plan is in the plan item's repository.
    /// </summary>
    private async Task<Plans.Changeset> EnsureChangesetAsync(Run run, string? session, CancellationToken ct, RepoRef? repo = null)
    {
        var (item, target) = (run.Item, repo ?? run.Repo);
        var history = await ledger.HistoryAsync(item, ct);
        if (Plans.Changeset.Of(history, target) is { } pushed)
        {
            return pushed;
        }
        var branch = Naming.ChangesetBranchName(run.Story.Id);
        var start = await workspaces.BranchStartAsync(target, branch, ct);
        var changeset = history.Where(e => e.Step == Steps.Changeset).Select(e => Plans.Changeset.FromDetail(e.Detail))
            .LastOrDefault(c => c is not null && c.Branch == branch && string.Equals(c.Repo, target.FullName, StringComparison.OrdinalIgnoreCase));
        if (changeset is null)
        {
            changeset = new Plans.Changeset(target.FullName, branch, start.BaseBranch, start.Existing ?? start.BaseSha);
            await ledger.CheckpointAsync(item, Steps.Changeset, session, changeset.ToDetail(), ct);
        }
        var head = await workspaces.CreateBranchAsync(target, branch, changeset.BaseSha, ct);
        await ledger.CheckpointAsync(item, Steps.ChangesetPushed, session, Plans.Changeset.PushedDetail(branch, head, start.Existing is not null), ct);
        log.WriteLine($"[plan] changeset branch {branch} of {target} at {Ci.Short(head)}"
            + (start.Existing is null ? $" (created from {changeset.Base})" : " (adopted: origin already had it)"));
        return changeset;
    }

    /// <summary>
    /// Why a story of a plan may not start yet (sc-25871, E7), or null when it may: the blockers the board relates to it
    /// (<see cref="IWorkSource.BlockersAsync"/>: stories of its plan or not) that are not satisfied
    /// (<see cref="PlanRelease.Satisfied(IReadOnlyList{LedgerEntry}?, bool, bool)"/>: a story of its plan by the factory's merge only,
    /// any other also once the board shows it finished), named in a claim refusal (<see cref="PlanRelease.Refusal"/>). Only a plan's
    /// story (<see cref="WorkItem.PlanItemId"/>) is held to its blockers: a story a person wrote keeps the board's relations as the
    /// owner's own business, as before. Reads only.
    /// </summary>
    private async Task<string?> BlockedAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        if (item.PlanItemId is not { } plan)
        {
            return null;
        }
        // A story of a plan that was stopped (sc-25876) is never started, whoever moves it to ready afterwards.
        if (await ledger.ItemAsync(plan, ct) is { State: WorkState.Cancelled } stopped)
        {
            return PlanRelease.PlanStoppedRefusal(stopped.ExternalId);
        }
        var blockers = await source.BlockersAsync(storyId, ct);
        if (blockers.Count == 0)
        {
            return null;
        }
        var ofPlan = await ledger.PlanStoriesAsync(plan, ct);
        var known = await ledger.ItemsByIdAsync(Naming.Source, blockers.Select(b => Naming.Format(b.Id)).ToList(), ct);
        var open = blockers.Select(b => (Id: Naming.Format(b.Id), b.Finished))
            .Where(b => !PlanRelease.Satisfied(known.TryGetValue(b.Id, out var k) ? k.History : null, ofPlan.Contains(b.Id), b.Finished))
            .Select(b => b.Id).ToList();
        return open.Count == 0 ? null : PlanRelease.Refusal(open, open.Where(b => !ofPlan.Contains(b)).ToList());
    }

    /// <summary>
    /// The control on the plan of a story the ledger does not know yet (sc-25876: released by its plan, not claimed): a Pause or Stop
    /// on the plan item holds its stories too, and for an item the ledger knows <see cref="IControls.EffectiveAsync"/> says so itself
    /// (through <see cref="WorkItem.PlanItemId"/>). Read from the ledger's own record of what the plan created.
    /// </summary>
    private async Task<ControlState> PlanControlAsync(string externalId, CancellationToken ct) =>
        source.PlansItems && await ledger.PlanOfAsync(Naming.Source, externalId, ct) is { } plan && await ledger.ItemAsync(plan, ct) is { } planItem
            ? (await _controls.GetAsync(ControlScope.Item(planItem.ExternalId), ct))?.State ?? ControlState.Running
            : ControlState.Running;

    /// <summary>
    /// For an item parked on a claim refused for a blocker (<see cref="PlanRelease.IsBlockedRefusal"/>): the refusal as it stands
    /// now, or null once every blocker is satisfied (or the item was parked for something else) and the run may go on.
    /// </summary>
    private async Task<string?> StillBlockedAsync(WorkItem item, int storyId, CancellationToken ct) =>
        PlanRelease.IsBlockedRefusal((await ledger.HistoryAsync(item, ct)).LastOrDefault(e => e.Step is null)?.Detail)
            ? await BlockedAsync(item, storyId, ct)
            : null;

    /// <summary>
    /// Why a story that says a plan created it may not start (sc-25871), or null when it may: its description carries the plan marker
    /// (<see cref="WorkStory.PlannerAuthored"/>) but the ledger links it to no plan — no <c>plan-stories</c> row lists it, because its
    /// plan's run failed or died part-way through creating the stories — so its blockers cannot be checked. The marker is text anyone
    /// may write; it is only ever used here to refuse (fail closed), never to grant: a story without it is a hand-written story, held to
    /// nothing. Links the item when the ledger does know its plan. Call under the item's run lock.
    /// </summary>
    private async Task<string?> WithoutPlanAsync(WorkItem item, WorkStory story, CancellationToken ct)
    {
        if (!source.PlansItems || item.PlanItemId is not null || !story.PlannerAuthored)
        {
            return null;
        }
        if (await ledger.PlanOfAsync(Naming.Source, item.ExternalId, ct) is { } plan)
        {
            await ledger.LinkPlanAsync(item, plan, ct);
            return null;
        }
        return PlanRelease.PlanUnfinishedRefusal(PlannedStory.ParentOf(story.Description));
    }

    /// <summary>
    /// For an item parked on <see cref="WithoutPlanAsync"/>'s refusal and still listed ready: the refusal as it stands now (nothing
    /// is written while it holds), or null once its plan has recorded its stories or the marker is gone. Reads only.
    /// </summary>
    private async Task<string?> StillWithoutPlanAsync(WorkItem item, WorkStory story, CancellationToken ct) =>
        source.PlansItems && item.PlanItemId is null && story.PlannerAuthored
        && PlanRelease.IsPlanUnfinishedRefusal((await ledger.HistoryAsync(item, ct)).LastOrDefault(e => e.Step is null)?.Detail)
        && await ledger.PlanOfAsync(Naming.Source, item.ExternalId, ct) is null
            ? PlanRelease.PlanUnfinishedRefusal(PlannedStory.ParentOf(story.Description))
            : null;

    /// <summary>Whether the item is a plan item waiting on its stories: its run needs no worker, so no worker run lock or sandbox.</summary>
    public static async Task<bool> WaitsOnStoriesAsync(WorkLedger ledger, ItemNaming naming, int storyId, CancellationToken ct) =>
        await ledger.StateOfAsync(naming.Source, naming.Format(storyId), ct) == WorkState.Planned;

    /// <summary>
    /// Planned (sc-25871): one evaluation of a plan item waiting on its stories, under its run lock, with no worker. The orchestrator,
    /// not a person, releases the plan's stories: each one whose blockers are all satisfied (<see cref="PlanRelease.Decide"/>) is moved
    /// to the source's ready state (<see cref="IWorkSource.MakeReadyAsync"/>) and recorded (<see cref="Steps.PlanReleased"/>) — the
    /// board write first, then its row: a run that died between the two asks again, and the source writes nothing for a story that
    /// already left the backlog, so nothing is released twice (E4). A released story is then picked up like any ready story.
    /// What the board is asked: a story that has not merged and was not released has its blockers read from the board
    /// (<see cref="IWorkSource.BlockersAsync"/>: the plan's relations and any a person added since) in the evaluation that could
    /// release it — only once every blocker the plan itself gave it (<see cref="PlanRelease.RecordedBlockers"/>, from the <c>plan</c>
    /// checkpoint) has merged; until then it is held by those and the board is not read for it (so removing an in-plan relation on
    /// the board does not release it early: move it to ready by hand, where the claim check reads the board). A released story that
    /// has not merged and that nothing works (the ledger does not know it, or it is Paused) is asked for once per evaluation
    /// (<see cref="IWorkSource.InBacklogAsync"/>): back in the backlog, a person moved it there, and the plan does not release it
    /// again. A control that pauses the plan item (the factory, its epic, the freeze) releases nothing. What holds the plan beyond
    /// its own order — a story held by a blocker outside the plan, an archived story, a released story back in the backlog — is
    /// recorded on the plan item when it changes (<see cref="Steps.PlanHeld"/>), not every poll. Once every story has merged
    /// (<see cref="ReleaseDecision.Complete"/>: into the plan's changeset branch, each by its own gated pull request) the changeset
    /// goes to the default branch by one pull request of the plan item's own (sc-25872, <see cref="ChangesetPullRequestAsync"/>);
    /// a plan with no changeset branch (recorded before there were any) is closed out at once (<see cref="ClosePlanAsync"/>). A story that escalated or was
    /// cancelled never satisfies its dependents: they stay in the backlog and the plan item stays here, saying which story holds it
    /// and, once nothing else of it can move, that it is stuck (sc-25876, <see cref="RecordFailedHoldAsync"/>). A Pause on the plan
    /// item holds its stories too and a Stop stops them with it (<see cref="IControls.EffectiveAsync"/>, <see cref="ItemStopper"/>):
    /// neither reaches this evaluation, which a paused or stopping plan item is not given. A board that cannot be read
    /// or written throws: nothing is recorded, the next run evaluates again, and the intake loop gives up on a plan that keeps
    /// failing (<see cref="GiveUpAsync"/>: one comment and one <c>plan-held</c> row; the item stays Planned).
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
        var ofPlan = stories.ToHashSet(StringComparer.Ordinal);
        bool Merged(string id) => PlanRelease.Satisfied(known.TryGetValue(id, out var k) ? k.History : null);

        // What blocks each story still waiting. The plan's own order first, from the ledger; the board (the plan's relations and
        // any a person added since) only for a story that order no longer holds.
        var own = PlanRelease.RecordedBlockers(history.Take(recorded).LastOrDefault(e => e.Step == Steps.Plan)?.Detail, stories);
        var blockers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var story in stories.Where(s => !Merged(s) && !released.Contains(s)))
        {
            if (own is not null && own[story].Where(b => !Merged(b)).ToList() is { Count: > 0 } unmerged)
            {
                blockers[story] = unmerged;
                continue;
            }
            var onBoard = await source.BlockersAsync(IdOf(story), ct);
            blockers[story] = onBoard.Select(b => Naming.Format(b.Id)).ToList();
            finished.UnionWith(onBoard.Where(b => b.Finished).Select(b => Naming.Format(b.Id)));
        }
        var outside = blockers.Values.SelectMany(b => b).Where(b => !ofPlan.Contains(b)).ToList();
        var outsiders = outside.Count == 0 ? [] : await ledger.ItemsByIdAsync(Naming.Source, outside, ct);
        bool Satisfied(string id) => ofPlan.Contains(id)
            ? Merged(id)
            : PlanRelease.Satisfied(outsiders.TryGetValue(id, out var o) ? o.History : null, ofPlan: false, finished.Contains(id));
        var decision = PlanRelease.Decide(stories, released, blockers, Satisfied);
        // Which stories failed and what they hold (sc-25876), recorded when it changes: apart from the release decision (a failed
        // story has no Merge row, so it already satisfies nothing) and from how a complete plan is finished.
        var status = PlanRelease.Classify(stories, released, s => blockers.TryGetValue(s, out var by) ? by : own?[s] ?? [],
            id => known.TryGetValue(id, out var k) ? k.History : null, Satisfied);
        await RecordFailedHoldAsync(item, storyId, since, status, ct);
        if (decision.Complete)
        {
            return Plans.Changeset.Of(history, RepoRef.Parse(item.Repo)) is { } changeset
                ? await ChangesetPullRequestAsync(item, storyId, changeset, stories, known, history, since, ct)
                : await ClosePlanAsync(item, storyId, stories, known, since, null, ct);
        }

        // A story the plan released that nothing works and that waits in the backlog again: a person moved it back.
        var inFlight = stories.Where(s => released.Contains(s) && !Merged(s)).ToList();
        var back = new List<string>();
        foreach (var story in inFlight.Where(s => !known.TryGetValue(s, out var k) || k.Item.State == WorkState.Paused))
        {
            if (await source.InBacklogAsync(IdOf(story), ct))
            {
                back.Add(story);
            }
        }

        var done = new List<string>();
        var archived = new List<string>();
        foreach (var story in decision.Release)
        {
            // A Pause (the factory's, the epic's, the item's, the freeze) that arrived meanwhile: nothing more is released.
            if (await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) is not ControlState.Running and var control)
            {
                log.WriteLine($"[{item.State}] {item.ExternalId} is {control} by a control; releasing nothing more");
                break;
            }
            var moved = await source.MakeReadyAsync(IdOf(story), ct);
            if (moved == ReadyResult.Archived)
            {
                // Not released, and not recorded as released: it is asked for again every evaluation, so unarchiving it releases it.
                archived.Add(story);
                continue;
            }
            // Released all the same when it is outside the watch scope: the listing will not find it, so say so.
            var listed = ignoreScope || await source.InScopeAsync(IdOf(story), ct);
            await ledger.CheckpointAsync(item, Steps.PlanReleased, null,
                story + (moved == ReadyResult.Moved ? "" : " (it had already left the backlog; left where it is)")
                    + (listed ? "" : $" (outside the watch scope: {source.ScopeReturnHint}, or run `factory run {story} --ignore-scope`)"), ct);
            log.WriteLine($"[{item.State}] {item.ExternalId}: released {story}{(listed ? "" : " (outside the watch scope: nothing will pick it up)")}");
            done.Add(story);
        }
        string[] holds = [PlanRelease.HeldByOutsiders(decision.Held), .. archived.Select(PlanRelease.HeldArchived), .. back.Select(PlanRelease.HeldBackInBacklog)];
        var held = string.Join("; ", holds.Where(h => h.Length > 0));
        if (held.Length > 0 && held != since.LastOrDefault(e => e.Step == Steps.PlanHeld
            && e.Detail?.StartsWith(PlanRelease.NotEvaluatedPrefix, StringComparison.Ordinal) != true
            && e.Detail?.StartsWith(PlanRelease.FailedPrefix, StringComparison.Ordinal) != true)?.Detail)
        {
            await ledger.CheckpointAsync(item, Steps.PlanHeld, null, held, ct);
        }
        var waiting = status.Failed.Select(f => $"{f.Id} {f.Failure!.As}")
            .Concat(decision.Held.Select(h => $"{h.Id} on {string.Join(", ", h.By)}"))
            .Concat(archived.Select(s => $"{s} archived"))
            .Concat(inFlight.Select(s => back.Contains(s) ? $"{s} released, back in the backlog" : $"{s} released, not merged")).ToList();
        log.WriteLine($"[{item.State}] {item.ExternalId}: {stories.Count(Merged)} of {stories.Count} stories merged"
            + (done.Count == 0 ? "" : $"; released {string.Join(", ", done)}")
            + (waiting.Count == 0 ? "" : $"; waiting: {string.Join("; ", waiting)}"));
        return await OutcomeAsync(item, null, ct);

        int IdOf(string story) => Naming.TryParse(story, out var id)
            ? id
            : throw new InvalidOperationException($"{item.ExternalId}'s plan records '{story}', which is not a {Naming.Noun} id.");
    }

    /// <summary>Whether a row of a waiting plan item records a failed story's hold on it, or the lifting of one (sc-25876).</summary>
    internal static bool IsFailedHoldRow(LedgerEntry row) =>
        row.Step is Steps.PlanStuck or Steps.PlanResumed
        || (row.Step == Steps.PlanHeld && row.Detail?.StartsWith(PlanRelease.FailedPrefix, StringComparison.Ordinal) == true);

    /// <summary>
    /// Held by a failed story (sc-25876): a story of the plan whose item ended Escalated or Cancelled (<see cref="PlanRelease.FailureOf"/>,
    /// a pull request merged outside the factory among them) satisfies no blocker, so what depends on it is never released while it
    /// stays so; the rest of the plan goes on. The plan item says so — a <see cref="Steps.PlanHeld"/> row naming each failed story, how
    /// it failed and its reason, or <see cref="Steps.PlanStuck"/> once every story left is failed or blocked behind one — only when
    /// that changes (<see cref="PlanRelease.HeldByFailed"/>), never every poll, and it never escalates for it: the story's own
    /// Escalated row is the failure. Once no story is failed any more (a person re-ran it) a <see cref="Steps.PlanResumed"/> row lifts
    /// it. Each such row gets one comment on the plan item, after the row (E4): its result is a <see cref="Steps.PlanNotice"/> row, a
    /// failed one is tried again by the next evaluations up to <see cref="MaxPlanNoticeAttempts"/>, and a posted one is never repeated.
    /// </summary>
    private async Task RecordFailedHoldAsync(WorkItem item, int storyId, List<LedgerEntry> since, PlanStatus status, CancellationToken ct)
    {
        var at = since.FindLastIndex(IsFailedHoldRow);
        var last = at < 0 ? null : since[at];
        var (step, detail) = !status.Held ? (Steps.PlanResumed, PlanRelease.Resumed(status))
            : status.Stuck ? (Steps.PlanStuck, PlanRelease.HeldByFailed(status))
            : (Steps.PlanHeld, PlanRelease.FailedPrefix + PlanRelease.HeldByFailed(status));
        // A lifted hold is said once, whatever the plan's counts do afterwards; a hold whenever its record differs.
        var changed = status.Held ? last?.Step != step || last.Detail != detail : last is { Step: not Steps.PlanResumed };
        var notices = at < 0 || changed ? [] : since.Skip(at + 1).Where(e => e.Step == Steps.PlanNotice).ToList();
        if (changed)
        {
            await ledger.CheckpointAsync(item, step, null, detail, ct);
            log.WriteLine($"[{item.State}] {item.ExternalId}: {(status.Held ? status.Stuck ? "stuck" : "held by a failed story" : "no longer held")}: {detail}");
        }
        else if (last is null || notices.Any(n => n.Detail == Posted) || notices.Count >= MaxPlanNoticeAttempts)
        {
            return;
        }
        string result;
        try
        {
            await source.CommentAsync(storyId,
                status.Held ? PlanRelease.HeldComment(item.ExternalId, status) : PlanRelease.ResumedComment(item.ExternalId, status), ct);
            result = Posted;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            result = $"failed: {ex.Message}";
            log.WriteLine($"[{item.State}] could not comment on {item.ExternalId}: {ex.Message}; "
                + (notices.Count + 1 < MaxPlanNoticeAttempts ? "the next evaluation tries again" : "not tried again: the ledger has it and the dashboard's plan row says so"));
        }
        await ledger.CheckpointAsync(item, Steps.PlanNotice, null, result, ct);
    }

    /// <summary>
    /// Every story of the plan has merged into its changeset branch (sc-25872): the plan item itself takes the changeset to the
    /// default branch. One pull request from the changeset branch into the default branch is opened (or the open one found:
    /// <see cref="IPullRequests.OpenAsync"/> never opens a second for a branch), linked on the plan item (<see cref="Steps.Linked"/>)
    /// and the item goes Planned → Review with it as its pull request — from there the existing handlers drive it exactly like any
    /// item's (the full review panel on the whole diff, CI, the merge gate and the merge queue's turn against the default branch,
    /// fix rounds pushing to the changeset branch, escalation, Stop), so the changeset is reviewed and gated once more as a whole
    /// and no merge has a second, weaker path (E6). The plan item is reported merged and closed out only by that pull request's
    /// Merge (<see cref="MergeAsync"/>). An empty changeset — the branch does not differ from the default branch (GitHub's compare,
    /// read now) — gets no pull request: recorded (<see cref="Steps.ChangesetEmpty"/>), and the plan is closed out saying so. A
    /// control that holds the plan item opens nothing. Needs no worker: it runs in the waiting plan's run.
    /// </summary>
    private async Task<RunOutcome> ChangesetPullRequestAsync(WorkItem item, int storyId, Plans.Changeset changeset, IReadOnlyList<string> stories,
        Dictionary<string, (WorkItem Item, List<LedgerEntry> History)> known, List<LedgerEntry> history, List<LedgerEntry> since, CancellationToken ct)
    {
        var repo = RepoRef.Parse(changeset.Repo);
        var empty = since.LastOrDefault(e => e.Step == Steps.ChangesetEmpty)?.Detail;
        // A changeset pull request of this plan that someone merged by hand (an escalated one, say): the changeset is delivered.
        // GitHub's three-dot compare below would still show a difference after a squash or rebase merge, and every run would open
        // another pull request for changes the default branch already has.
        if (empty is null && gate is not null && since.LastOrDefault(e => e.Step == Steps.Linked)?.Detail?.Split(' ')[0] is { } linked
            && (await Gate.GitHub.GetPullAsync(repo, GitHubGate.ParsePullUrl(linked).Number, ct)).Merged)
        {
            empty = Plans.Changeset.MergedOutside(linked);
            await ledger.CheckpointAsync(item, Steps.ChangesetEmpty, null, empty, ct);
        }
        // Without a gate stage nothing can read the difference (nor review the pull request: the item parks at Review).
        if (empty is null && gate is not null && string.IsNullOrWhiteSpace(await Gate.GitHub.GetDiffAsync(repo, changeset.Base, changeset.Branch, ct)))
        {
            empty = $"{changeset.Branch} does not differ from {changeset.Base}: no changeset pull request";
            await ledger.CheckpointAsync(item, Steps.ChangesetEmpty, null, empty, ct);
        }
        if (empty is not null)
        {
            return await ClosePlanAsync(item, storyId, stories, known, since, empty, ct);
        }
        if (await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) is not ControlState.Running and var control)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is {control} by a control; its changeset pull request is not opened");
            return await OutcomeAsync(item, null, ct);
        }
        var prUrl = await pullRequests.OpenAsync(repo, changeset.Branch, changeset.Base, 
            $"{item.ExternalId}: {UntrustedText.Inert(item.Title)} (changeset of {stories.Count} stories)",
            LedgerReport.ChangesetPullRequestBody(item.ExternalId, null, changeset, PlanStoryReports(stories, known), history,
                await ledger.SessionCostsAsync(item, ct)), ct);
        var links = $"{prUrl} {BranchUrl(repo, changeset.Branch)}";
        if (!since.Any(e => e.Step == Steps.Linked && e.Detail == links))
        {
            await source.LinkAsync(storyId, links.Split(' '), ct);
            await ledger.CheckpointAsync(item, Steps.Linked, null, links, ct);
        }
        await ledger.RecordAsync(item, WorkState.Review, null, prUrl, ct);
        log.WriteLine($"[{item.State}] {item.ExternalId}: every story of the plan merged into {changeset.Branch}; its changeset pull request is {prUrl}");
        return await OutcomeAsync(item, null, ct);
    }

    private static IReadOnlyList<PlanStoryReport> PlanStoryReports(IReadOnlyList<string> stories,
        Dictionary<string, (WorkItem Item, List<LedgerEntry> History)> known) =>
        [.. stories.Select(s => known.TryGetValue(s, out var k) ? new PlanStoryReport(s, k.Item.Title, k.History) : new PlanStoryReport(s, "", []))];

    /// <summary>The stories of the plan item whose rows are <paramref name="history"/>, each with its own ledger rows; none when it is no plan item.</summary>
    private async Task<IReadOnlyList<PlanStoryReport>> PlanStoryReportsAsync(List<LedgerEntry> history, CancellationToken ct)
    {
        var stories = PlanRelease.StoriesOf(history.LastOrDefault(e => e.Step == Steps.PlanStories)?.Detail);
        return stories.Count == 0 ? [] : PlanStoryReports(stories, await ledger.ItemsByIdAsync(Naming.Source, stories, ct));
    }

    /// <summary>
    /// What the review panel judges the item against: its story — and, for a plan item's changeset pull request (sc-25872), its
    /// story followed by the plan's story list, fenced as a planning model's text (<see cref="Plans.Changeset.ReviewSpec"/>, E3).
    /// </summary>
    private async Task<WorkStory> ReviewStoryAsync(Run run, CancellationToken ct)
    {
        if (run.Changeset is null)
        {
            return run.Story;
        }
        var history = await ledger.HistoryAsync(run.Item, ct);
        var recorded = history.FindLastIndex(e => e.Step == Steps.PlanStories);
        return recorded < 0 ? run.Story : run.Story with
        {
            Description = Plans.Changeset.ReviewSpec(run.Story.Description, history.Take(recorded).LastOrDefault(e => e.Step == Steps.Plan)?.Detail,
                PlanRelease.StoriesOf(history[recorded].Detail)),
        };
    }

    /// <summary>
    /// Every story of the plan has merged and there is no changeset to merge — the plan has no changeset branch, or its changeset is
    /// empty (<paramref name="note"/> says so in the closeout) (sc-25871): the plan item is reported merged on its board (<see cref="Steps.MergedReported"/>),
    /// gets a closeout rendered from the ledger (<see cref="LedgerReport.PlanCloseout"/>: each story, its pull request, its outcome;
    /// <see cref="Steps.Closeout"/>) and goes to Done, each step recorded before the next so a resumed run redoes none. A closeout the
    /// board refuses is recorded <c>failed: …</c> and tried again by the next runs, like a merged item's, up to
    /// <see cref="MaxCloseoutAttempts"/>. After the last the item stays Planned, as a merged item stays in Watch: no poll lists it
    /// any more (<see cref="PlanCloseoutAbandoned"/>), nothing is written, and the dashboard keeps saying the closeout is left to post
    /// by hand. It reaches Done only by a posted closeout.
    /// </summary>
    private async Task<RunOutcome> ClosePlanAsync(WorkItem item, int storyId, IReadOnlyList<string> stories,
        Dictionary<string, (WorkItem Item, List<LedgerEntry> History)> known, List<LedgerEntry> since, string? note, CancellationToken ct)
    {
        if (!since.Any(e => e.Step == Steps.MergedReported))
        {
            await source.ReportStateAsync(storyId, BoardState.Merged, null, ct);
            await ledger.CheckpointAsync(item, Steps.MergedReported, null, $"{stories.Count} stories merged", ct);
        }
        var attempts = since.Where(e => e.Step == Steps.Closeout).ToList();
        var posted = attempts.Any(a => a.Detail == Posted);
        var tried = attempts.Count;
        var error = attempts.LastOrDefault()?.Detail;
        if (!posted && tried < MaxCloseoutAttempts)
        {
            try
            {
                await source.CommentAsync(storyId, LedgerReport.PlanCloseout(item.ExternalId,
                    [.. stories.Select(s => new PlanStoryReport(s, known[s].Item.Title, known[s].History))], note), ct);
                posted = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = $"failed: {ex.Message}";
                log.WriteLine($"[{item.State}] could not post the plan closeout on {item.ExternalId}: {ex.Message}");
            }
            await ledger.CheckpointAsync(item, Steps.Closeout, null, posted ? Posted : error, ct);
            tried++;
        }
        if (!posted)
        {
            return await OutcomeAsync(item, $"{item.ExternalId}: closeout NOT posted ({error}); "
                + (tried < MaxCloseoutAttempts ? "the next run tries again" : $"{tried} attempts made: post it by hand"), ct);
        }
        await ledger.RecordAsync(item, WorkState.Done, null, $"{stories.Count} stories merged", ct);
        log.WriteLine($"[{item.State}] {item.ExternalId}: every story of the plan merged; the plan is closed out");
        return await OutcomeAsync(item, null, ct);
    }

    /// <summary>
    /// Whether a waiting plan item's closeout failed <see cref="MaxCloseoutAttempts"/> times (<paramref name="rows"/>: its
    /// <see cref="Steps.Closeout"/> rows; a plan item has no Merge row): every story merged and the board was told, only the closeout
    /// is left, to a person. No poll lists it (<see cref="InFlightAsync"/>).
    /// </summary>
    public static bool PlanCloseoutAbandoned(WorkState state, IEnumerable<LedgerEntry> rows) =>
        state == WorkState.Planned && CloseoutOf(rows) is { Posted: false, Attempts: >= MaxCloseoutAttempts };

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
        // Before the seat too: a plan naming a repository the factory is not configured for starts no session (sc-25874).
        var repos = PlanRepositories(run);
        return await planner.RunAsync(run.Item, repos, async (seat, c) =>
        {
            try
            {
                var (session, result) = await RunSessionAsync(run, seat.Worker, seat.Path, seat.McpConfig, implementing: false, null,
                    _ => PlanPrompt.Build(run.Spec, run.Repo, seat.Checkouts) + PlanPrompt.McpNote(seat.ToolRules, repos), [], [Taint.Planner], "plan",
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

    /// <summary>
    /// The planner's repository set (sc-25874): every <c>Repo: owner/name</c> line of the plan item (<see cref="RepoResolver.ResolveAll"/>),
    /// the item's primary repository (<see cref="Run.Repo"/>, what the rest of the pipeline uses) first. With one line or none it is
    /// that repository alone, as before. A set of several is what the planner's checkouts and its CodeGraph grant cover, so each
    /// must be one of the factory's configured repositories: one that is not fails the item before any session starts
    /// (<see cref="WorkerFailedException"/>: escalated with the reason, nothing created).
    /// </summary>
    private IReadOnlyList<RepoRef> PlanRepositories(Run run)
    {
        var repos = RepoResolver.ResolveAll(run.Story.Description, run.Repo);
        if (repos.Count > 1)
        {
            var unconfigured = repos.Where(r => !_configuredRepos.Any(c => string.Equals(c.FullName, r.FullName, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.FullName).ToList();
            if (unconfigured.Count > 0)
            {
                throw new WorkerFailedException($"The plan names {repos.Count} repositories ({string.Join(", ", repos.Select(r => r.FullName))}), "
                    + $"but {string.Join(", ", unconfigured)} {(unconfigured.Count == 1 ? "is" : "are")} not configured for this factory "
                    + $"(Factory:DefaultRepo, GitHub:Watch:Repos: {string.Join(", ", _configuredRepos.Select(r => r.FullName))}), so no planner "
                    + "session was started and no story was created. Configure it, or remove its Repo: line.");
            }
        }
        return repos;
    }
}
