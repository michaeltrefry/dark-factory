using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator;

/// <summary>
/// What the Review → CI → MergeGate → Merge handlers need: GitHub as the gate App (<see cref="GitHubGate"/>), the reviewer
/// (through the router), the reviewer models to choose from (first one of a family the implementer did not use), and how
/// long and how often CI is waited for.
/// </summary>
public sealed record GateStage(
    IGateGitHub GitHub,
    IReviewer Reviewer,
    IReadOnlyList<string> ReviewerModels,
    TimeSpan CiPollInterval,
    TimeSpan CiTimeout,
    TimeProvider? Time = null)
{
    public static readonly IReadOnlyList<string> DefaultReviewerModels = ["gpt-5.6-sol", "claude-opus-5-5"];
    public static readonly TimeSpan DefaultCiPollInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultCiTimeout = TimeSpan.FromMinutes(30);
}

/// <summary>The review and merge-gate stage (a pipeline with a <see cref="GateStage"/>).</summary>
public sealed partial class RunPipeline
{
    /// <summary>The reviewer judged the head commit not mergeable; the item escalates with its summary.</summary>
    public sealed class ReviewFailedException(string message) : Exception(message);

    /// <summary>The gate refused to merge (a rule does not hold, or its policy is unreadable/invalid); the item escalates.</summary>
    public sealed class GateBlockedException(string message) : Exception(message);

    private GateStage Gate => gate ?? throw new InvalidOperationException("This pipeline has no gate stage.");

    private TimeProvider GateTime => Gate.Time ?? TimeProvider.System;

    /// <summary>Every model the item's implementer sessions reported, oldest first.</summary>
    internal static List<string> ImplementerModels(List<LedgerEntry> history) =>
        history.Where(e => e.Step == Steps.ImplementerModel && e.Detail is not null).Select(e => e.Detail!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Every review verdict in the item's ledger, oldest first.</summary>
    internal static List<ReviewVerdict> Verdicts(List<LedgerEntry> history) =>
        history.Where(e => e.Step == Steps.Verdict).Select(e => ReviewVerdict.FromDetail(e.Detail)).OfType<ReviewVerdict>().ToList();

    /// <summary>The PR Implement opened and linked (its <see cref="Steps.Linked"/> checkpoint), else the Review row's PR.</summary>
    internal static string? LinkedPullRequestUrl(List<LedgerEntry> history) =>
        history.LastOrDefault(e => e.Step == Steps.Linked)?.Detail?.Split(' ')[0]
        ?? history.LastOrDefault(e => e.Step is null && e.State == WorkState.Review && e.Detail?.StartsWith("https://", StringComparison.Ordinal) == true)?.Detail;

    private async Task<(List<LedgerEntry> History, PullFacts Pull)> ReadPullAsync(Run run, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(run.Item, ct);
        var url = LinkedPullRequestUrl(history) ?? throw new InvalidOperationException($"{run.Item.ExternalId} has no pull request in its ledger.");
        var (_, number) = GitHubGate.ParsePullUrl(url);
        var pull = await Gate.GitHub.GetPullAsync(run.Repo, number, ct);
        return (history, pull);
    }

    private static void EnsureOpen(PullFacts pull)
    {
        if (!pull.Open)
        {
            throw new InvalidOperationException($"{pull.HtmlUrl} is {(pull.Merged ? "merged" : "closed")} outside the factory.");
        }
    }

    /// <summary>
    /// Review: one reviewer, pinned through the router to a model family the implementer did not use, judges the PR's
    /// current head commit. The verdict is checkpointed bound to that commit (E3) before it counts; a commit that already
    /// has a verdict is not reviewed again. Pass → CI; fail → escalate (the fix loop is not part of this stage yet). The
    /// reviewer's router session is named in the ledger (<see cref="Steps.ReviewSession"/>) before the call, and never as a
    /// row's Claude session (that column stays the implementer's). The router refusing the call for usage pauses the factory
    /// for usage (the item resumes and reviews again once it lifts) rather than escalating the item.
    /// </summary>
    private async Task ReviewAsync(Run run, CancellationToken ct)
    {
        var (history, pull) = await ReadPullAsync(run, ct);
        EnsureOpen(pull);
        var verdict = Verdicts(history).LastOrDefault(v => v.HeadSha == pull.HeadSha);
        if (verdict is null)
        {
            var model = ReviewerChoice.Choose(Gate.ReviewerModels, ImplementerModels(history));
            log.WriteLine($"[review] {pull.HtmlUrl} head {Ci.Short(pull.HeadSha)}: reviewing with {model}");
            var diff = await Gate.GitHub.GetDiffAsync(run.Repo, pull.BaseSha, pull.HeadSha, ct);
            var session = Guid.NewGuid().ToString();
            await ledger.CheckpointAsync(run.Item, Steps.ReviewSession, null, $"{session} {model} {pull.HeadSha}", ct);
            try
            {
                verdict = await Gate.Reviewer.ReviewAsync(new ReviewRequest(run.Story, run.Repo.FullName, pull, diff, model, session), ct);
            }
            catch (RouterUsageLimitedException ex)
            {
                // The plans ran out, not the review: pause the factory (backing off) and review this head again afterwards.
                // Without a control table nothing could hold the pause, so the failure escalates.
                if (await _controls.PauseForUsageAsync(null, UsagePause.ReviewerRateLimited, ct) is { State: ControlState.Paused } pause)
                {
                    log.WriteLine($"[review] {ex.Message}; factory paused for usage until {pause.ResumeAt:u}");
                    throw new ControlRequestedException(ControlState.Paused);
                }
                throw;
            }
            await ledger.CheckpointAsync(run.Item, Steps.Verdict, null, verdict.ToDetail(), ct);
        }
        log.WriteLine($"[review] {Ci.Short(pull.HeadSha)}: {verdict.Verdict} by {verdict.ServedModel ?? verdict.Model}");
        if (!verdict.Passed)
        {
            throw new ReviewFailedException($"The reviewer ({verdict.ServedModel ?? verdict.Model}) failed {pull.HtmlUrl} at {Ci.Short(pull.HeadSha)}: {verdict.Summary}");
        }
        await ledger.RecordAsync(run.Item, WorkState.CI, null, pull.HeadSha, ct);
    }

    /// <summary>
    /// CI: waits (polling, controls checked between polls) until every check on the PR's head commit has finished.
    /// Green → MergeGate; failed → escalate; still pending after the CI timeout → escalate. A push that moved the head
    /// away from the reviewed commit voids the verdict (E3): back to Review.
    /// </summary>
    private async Task CiAsync(Run run, CancellationToken ct)
    {
        var deadline = GateTime.GetUtcNow() + Gate.CiTimeout;
        while (true)
        {
            var (history, pull) = await ReadPullAsync(run, ct);
            EnsureOpen(pull);
            if (!Verdicts(history).Any(v => v.HeadSha == pull.HeadSha && v.Passed))
            {
                await ledger.RecordAsync(run.Item, WorkState.Review, null, $"head moved to {pull.HeadSha} after the review; reviewing it again", ct);
                return;
            }
            var (state, why) = Ci.Evaluate(await Gate.GitHub.GetCiAsync(run.Repo, pull.HeadSha, ct));
            switch (state)
            {
                case CiState.Green:
                    log.WriteLine($"[ci] {why}");
                    await ledger.RecordAsync(run.Item, WorkState.MergeGate, null, pull.HeadSha, ct);
                    return;
                case CiState.Failed:
                    throw new GateBlockedException(why);
            }
            if (GateTime.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"CI did not finish within {Gate.CiTimeout}: {why}");
            }
            log.WriteLine($"[ci] {why}; checking again in {Gate.CiPollInterval}");
            await Task.Delay(Gate.CiPollInterval, GateTime, ct);
            await ThrowIfControlledAsync(run.Item, ct);
        }
    }

    /// <summary>
    /// MergeGate (E1–E3): evaluates <see cref="MergeGate"/> on facts read now — the base branch's <c>factory/gate.yaml</c>,
    /// the PR, its head commit's CI and the ledger's verdicts — and checkpoints the decision. Merge: merges exactly the
    /// gated head commit with the gate App's token, then records Merge with the merge commit. A head with no verdict (a
    /// push after the review, or one that lands between the evaluation and the merge, which GitHub refuses) → Review.
    /// Anything else → escalate; nothing merges. A PR found merged (on resume, or re-read after a failed merge call) at a
    /// head some <see cref="Steps.GatePassed"/> names is recorded as merged; merged at any other head, it escalates.
    /// </summary>
    private async Task MergeGateAsync(Run run, CancellationToken ct)
    {
        var (history, pull) = await ReadPullAsync(run, ct);
        if (pull.Merged)
        {
            // A run that stopped after GitHub merged but before the ledger said so (a crash, Ctrl-C during the merge call,
            // then an "unpaused" row): only a commit this gate passed counts. gate-passed is bound to its head SHA, so one
            // anywhere in the item's history proves the merged head is a commit the gate let through.
            if (GatePassedAt(history, pull) is { } commit)
            {
                log.WriteLine($"[merge] {pull.HtmlUrl} was merged by the gate at {Ci.Short(pull.HeadSha)} as {commit}; recording it");
                await ledger.RecordAsync(run.Item, WorkState.Merge, null, commit, ct);
                return;
            }
            throw new GateBlockedException($"{pull.HtmlUrl} was merged outside the gate.");
        }

        string? policy = null, policyError = null;
        try
        {
            policy = await Gate.GitHub.GetPolicyAsync(run.Repo, pull.BaseRef, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            policyError = ex.Message;
        }
        var ci = await Gate.GitHub.GetCiAsync(run.Repo, pull.HeadSha, ct);
        var decision = MergeGate.Evaluate(policy, policyError, pull, ci, Verdicts(history), ImplementerModels(history));
        await ledger.CheckpointAsync(run.Item, Steps.GateDecision, null, decision.Detail, ct);
        log.WriteLine($"[gate] {decision.Detail}");
        switch (decision.Outcome)
        {
            case GateOutcome.ReviewHead:
                await ledger.RecordAsync(run.Item, WorkState.Review, null, $"head moved to {pull.HeadSha} after the review; reviewing it again", ct);
                return;
            case GateOutcome.Blocked:
                throw new GateBlockedException($"The merge gate refused {pull.HtmlUrl}: {string.Join("; ", decision.Reasons)}");
        }

        await ThrowIfControlledAsync(run.Item, ct);
        await ledger.CheckpointAsync(run.Item, Steps.GatePassed, null, pull.HeadSha, ct);
        MergeResult merged;
        try
        {
            merged = await Gate.GitHub.MergeAsync(run.Repo, pull.Number, pull.HeadSha, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The call failed (e.g. timed out) but GitHub may have merged anyway: a PR merged at the gated head is merged.
            if (await MergedAfterFailureAsync(run, pull, ex, ct) is not { } commit)
            {
                throw;
            }
            log.WriteLine($"[merge] the merge call failed ({ex.Message}), but GitHub merged {pull.HtmlUrl} at {Ci.Short(pull.HeadSha)} as {commit}");
            await ledger.RecordAsync(run.Item, WorkState.Merge, null, commit, ct);
            return;
        }
        if (merged.HeadMoved)
        {
            await ledger.RecordAsync(run.Item, WorkState.Review, null, $"head moved from {pull.HeadSha} before the merge; reviewing it again", ct);
            return;
        }
        if (!merged.Merged || merged.CommitSha is null)
        {
            throw new GateBlockedException($"GitHub did not merge {pull.HtmlUrl}: {merged.Message}");
        }
        log.WriteLine($"[merge] {pull.HtmlUrl} merged as {merged.CommitSha}");
        await ledger.RecordAsync(run.Item, WorkState.Merge, null, merged.CommitSha, ct);
    }

    /// <summary>The merge commit of <paramref name="pull"/> when it is merged at a head a <see cref="Steps.GatePassed"/> names, else null.</summary>
    private static string? GatePassedAt(List<LedgerEntry> history, PullFacts pull) =>
        pull.Merged && history.Any(e => e.Step == Steps.GatePassed && e.Detail == pull.HeadSha) ? pull.MergeCommitSha : null;

    /// <summary>
    /// After a merge call failed: re-reads the PR and returns its merge commit when GitHub merged it at the gated head;
    /// null otherwise, and when the PR cannot be read (the merge call's own failure is then the one that counts).
    /// </summary>
    private async Task<string?> MergedAfterFailureAsync(Run run, PullFacts gated, Exception failure, CancellationToken ct)
    {
        try
        {
            var after = await Gate.GitHub.GetPullAsync(run.Repo, gated.Number, ct);
            return after.Merged && after.HeadSha == gated.HeadSha ? after.MergeCommitSha : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.WriteLine($"[merge] the merge call failed ({failure.Message}) and the PR could not be re-read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Merge: the change is merged (the Merge row holds the merge commit); the board shows it, then Watch.</summary>
    private async Task MergeAsync(Run run, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(run.Item, ct);
        var entered = history.FindLastIndex(e => e.Step is null);
        var commit = history[entered].Detail;
        if (!history.Skip(entered + 1).Any(e => e.Step == Steps.MergedReported))
        {
            await source.ReportStateAsync(run.Story.Id, BoardState.Merged, null, ct);
            await ledger.CheckpointAsync(run.Item, Steps.MergedReported, null, commit, ct);
        }
        await ledger.RecordAsync(run.Item, WorkState.Watch, null, commit, ct);
    }
}
