using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator;

public sealed partial class RunPipeline
{
    /// <summary>
    /// Plan (sc-25870): a plan item is planned into stories, each step a ledger row before the next (E4). One read-only planner
    /// session proposes a plan (<see cref="PlanSessionAsync"/>); the code checks it (<see cref="PlanParser"/>, E1) — an answer that
    /// breaks a rule is recorded (<see cref="Steps.PlanRejected"/>) and escalates the item with no story created; a valid one is
    /// recorded (<see cref="Steps.Plan"/>), then its stories are created on the board through the work source
    /// (<see cref="WorkSources.IWorkSource.CreateChildrenAsync"/>, idempotent: a retry reuses what an earlier call created) and their ids
    /// recorded (<see cref="Steps.PlanStories"/>), and the item moves to <see cref="WorkState.Planned"/>, where it waits on them.
    /// A recorded plan is never planned again: a run that resumes after a crash, a pause or an escalation (the board was down) creates
    /// the stories of that plan, so no second plan's stories land beside the first's.
    /// </summary>
    private async Task PlanAsync(Run run, CancellationToken ct)
    {
        var item = run.Item;
        var history = await ledger.HistoryAsync(item, ct);
        var planned = history.FindLastIndex(e => e.Step == Steps.Plan);
        var created = history.FindLastIndex(e => e.Step == Steps.PlanStories);
        Plan plan;
        string? session;
        if (planned >= 0)
        {
            (plan, session) = (Plan.FromDetail(history[planned].Detail), history[planned].ClaudeSessionId);
            log.WriteLine($"[plan] continuing with the plan recorded at {history[planned].RecordedAt:u}");
        }
        else
        {
            (var answer, session) = await PlanSessionAsync(run, ct);
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

        string stories;
        if (created > planned)
        {
            stories = history[created].Detail ?? "";
        }
        else
        {
            var ids = await source.CreateChildrenAsync(run.Story.Id, plan.Children(run.Story.Ref, run.Repo), ct);
            if (ids.Count != plan.Stories.Count)
            {
                throw new InvalidOperationException($"The board returned {ids.Count} stories for a plan of {plan.Stories.Count}.");
            }
            stories = string.Join(' ', ids.Select(Naming.Format));
            await ledger.CheckpointAsync(item, Steps.PlanStories, session, stories, ct);
            log.WriteLine($"[plan] stories on the board: {stories}");
        }
        await ledger.RecordAsync(item, WorkState.Planned, session, $"{plan.Stories.Count} stories: {stories}", ct);
    }

    /// <summary>
    /// One planner session (E2), always a fresh one (a planner session is never resumed: its checkout is throwaway and it has written
    /// nothing): a read-only worker on <see cref="WorkerModelClass.Planner"/> in the seat the <see cref="IPlanRunner"/> gives it, run
    /// like any worker session of the item (<see cref="RunSessionAsync"/>) — pid, session id and model class checkpointed, every line
    /// stored, Pause/Stop and the freeze watched, a usage-limited or class-refused session pausing the factory instead of escalating —
    /// and tainted from its start (<see cref="Taint.Planner"/>), so nothing it did can ever be pushed. Returns its answer and its id.
    /// </summary>
    private async Task<(string? Answer, string? Session)> PlanSessionAsync(Run run, CancellationToken ct)
    {
        if (planner is null)
        {
            throw new InvalidOperationException($"{run.Item.ExternalId} is a plan item, but this pipeline has no planner.");
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
