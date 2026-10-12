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
    /// created) and their ids recorded (<see cref="Steps.PlanStories"/>), and the item moves to <see cref="WorkState.Planned"/>, where
    /// it waits on them. Which plan a later run uses: <see cref="ReusesRecordedPlan"/>.
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
