using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator;

public sealed partial class RunPipeline
{
    /// <summary>
    /// Plan (sc-25873): stops a plan whose stories' CI could never report in <paramref name="repo"/>, before anything is spent on
    /// it. The stories' pull requests target the plan's changeset branch; a repository whose workflows all filter that base out
    /// reports no check for them, and each story would wait out <c>Gate:CiTimeoutMinutes</c> and escalate. The orchestrator reads
    /// the default branch's workflow files owner-side with the gate App's read-only token (<see cref="GitHub.IGateGitHub.GetWorkflowsAsync"/>)
    /// and <see cref="CiTriggers.Check"/> decides, per repository (a plan with stories in several calls this for each):
    /// <list type="bullet">
    /// <item>certain that no workflow can run → a <see cref="Steps.CiTriggers"/> row (<c>blocked: …</c>) and the plan item escalates
    /// with the repository, the workflow files read and the fix; no planner session, story or changeset branch comes after it;</item>
    /// <item>it cannot be told (nothing to read, an unreadable or unparsable workflow, GitHub not answering) → a
    /// <see cref="Steps.CiTriggers"/> row (<c>warning: …</c>, written when it changes) and a log line, and planning goes on: the gate
    /// still needs green CI on every head, so nothing is weakened either way;</item>
    /// <item>a workflow runs → a log line only.</item>
    /// </list>
    /// Not checked once the repository's changeset branch is on origin (the plan's stories are already running there), nor in a
    /// pipeline with no gate stage (it has no GitHub read client and merges nothing).
    /// </summary>
    private async Task EnsureCiTriggersAsync(Run run, RepoRef repo, CancellationToken ct)
    {
        if (gate is null)
        {
            return;
        }
        var item = run.Item;
        var history = await ledger.HistoryAsync(item, ct);
        if (Plans.Changeset.Of(history, repo) is not null)
        {
            return;
        }
        var (changeset, story) = (Naming.ChangesetBranchName(run.Story.Id), Naming.BranchName(run.Story.Id));
        CiTriggerCheck check;
        try
        {
            check = CiTriggers.Check(await gate.GitHub.GetWorkflowsAsync(repo, ct), changeset, story);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var error = ex.Message.Length > CiTriggers.MaxErrorChars ? ex.Message[..CiTriggers.MaxErrorChars] : ex.Message;
            check = new CiTriggerCheck(CiTriggerOutcome.Unknown,
                $"whether any CI workflow runs for a pull request into {changeset} cannot be told: the workflows could not be read ({error})");
        }
        check = check with { Reason = $"{repo}: {check.Reason}" };
        if (check.Outcome == CiTriggerOutcome.Runs)
        {
            log.WriteLine($"[plan] CI triggers of {check.Reason}");
            return;
        }
        var detail = check.ToDetail();
        if (history.LastOrDefault(e => e.Step == Steps.CiTriggers)?.Detail != detail)
        {
            await ledger.CheckpointAsync(item, Steps.CiTriggers, null, detail, ct);
        }
        if (check.Outcome == CiTriggerOutcome.Unknown)
        {
            log.WriteLine($"[plan] warning: CI triggers of {check.Reason}; planning goes on");
            return;
        }
        throw new WorkerFailedException($"The plan was stopped before it was planned or any story of it created: in {check.Reason}. "
            + $"Every story of the plan would open its pull request against {changeset}, get no CI check, wait out Gate:CiTimeoutMinutes and "
            + "escalate. Fix the workflow on the default branch, then run the plan item again.");
    }
}
