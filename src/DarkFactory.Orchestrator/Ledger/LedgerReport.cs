using System.Globalization;
using System.Text;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// What the factory writes about an item on its PR and its board (E5, sc-25389), rendered only from the item's ledger rows and its worker
/// sessions' recorded costs: the checks run (each <c>gate</c>, <c>ci-failure</c> and <c>new-tests</c> row), the review verdicts (each
/// <c>verdict</c> row), the fix rounds and the cost — never from a worker's summary. Every row gets its typed outcome
/// (<see cref="StepOutcomes"/>). Text a row holds that a model, a repository or an issue wrote (finding titles, check and test names, gate
/// reasons, model ids) goes through <see cref="UntrustedText"/>; what is not measured says N/A.
/// </summary>
public static class LedgerReport
{
    /// <summary>The steps reported as checks run.</summary>
    public static readonly IReadOnlySet<string> CheckSteps =
        new HashSet<string> { RunPipeline.Steps.GateDecision, RunPipeline.Steps.CiFailure, RunPipeline.Steps.NewTests };

    private const int MaxFragment = 400;

    /// <summary>
    /// The PR's description: the item it implements (and, for an issue, GitHub's closing keyword), then the ledger's facts. Rendered when the
    /// PR is opened and again as the item moves on (at merge).
    /// </summary>
    public static string PullRequestBody(WorkStory story, IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Implements {story.Kind.Noun} [{story.Ref}]({story.AppUrl}): {(story.UntrustedName ? UntrustedText.CodeSpan(story.Name) : story.Name)}\n");
        if (story.Closes is { } closes)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nCloses {closes}\n");
        }
        text.Append("\nOpened by Dark Factory.\n\n");
        text.Append(Facts(history, sessionCosts));
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>The board comment that closes an item out once it merged: the merge commit and the ledger's facts.</summary>
    public static string MergedCloseout(string externalId, IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts)
    {
        var merged = history.LastOrDefault(e => e.Step is null && e.State == WorkState.Merge)?.Detail;
        return $"{WorkSourceComments.Author} {externalId} merged{(merged is null ? "" : $" as {Code(merged)}")} by the factory's merge gate.\n\n"
            + Facts(history, sessionCosts).TrimEnd();
    }

    /// <summary>
    /// The ledger's facts about the item as Markdown: its state, its worker sessions, every check row, every verdict row, the fix rounds and
    /// the cost.
    /// </summary>
    public static string Facts(IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts)
    {
        var text = new StringBuilder();
        text.Append("**From the factory ledger** (results the orchestrator executed and recorded, never a worker's own report):\n\n");
        if (history.LastOrDefault(e => e.Step is null) is { } state)
        {
            text.Append(CultureInfo.InvariantCulture, $"- State: {state.State} ({StepOutcomes.Name(state.Outcome)}, {state.RecordedAt:u})\n");
        }
        var sessions = history.Where(e => e.Step == RunPipeline.Steps.Session && e.ClaudeSessionId is not null).Select(e => e.ClaudeSessionId!)
            .Distinct(StringComparer.Ordinal).ToList();
        text.Append(CultureInfo.InvariantCulture,
            $"- Worker sessions: {(sessions.Count == 0 ? "none" : string.Join(", ", sessions.Select(Code)))}\n");

        text.Append("\nChecks run:\n");
        var checks = history.Where(e => e.Step is { } step && CheckSteps.Contains(step)).ToList();
        if (checks.Count == 0)
        {
            text.Append("- none recorded\n");
        }
        foreach (var row in checks)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {Outcome(row)} {Check(row)}\n");
        }

        text.Append("\nReview verdicts:\n");
        var verdicts = history.Where(e => e.Step == RunPipeline.Steps.Verdict).ToList();
        if (verdicts.Count == 0)
        {
            text.Append("- none recorded\n");
        }
        foreach (var row in verdicts)
        {
            text.Append(Verdict(row));
        }

        text.Append('\n').Append(Rounds(history));
        text.Append(CultureInfo.InvariantCulture, $"\nWorker cost: {Cost(sessionCosts)}\n");
        return text.ToString();
    }

    /// <summary>The summed router cost of the item's worker sessions, or N/A when none is recorded (never $0 for unmeasured, E5).</summary>
    public static string Cost(IReadOnlyList<decimal?> sessionCosts)
    {
        var measured = sessionCosts.OfType<decimal>().ToList();
        return measured.Count == 0
            ? $"N/A (no worker session cost recorded{(sessionCosts.Count == 0 ? "" : $"; {sessionCosts.Count} session(s) unmeasured")})"
            : string.Create(CultureInfo.InvariantCulture,
                $"${measured.Sum():0.00##} ({measured.Count} of {sessionCosts.Count} worker session(s) measured; review calls not included)");
    }

    private static string Outcome(LedgerEntry row) => $"**{StepOutcomes.Name(row.Outcome)}**";

    private static string Check(LedgerEntry row)
    {
        switch (row.Step)
        {
            case RunPipeline.Steps.GateDecision:
                return $"merge gate: {Code(row.Detail ?? "")}";
            case RunPipeline.Steps.CiFailure when CiTriage.FromDetail(row.Detail) is { } triage:
                return $"CI on {Code(Ci.Short(triage.HeadSha))} failed: the PR's {List(triage.Fixable)}; not the PR's {List(triage.NotThePrs)}";
            case RunPipeline.Steps.NewTests when NewTestsResult.FromDetail(row.Detail) is { } tests:
                return $"{GateChecks.NewTestsFailOnBase} on {Code(Ci.Short(tests.BaseSha))}...{Code(Ci.Short(tests.HeadSha))}: "
                    + $"{Code(tests.Outcome)}, {Code(tests.Reason)} ({tests.Tests.Count} new test(s))";
            default:
                return $"{row.Step}: {Code(row.Detail ?? "")} (unreadable)";
        }
    }

    private static string Verdict(LedgerEntry row)
    {
        if (ReviewVerdict.FromDetail(row.Detail) is not { } verdict)
        {
            return $"- {Outcome(row)} an unreadable verdict\n";
        }
        var text = new StringBuilder();
        var findings = verdict.Reviews.SelectMany(r => r.Findings.Select(f => (r.Role, Finding: f))).ToList();
        var blocking = findings.Where(f => f.Finding.IsBlocking).ToList();
        text.Append(CultureInfo.InvariantCulture, $"- {Outcome(row)} {Code(verdict.Verdict)} on {Code(Ci.Short(verdict.HeadSha))} by ");
        text.Append(string.Join(", ", verdict.Reviews.Select(r =>
            $"{r.Role} ({Code(r.ServedModel ?? r.Model)}{(r.CarriedFrom is { } from ? $", carried from {Code(Ci.Short(from))}" : "")}"
            + $"{(r.Error is null ? "" : ", unusable answer")})")));
        text.Append(CultureInfo.InvariantCulture,
            $"; {blocking.Count} blocking, {findings.Count - blocking.Count} optional ({findings.Count(f => f.Finding.Downgraded)} downgraded by the second model)\n");
        foreach (var (role, finding) in blocking)
        {
            var how = finding.Confirmation is { Outcome: Confirmation.Confirmed } c ? $"confirmed by {Code(c.ServedModel ?? c.Model)}"
                : finding.Confirmation is not null ? "the second model's answer was unusable"
                : "not checked by a second model";
            text.Append(CultureInfo.InvariantCulture, $"  - blocking {role} finding, {how}: {Code(finding.ToString())}\n");
        }
        return text.ToString();
    }

    /// <summary>The fix rounds since the last Implement: each with its kind, how it ended, and (a review round) its progress check.</summary>
    private static string Rounds(IReadOnlyList<LedgerEntry> history)
    {
        var transitions = history.Where(e => e.Step is null).ToList();
        var used = TransitionContext.From(transitions.Select(e => e.State).ToList()).FixRounds;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Fix rounds: {used} of {Lifecycle.MaxFixRounds}\n");
        var implemented = transitions.FindLastIndex(e => e.State == WorkState.Implement);
        var progress = history.Where(e => e.Step == RunPipeline.Steps.FixProgress).Select(e => (Row: e, Progress: FixProgress.FromDetail(e.Detail)))
            .Where(p => p.Progress is not null).ToList();
        var round = 0;
        for (var i = Math.Max(implemented, 0) + 1; i < transitions.Count; i++)
        {
            var (from, to) = (transitions[i - 1].State, transitions[i].State);
            if (!TransitionContext.IsFixRound(from, to))
            {
                continue;
            }
            round++;
            var kind = to == WorkState.CIHealing ? "red CI" : from == WorkState.MergeGate ? "conflict with the base" : "review findings";
            var end = transitions.Skip(i + 1).FirstOrDefault(e => e.State != WorkState.Paused && e.Detail != "unpaused");
            var line = $"- round {round} ({kind}): {(end is null ? "in progress" : $"{Outcome(end)}, {Code(end.Detail ?? "")}")}";
            var check = progress.FindLast(p => p.Progress!.Round == round && p.Row.Id > transitions[i].Id);
            if (check.Row is not null)
            {
                line += $"; progress check {Outcome(check.Row)}: {Code(check.Progress!.Reason)}";
            }
            text.Append(line).Append('\n');
        }
        return text.ToString();
    }

    private static string List(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names.Select(Code));

    /// <summary>Text from a row, inert and in a code span (bounded: a row's detail can be long).</summary>
    private static string Code(string text) => UntrustedText.CodeSpan(text.Length > MaxFragment ? text[..MaxFragment] + "…" : text);
}
