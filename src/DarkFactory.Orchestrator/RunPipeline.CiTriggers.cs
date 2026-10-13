using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator;

public sealed partial class RunPipeline
{
    /// <summary>
    /// Plan (sc-25873): stops a plan whose CI could never report in <paramref name="repo"/>, before anything is spent on it. The
    /// orchestrator reads the default branch's workflow files, and the default branch's name, owner-side with the gate App's
    /// read-only token (<see cref="GitHub.IGateGitHub.GetWorkflowsAsync"/>) and <see cref="CiTriggers.Check"/> answers for both
    /// kinds of pull request the plan opens there:
    /// <list type="bullet">
    /// <item>a story's (base: the plan's changeset branch, head: the story's own branch) — a repository whose workflows all filter
    /// that base out reports no check for them, and each story would wait out <c>Gate:CiTimeoutMinutes</c> and escalate;</item>
    /// <item>the changeset's (base: the default branch as GitHub names it, head: the changeset branch) — a repository whose only
    /// trigger is, say, <c>pull_request: branches: ['factory/**']</c> runs for every story and then never for the pull request that
    /// delivers the plan, which would time out after every story was built.</item>
    /// </list>
    /// It takes the repository as a parameter: <see cref="PlanAsync"/> calls it for every repository the plan item names
    /// (<see cref="PlanRepositories"/>, the planner's set — one call for a plan of one repository), in that order, before the planner
    /// session, so the first repository that blocks stops the plan with nothing spent in any of them. Every reason starts with the
    /// repository it is about, and a repository's row is compared with that repository's last row only. What follows from the two
    /// answers, the stories' first:
    /// <list type="bullet">
    /// <item>certain that no workflow can run for one of them → a <see cref="Steps.CiTriggers"/> row (<c>blocked: …</c>) and the plan
    /// item escalates with the repository, the fix and the workflow files read; no planner session, story or changeset branch comes
    /// after it;</item>
    /// <item>else it cannot be told for one of them (nothing to read, an unreadable or unparsable workflow, no workflow with a pull
    /// request or push trigger for branches, GitHub not answering) → a <see cref="Steps.CiTriggers"/> row (<c>warning: …</c>,
    /// written when it changes) and a log line, and planning goes on: the gate still needs green CI on every head, so nothing is
    /// weakened either way;</item>
    /// <item>a workflow runs for both → log lines only.</item>
    /// </list>
    /// Not checked once the repository's changeset branch is on origin (the plan's stories are already running there; per
    /// repository, so a run resumed between two repositories' branches still checks the one without), nor in a
    /// pipeline with no gate stage (it has no GitHub read client and merges nothing).
    /// </summary>
    /// <param name="progress">What an earlier run of this plan left: a stop says that, never that nothing exists when stories do.</param>
    /// <param name="several">The plan item names several repositories: a stop names the way out for one the plan should not touch.</param>
    /// <param name="hasStory">Whether the recorded plan this run continues has a story in <paramref name="repo"/>; null when no plan is known yet.</param>
    private async Task EnsureCiTriggersAsync(Run run, RepoRef repo, PlanProgress progress, bool several, bool? hasStory, CancellationToken ct)
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
        // What each pull request of the plan would cost when it gets no check, in the words of the stop.
        var stories = $"Every story of the plan would open its pull request against {changeset}, get no CI check, wait out Gate:CiTimeoutMinutes and escalate.";
        (CiTriggerCheck Check, string Wasted)[] checks;
        try
        {
            var workflows = await gate.GitHub.GetWorkflowsAsync(repo, ct);
            checks =
            [
                (CiTriggers.Check(workflows.Files, changeset, story), stories),
                (CiTriggers.Check(workflows.Files, workflows.DefaultBranch, changeset),
                    $"The plan's stories would be built and merged into {changeset}, and then the changeset pull request that takes them to the "
                    + "default branch would get no CI check, wait out Gate:CiTimeoutMinutes and escalate: the plan could never be delivered."),
            ];
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var error = ex.Message.Length > CiTriggers.MaxErrorChars ? ex.Message[..CiTriggers.MaxErrorChars] : ex.Message;
            checks = [(new CiTriggerCheck(CiTriggerOutcome.Unknown,
                $"whether any CI workflow runs for a pull request into {changeset} cannot be told: the workflows could not be read ({error})"), stories)];
        }
        checks = [.. checks.Select(c => (c.Check with { Reason = $"{repo}: {c.Check.Reason}" }, c.Wasted))];
        // A certain "never" for either pull request stops the plan; else a "cannot tell" for either is the warning.
        var found = checks.Where(c => c.Check.Outcome == CiTriggerOutcome.Never)
            .Concat(checks.Where(c => c.Check.Outcome == CiTriggerOutcome.Unknown)).ToList();
        if (found.Count == 0)
        {
            foreach (var (runs, _) in checks)
            {
                log.WriteLine($"[plan] CI triggers of {runs.Reason}");
            }
            return;
        }
        var (check, wasted) = found[0];
        var detail = check.ToDetail();
        // This repository's last row: another repository's warning in between is not a change of this one's.
        if (history.LastOrDefault(e => e.Step == Steps.CiTriggers && CiTriggerCheck.IsAbout(e.Detail, repo.FullName))?.Detail != detail)
        {
            await ledger.CheckpointAsync(item, Steps.CiTriggers, null, detail, ct);
        }
        if (check.Outcome == CiTriggerOutcome.Unknown)
        {
            log.WriteLine($"[plan] warning: CI triggers of {check.Reason}; planning goes on");
            return;
        }
        // What the stop left, from the ledger: a plan this run continues may already have stories on the board.
        var stopped = progress switch
        {
            PlanProgress.Fresh => "The plan was stopped before it was planned or any story of it created",
            PlanProgress.Recorded => "The plan an earlier run recorded was stopped before any story of it was created",
            _ => "The plan was stopped before its stories were recorded as its own",
        };
        // Every repository the plan item names is checked, before the planner says which of them get a story: one the plan should
        // not change at all blocks it too, and the way out of that is the plan item's own `Repo:` line.
        var fix = !several ? "Fix the workflow on the default branch, then run the plan item again."
            : hasStory switch
            {
                true => $"Fix the workflow on the default branch of {repo}, then run the plan item again (the recorded plan has a story in {repo}).",
                false => $"The recorded plan has no story in {repo}: remove its `Repo:` line from the plan item if the plan should not touch it, "
                    + $"or fix the workflow on the default branch of {repo}; then run the plan item again.",
                null => $"The plan item names {repo}, so it is checked before the planner runs, whether or not the plan would give it a story: "
                    + $"remove its `Repo:` line from the plan item if the plan should not touch it, or fix the workflow on the default branch of {repo}; "
                    + "then run the plan item again.",
            };
        throw new WorkerFailedException($"{stopped}: in {check.Reason}. {wasted} "
            + (progress == PlanProgress.StoriesMayExist ? $"{StoriesMayExist} " : "") + fix);
    }
}
