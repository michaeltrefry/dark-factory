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
        new HashSet<string> { RunPipeline.Steps.GateDecision, RunPipeline.Steps.CiFailure, RunPipeline.Steps.NewTests, RunPipeline.Steps.MergeConflict };

    private const int MaxFragment = 400;

    /// <summary>The longest list of files with no CodeGraph data a gate row's line shows (sc-26254).</summary>
    private const int MaxNoCodeGraphData = 2000;

    /// <summary>How many failing tools' names a review's (or its second opinions') tool use names; the rest are counted.</summary>
    public const int MaxFailingToolsShown = 3;

    /// <summary>The most characters of one failing tool's name a report shows.</summary>
    private const int MaxToolName = 60;

    /// <summary>
    /// The PR's description: the item it implements (and, for an issue, GitHub's closing keyword), then the ledger's facts. Rendered when the
    /// PR is opened and again as the item moves on (at merge).
    /// </summary>
    /// <param name="baseBranch">The changeset branch the PR targets when the item is a plan's story (sc-25872); null for the default branch.</param>
    public static string PullRequestBody(WorkStory story, IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts,
        string? baseBranch = null)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Implements {story.Kind.Noun} [{story.Ref}]({story.AppUrl}): {(story.UntrustedName ? UntrustedText.CodeSpan(story.Name) : story.Name)}\n");
        if (story.Closes is { } closes)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nCloses {closes}\n");
        }
        if (baseBranch is not null)
        {
            text.Append("\nBase: ").Append(Code(baseBranch)).Append(", its plan's changeset branch. It reaches the default branch with the rest "
                + "of the plan, by the changeset's own pull request through the same merge gate.\n");
        }
        text.Append("\nOpened by Dark Factory.\n\n");
        text.Append(Facts(history, sessionCosts));
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The description of a plan's changeset pull request (sc-25872), written by code: what it merges where, one line per story of
    /// the plan (<see cref="PlanStoryLines"/>: a story's title is a planner's text, inert in a code span), then the plan item's own
    /// ledger facts. Rendered when the pull request is opened and again at its merge. One of several changesets
    /// (<paramref name="changesets"/>, sc-25873: every repository's, in merge order; <paramref name="stories"/> and
    /// <paramref name="history"/> are then this repository's stories and this pull request's rows): it also lists where each
    /// repository's changeset stands.
    /// </summary>
    public static string ChangesetPullRequestBody(string externalId, string? appUrl, Plans.Changeset changeset, IReadOnlyList<PlanStoryReport> stories,
        IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts, IReadOnlyList<Plans.ChangesetState>? changesets = null)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Changeset of plan {(appUrl is null ? externalId : $"[{externalId}]({appUrl})")}: merges {Code(changeset.Branch)} into {Code(changeset.Base)}.\n\n");
        if (changesets is { Count: > 1 })
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Each of the plan's {stories.Count} stories in {Code(changeset.Repo)} was reviewed and merged into {Code(changeset.Branch)} by its own pull request through the ");
            text.Append("factory's merge gate; this pull request takes them to the default branch together, through the same review, CI and gate.")
                .Append(OutsideStories(stories, "Not {1}, which")).Append("\n\n");
            ChangesetLines(text, changesets);
            text.Append('\n');
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Each of the plan's {stories.Count} stories was reviewed and merged into {Code(changeset.Branch)} by its own pull request through the ");
            text.Append("factory's merge gate; this pull request takes them to the default branch together, through the same review, CI and gate.")
                .Append(OutsideStories(stories, "Not {1}, which")).Append("\n\n");
        }
        PlanStoryLines(text, stories);
        text.Append("\nOpened by Dark Factory.\n\n");
        text.Append(Facts(history, sessionCosts));
        return Clip(text.ToString().TrimEnd(), MaxChangesetReportLength) + "\n";
    }

    /// <summary>The board comment that closes an item out once it merged: the merge commit and the ledger's facts.</summary>
    public static string MergedCloseout(string externalId, IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts)
    {
        var merged = history.LastOrDefault(e => e.Step is null && e.State == WorkState.Merge)?.Detail;
        return $"{WorkSourceComments.Author} {externalId} merged{IntoChangeset(history)}{(merged is null ? "" : $" as {Code(merged)}")} by the factory's merge gate.\n\n"
            + Facts(history, sessionCosts).TrimEnd();
    }

    /// <summary>
    /// " into its plan's changeset branch `…`" when the base the gate recorded for the item's latest merge (<c>merge-files</c>) is a
    /// changeset branch (sc-25872): such a change is not on the default branch yet. Nothing otherwise.
    /// </summary>
    private static string IntoChangeset(IReadOnlyList<LedgerEntry> history) =>
        Controls.MergeFiles.FromDetail(history.LastOrDefault(e => e.Step == RunPipeline.Steps.MergeFiles)?.Detail) is { } files
        && ItemNaming.IsChangesetBranch(files.Base)
            ? $" into its plan's changeset branch {Code(files.Base)}"
            : "";

    /// <summary>The most stories a plan's closeout lists; the rest are counted (a plan holds at most <see cref="Plans.PlanParser.MaxStories"/>).</summary>
    public const int MaxPlanStoriesShown = 40;

    /// <summary>
    /// The board comment that closes a plan item out once every story of its plan has merged (sc-25871): one line per story, from
    /// that story's own ledger rows — its typed outcome and state, the merge commit its Merge row holds and its pull request (the
    /// <c>linked</c> checkpoint). A story's title is a planner's text: inert, in a code span. With a changeset (sc-25872) the
    /// comment is posted only once the changeset's own pull request has merged: <paramref name="changeset"/> is the plan item's own
    /// rows and session costs, and the comment says where the changeset merged and ends with those facts. <paramref name="note"/>
    /// says why the factory merged no changeset pull request (an empty changeset, or one a person merged:
    /// <see cref="Plans.Changeset.MergedOutside"/>). A plan of several repositories (<paramref name="changesets"/>, sc-25873): one
    /// line per repository instead, in merge order — its changeset's pull request and merge commit, or why it had none — and
    /// <paramref name="changeset"/> holds the last pull request's rows.
    /// </summary>
    public static string PlanCloseout(string externalId, IReadOnlyList<PlanStoryReport> stories, string? note = null,
        (IReadOnlyList<LedgerEntry> History, IReadOnlyList<decimal?> SessionCosts)? changeset = null,
        IReadOnlyList<Plans.ChangesetState>? changesets = null)
    {
        var merged = stories.Count(s => s.History.Any(e => e.Step is null && e.State == WorkState.Merge));
        var several = changesets is { Count: > 1 };
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{WorkSourceComments.Author} {externalId}'s plan is complete: {merged} of its {stories.Count} stories merged through the factory's merge gate.{OutsideStories(stories, "{0} more ({1})")}\n\n");
        if (several)
        {
            ChangesetLines(text, changesets!);
            text.Append('\n');
        }
        else if (changeset is { } own)
        {
            var commit = own.History.LastOrDefault(e => e.Step is null && e.State == WorkState.Merge)?.Detail;
            var into = Controls.MergeFiles.FromDetail(own.History.LastOrDefault(e => e.Step == RunPipeline.Steps.MergeFiles)?.Detail)?.Base;
            text.Append("Its changeset merged").Append(into is null ? "" : $" into {Code(into)}").Append(commit is null ? "" : $" as {Code(commit)}")
                .Append(" by the factory's merge gate")
                .Append(RunPipeline.LinkedPullRequestUrl(own.History.ToList()) is { } pull ? $": {Link(pull)}" : "").Append(".\n\n");
        }
        if (note is not null && !several)
        {
            text.Append(Plans.Changeset.IsMergedOutside(note) ? "The factory's merge gate did not merge its changeset: " : "No changeset pull request was opened: ")
                .Append(Code(note)).Append(".\n\n");
        }
        text.Append("**From the factory ledger** (results the orchestrator executed and recorded, never a worker's own report):\n\n");
        PlanStoryLines(text, stories);
        if (changeset is { } facts)
        {
            text.Append(several ? "\nThe last changeset's own pull request:\n\n" : "\nThe changeset's own pull request:\n\n")
                .Append(Facts(facts.History, facts.SessionCosts));
        }
        return Clip(text.ToString().TrimEnd(), changeset is null ? MaxFactsLength : MaxChangesetReportLength);
    }

    /// <summary>
    /// One line per repository of a plan of several (sc-25873), in merge order, from the plan item's own rows: where its changeset
    /// stands — merged (into which branch, as which commit, by which pull request), empty, merged by someone else, its pull request
    /// open, or not opened.
    /// </summary>
    private static void ChangesetLines(StringBuilder text, IReadOnlyList<Plans.ChangesetState> changesets)
    {
        text.Append(CultureInfo.InvariantCulture, $"The plan changes {changesets.Count} repositories. ");
        text.Append("Each one's changeset merges into its default branch by its own pull request, one repository at a time in this order; "
            + "the merges are not atomic:\n\n");
        foreach (var state in changesets)
        {
            text.Append("- ").Append(Code(state.Changeset.Repo)).Append(": ").Append(state.How switch
            {
                Plans.ChangesetDelivery.Merged => $"merged into {Code(state.Changeset.Base)}{(state.Commit is null ? "" : $" as {Code(state.Commit)}")} by the factory's merge gate"
                    + (state.Pull is null ? "" : $": {Link(state.Pull)}"),
                Plans.ChangesetDelivery.Empty => $"no pull request: {Code(state.Changeset.Branch)} did not differ from {Code(state.Changeset.Base)}",
                Plans.ChangesetDelivery.MergedOutside => "not merged by the factory's merge gate: its pull request was merged outside the factory"
                    + (state.Pull is null ? "" : $": {Link(state.Pull)}"),
                _ when state.State is { } at => $"pull request {(state.Pull is null ? "(none recorded)" : Link(state.Pull))} ({at})",
                _ => "not opened yet: it waits on its stories and on the repositories before it",
            }).Append('\n');
        }
    }

    /// <summary>
    /// The sentence a plan's report adds when stories of it were merged outside the factory into its changeset branch (sc-25877,
    /// <see cref="Plans.PlanRelease.MergedOutsideChangeset"/>): which, and that the changeset pull request's review and gate are
    /// the only ones their change gets. <paramref name="lead"/>: how the sentence names them after the report's own count
    /// ("{0} more ({1})" in a closeout, "Not {1}:" after "each story was merged by the gate"). Nothing when there is none.
    /// </summary>
    private static string OutsideStories(IReadOnlyList<PlanStoryReport> stories, string lead)
    {
        var outside = stories.Where(s => Plans.PlanRelease.MergedOutsideChangeset(s.History)).Select(s => s.ExternalId).ToList();
        return outside.Count == 0 ? ""
            : " " + string.Format(CultureInfo.InvariantCulture, lead, outside.Count, string.Join(", ", outside))
                + $" {(outside.Count == 1 ? "was" : "were")} {Plans.PlanRelease.MergedOutsideIntoChangeset} (the factory's merge gate did not merge "
                + (outside.Count == 1 ? "its pull request" : "their pull requests")
                + ", so the changeset pull request's review and gate are the only ones that change gets).";
    }

    /// <summary>A pull request URL as a report shows it: itself when it is a short https URL, else inert in a code span.</summary>
    private static string Link(string url) =>
        url.Length <= MaxFragment && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : Code(url);

    /// <summary>One line per story of a plan, from that story's own ledger rows (at most <see cref="MaxPlanStoriesShown"/>, the rest counted).</summary>
    private static void PlanStoryLines(StringBuilder text, IReadOnlyList<PlanStoryReport> stories)
    {
        foreach (var story in stories.Take(MaxPlanStoriesShown))
        {
            var history = story.History.ToList();
            var state = history.LastOrDefault(e => e.Step is null);
            var commit = history.LastOrDefault(e => e.Step is null && e.State == WorkState.Merge)?.Detail;
            var pull = RunPipeline.LinkedPullRequestUrl(history);
            text.Append(CultureInfo.InvariantCulture, $"- {story.ExternalId} {Code(story.Title)}: ");
            text.Append(state is null ? "no ledger row" : $"{Outcome(state)} {state.State}");
            text.Append(commit is not null ? $", merged as {Code(commit)}"
                : Plans.PlanRelease.MergedOutsideChangeset(history) ? $", {Plans.PlanRelease.MergedOutsideIntoChangeset}" : ", not merged");
            text.Append(pull is null ? ", no pull request recorded" : $", {Link(pull)}");
            // Such a story's item stays Escalated: nothing the factory does moves its board story, so the report says who must.
            text.Append(commit is null && Plans.PlanRelease.MergedOutsideChangeset(history) ? $"; {Plans.PlanRelease.BoardStoryStillClaimed}" : "");
            text.Append('\n');
        }
        if (stories.Count > MaxPlanStoriesShown)
        {
            text.Append(CultureInfo.InvariantCulture, $"- … and {stories.Count - MaxPlanStoriesShown} more (the ledger has them)\n");
        }
    }

    /// <summary>
    /// The ledger's facts about the item as Markdown: its state, its worker sessions, every check row, every verdict row, the fix rounds and
    /// the cost. Bounded to <see cref="MaxFactsLength"/> characters, so a PR body or a comment carrying it stays under GitHub's (65,536) and
    /// Shortcut's limits whatever the item's history: each section's heading counts its rows by outcome, and the most recent verdict is always
    /// shown (with at most <see cref="MaxBlockingFindingsShown"/> blocking findings); what is left of the budget shows the newest checks, then
    /// the newest earlier verdicts, and a line says how many older ones are left out (the ledger has them all).
    /// </summary>
    public static string Facts(IReadOnlyList<LedgerEntry> history, IReadOnlyList<decimal?> sessionCosts)
    {
        var head = new StringBuilder();
        head.Append("**From the factory ledger** (results the orchestrator executed and recorded, never a worker's own report):\n\n");
        if (history.LastOrDefault(e => e.Step is null) is { } state)
        {
            head.Append(CultureInfo.InvariantCulture, $"- State: {state.State} ({StepOutcomes.Name(state.Outcome)}, {state.RecordedAt:u})\n");
        }
        var sessions = history.Where(e => e.Step == RunPipeline.Steps.Session && e.ClaudeSessionId is not null).Select(e => e.ClaudeSessionId!)
            .Distinct(StringComparer.Ordinal).ToList();
        head.Append(CultureInfo.InvariantCulture, $"- Worker sessions: {List(sessions)}\n");

        var checks = history.Where(e => e.Step is { } step && CheckSteps.Contains(step)).ToList();
        var verdicts = history.Where(e => e.Step == RunPipeline.Steps.Verdict).ToList();
        var checkLines = checks.Select(row => $"- {Outcome(row)} {Check(row)}\n").ToList();
        var verdictBlocks = verdicts.Select(Verdict).ToList();
        var checksHeading = $"\nChecks run ({Summary(checks)}):\n";
        var verdictsHeading = $"\nReview verdicts ({Summary(verdicts)}):\n";
        var tail = $"\n{Rounds(history)}" + string.Create(CultureInfo.InvariantCulture, $"\nWorker cost: {Cost(sessionCosts)}\n");
        var earlier = verdictBlocks.Take(Math.Max(verdictBlocks.Count - 1, 0)).ToList();
        var latest = verdictBlocks.Count == 0 ? "" : verdictBlocks[^1];

        var budget = MaxFactsLength - head.Length - checksHeading.Length - verdictsHeading.Length - tail.Length - latest.Length
            - (2 * OmittedLineReserve);
        var shownChecks = Newest(checkLines, ref budget);
        var shownVerdicts = Newest(earlier, ref budget);

        var text = new StringBuilder(head.ToString());
        text.Append(checksHeading);
        if (checks.Count == 0)
        {
            text.Append("- none recorded\n");
        }
        Omitted(text, checkLines.Count - shownChecks, "check(s)");
        foreach (var line in checkLines.Skip(checkLines.Count - shownChecks))
        {
            text.Append(line);
        }
        text.Append(verdictsHeading);
        if (verdicts.Count == 0)
        {
            text.Append("- none recorded\n");
        }
        Omitted(text, earlier.Count - shownVerdicts, "verdict(s)");
        foreach (var block in earlier.Skip(earlier.Count - shownVerdicts))
        {
            text.Append(block);
        }
        text.Append(latest).Append(tail);
        return text.Length <= MaxFactsLength ? text.ToString() : text.ToString(0, MaxFactsLength) + "\n… (cut short; the ledger has the rest)\n";
    }

    /// <summary>The longest <see cref="Facts"/> renders (characters; plus a short cut-short line in the worst case).</summary>
    public const int MaxFactsLength = 60_000;

    /// <summary>The most characters of a changeset report (its story lines, then <see cref="Facts"/>): under GitHub's 65,536.</summary>
    public const int MaxChangesetReportLength = 64_000;

    /// <summary>The blocking findings listed per verdict; the rest are counted.</summary>
    public const int MaxBlockingFindingsShown = 10;

    /// <summary>The longest escalation reason a comment quotes (characters): with the facts it stays under GitHub's comment limit.</summary>
    public const int MaxReasonLength = 4_000;

    /// <summary><paramref name="text"/>, cut to <paramref name="max"/> characters with a note when longer.</summary>
    public static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"… ({text.Length - max} more characters; the ledger has them)";

    private const int MaxNamesShown = 20;
    private const int OmittedLineReserve = 80;

    /// <summary>How many of <paramref name="lines"/>' newest fit in <paramref name="budget"/> (and takes them from it).</summary>
    private static int Newest(IReadOnlyList<string> lines, ref int budget)
    {
        var shown = 0;
        for (var i = lines.Count - 1; i >= 0 && lines[i].Length <= budget; i--, shown++)
        {
            budget -= lines[i].Length;
        }
        return shown;
    }

    private static void Omitted(StringBuilder text, int count, string what)
    {
        if (count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"- … {count} earlier {what} not shown (the ledger has them)\n");
        }
    }

    /// <summary>A section's count, by outcome: <c>3: 2 passed, 1 failed</c>.</summary>
    private static string Summary(IReadOnlyList<LedgerEntry> rows) =>
        rows.Count == 0 ? "0"
            : $"{rows.Count}: " + string.Join(", ", rows.GroupBy(r => r.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Count()} {StepOutcomes.Name(g.Key)}"));

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
                // The files CodeGraph gave no data for (sc-26254) are at the end of the row, past the clip: said on their own.
                var detail = row.Detail ?? "";
                var noData = detail.IndexOf(CodeGraphRisk.NoDataMark, StringComparison.Ordinal);
                return $"merge gate: {Code(detail)}"
                    + (noData < 0 ? "" : $"; {UntrustedText.CodeSpan(Clip(detail[(noData + 2)..], MaxNoCodeGraphData))}");
            case RunPipeline.Steps.CiFailure when CiTriage.FromDetail(row.Detail) is { } triage:
                return $"CI on {Code(Ci.Short(triage.HeadSha))} failed: the PR's {List(triage.Fixable)}; not the PR's {List(triage.NotThePrs)}";
            case RunPipeline.Steps.MergeConflict:
                return $"merge with the base: conflict {Code(row.Detail ?? "")}";
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
            $"{r.Role} ({Code(r.ServedName)}{(r.CarriedFrom is { } from ? $", carried from {Code(Ci.Short(from))}" : "")}"
            + $"{(r.Tools is { Count: > 0 } tools ? $", {ToolUse(tools)}" : "")}"
            + $"{(r.Findings.SelectMany(f => f.Confirmation?.Tools ?? []).ToList() is { Count: > 0 } confirms ? $"; second opinions: {ToolUse(confirms)}" : "")}"
            + $"{(r.Error is null ? "" : ", unusable answer")})")));
        text.Append(CultureInfo.InvariantCulture,
            $"; {blocking.Count} blocking, {findings.Count - blocking.Count} optional ({findings.Count(f => f.Finding.Downgraded)} downgraded by the second opinion)");
        if (verdict.Overlay is { } overlay)
        {
            // sc-25708 (E3): whether the reviewers' CodeGraph answers were of the head or of the default branch.
            text.Append(overlay.IsReady
                ? $"; CodeGraph answered about the head (overlay {Code(overlay.OverlayId?.ToString(CultureInfo.InvariantCulture) ?? "?")})"
                : $"; CodeGraph overlay of the head {Code(overlay.Outcome)}{(overlay.Reason is { } why ? $" ({Code(why)})" : "")}, answers from the default branch");
        }
        var fallbacks = verdict.Reviews.Where(r => r.CarriedFrom is null)
            .SelectMany(r => (r.Tools ?? []).Concat(r.Findings.SelectMany(f => f.Confirmation?.Tools ?? []))).Count(t => t.Fallback is not null);
        if (fallbacks > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"; {fallbacks} CodeGraph call(s) fell back to the default branch (the head not indexed)");
        }
        text.Append('\n');
        foreach (var (role, finding) in blocking.Take(MaxBlockingFindingsShown))
        {
            var how = finding.Confirmation is { Outcome: Confirmation.Confirmed } c ? $"confirmed by {Code(c.ServedName)}"
                : finding.Confirmation is not null ? "the second opinion was unusable"
                : "not checked by a second opinion";
            text.Append(CultureInfo.InvariantCulture, $"  - blocking {role} finding, {how}: {Code(finding.ToString())}\n");
        }
        if (blocking.Count > MaxBlockingFindingsShown)
        {
            text.Append(CultureInfo.InvariantCulture, $"  - … and {blocking.Count - MaxBlockingFindingsShown} more blocking finding(s) (the ledger has them)\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// A review's or its second opinions' tool use as recorded (E4/E8): the calls, how many answered an error, and the first
    /// <see cref="MaxFailingToolsShown"/> failing tools' names (distinct, each cut and inert: a model may name any tool).
    /// </summary>
    private static string ToolUse(IReadOnlyCollection<ToolCall> calls)
    {
        var failed = calls.Where(c => c.Error).ToList();
        var names = failed.Select(c => c.Tool.Length > MaxToolName ? c.Tool[..MaxToolName] + "…" : c.Tool).Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", names.Take(MaxFailingToolsShown).Select(UntrustedText.CodeSpan))
            + (names.Count > MaxFailingToolsShown ? $" and {names.Count - MaxFailingToolsShown} more" : "");
        return $"{calls.Count} tool call{(calls.Count == 1 ? "" : "s")}"
            + (failed.Count == 0 ? "" : $", {failed.Count} tool error{(failed.Count == 1 ? "" : "s")}: {shown}");
    }

    /// <summary>The fix rounds since the last Implement: each with its kind, how it ended, and (a review round) its progress check.</summary>
    private static string Rounds(IReadOnlyList<LedgerEntry> history)
    {
        var transitions = history.Where(e => e.Step is null).ToList();
        var used = TransitionContext.From(transitions.Select(e => e.State).ToList()).FixRounds;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Fix rounds: {used} of {RunPipeline.FixCapOf(history)}\n");
        var implemented = transitions.FindLastIndex(e => TransitionContext.StartsAttempt(e.State));
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

    /// <summary>At most <see cref="MaxNamesShown"/> names (the rest counted).</summary>
    private static string List(IReadOnlyList<string> names) =>
        names.Count == 0 ? "none"
            : string.Join(", ", names.Take(MaxNamesShown).Select(Code)) + (names.Count > MaxNamesShown ? $" and {names.Count - MaxNamesShown} more" : "");

    /// <summary>Text from a row, inert and in a code span (bounded: a row's detail can be long).</summary>
    private static string Code(string text) => UntrustedText.CodeSpan(text.Length > MaxFragment ? text[..MaxFragment] + "…" : text);
}

/// <summary>One story of a plan as its plan's closeout reports it: its id, its title (a planner's text) and its own ledger rows.</summary>
public sealed record PlanStoryReport(string ExternalId, string Title, IReadOnlyList<LedgerEntry> History);
