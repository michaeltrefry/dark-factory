using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator;

public sealed partial class RunPipeline
{
    /// <summary>The owner's exact adoption, committed in the Recovering transition, not a detached checkpoint.</summary>
    internal sealed record Recovery(string Owner, string Repo, string Branch, string Base, string Pull, string RejectedHead, string Head, string BaseEvidence = "owner assertion validated against repository default")
    {
        public string ToDetail() => JsonSerializer.Serialize(this);
        public static Recovery? Of(IReadOnlyList<LedgerEntry> history)
        {
            var row = history.LastOrDefault(e => e.Step is null && e.State == WorkState.Recovering && e.Detail != "unpaused");
            if (row is null || history.SkipWhile(e => e.Id != row.Id).Skip(1)
                .Any(e => e.Step is null && e.State is WorkState.Intake or WorkState.Planned)) return null;
            try
            {
                return JsonSerializer.Deserialize<Recovery>(row.Detail ?? "")
                    ?? throw new InvalidOperationException("The recorded recovery is unreadable.");
            }
            catch (JsonException ex) { throw new InvalidOperationException("The recorded recovery is unreadable.", ex); }
        }
    }

    /// <summary>
    /// Explicit owner recovery of this item's recorded PR only. Admission is read-only under the existing item lock;
    /// the single Recovering transition commits both evidence and a fresh attempt. A crash afterwards is resumed by run/work,
    /// never by Implement. No worker, checkout, push, claim or control write is made by admission.
    /// </summary>
    public async Task<RunOutcome> RecoverAsync(int storyId, string head, string owner, CancellationToken ct, string? assertedBase = null)
    {
        var item = await ledger.FindAsync(Naming.Source, Naming.Format(storyId), ct);
        if (item is null)
        {
            log.WriteLine("[recovery] refused: the ledger knows no such item.");
            return new RunOutcome(0, WorkState.Intake, null, null, "Recovery refused: the ledger knows no such item.");
        }
        await using (var held = await locks.TryAcquireAsync(item.Id, ct))
        {
            if (held is null)
            {
                log.WriteLine($"[recovery] {item.ExternalId}: refused: the item is already running.");
                return await OutcomeAsync(item, "Recovery refused: the item is already running.", ct);
            }
            await ledger.ReloadAsync(item, ct);
            Recovery recovery;
            try
            {
                void Require(bool yes, string why) { if (!yes) throw new InvalidOperationException(why); }
                Require(gate is not null, "recovery needs the normal gate");
                Require(item.State == WorkState.Escalated, "only an Escalated item can be recovered");
                Require(IsCommit(head) && !string.IsNullOrWhiteSpace(owner), "an exact 40-hex --head and an owner are required");
                await RequireRecoveryControlsAsync(item, ct);
                Require(ignoreScope || await source.InScopeAsync(storyId, ct), "the item is outside its watch scope");
                if (await source.RecoveryClaimRefusalAsync(storyId, ct) is { } ownership)
                    throw new InvalidOperationException(ownership);
                var history = await ledger.HistoryAsync(item, ct);
                Require(history.Any(e => e.Step == Steps.Claimed), "no recorded factory claim");
                Require(!Plans.PlanRelease.MergedOutsideChangeset(history), "the story already merged into its changeset");
                var spec = await source.ReadSpecAsync(storyId, ct);
                var primary = RepoResolver.Resolve(spec.Story.Description, defaultRepo);
                var url = LinkedPullRequestUrl(history) ?? throw new InvalidOperationException("no recorded PR");
                var repo = history.Any(e => e.Step == Steps.PlanStories)
                    ? Plans.Changeset.All(history).FirstOrDefault(c => SameRepo(c.Repo, GitHubGate.ParsePullUrl(url).Repo.FullName)) is { } pass
                        ? RepoRef.Parse(pass.Repo) : throw new InvalidOperationException("the linked plan pass has no recorded changeset")
                    : primary;
                Require(SameRepo(item.Repo, repo.FullName), "the item's repository changed since its recorded work");
                // A plan's changeset may live in a repository other than its primary one.
                var changeset = Plans.Changeset.Of(history, repo);
                var ofPlan = item.PlanItemId is { } plan ? await ledger.ChangesetOfAsync(plan, repo, ct) : (null, []);
                Require(ofPlan is not { Changeset: null, Recorded.Count: > 0 }, "the plan has no pushed changeset in this repository");
                if (item.PlanItemId is { } parent)
                {
                    var planned = await RecordedRepoAsync(parent, item.ExternalId, ct);
                    Require(planned is null || SameRepo(planned, repo.FullName), "the story no longer names its planned repository");
                }
                var branch = changeset?.Branch ?? Naming.BranchName(storyId);
                var (linkedRepo, number) = GitHubGate.ParsePullUrl(url);
                Require(SameRepo(linkedRepo.FullName, repo.FullName), "the recorded PR is in another repository");
                Require(history.Any(e => e.Step == Steps.Linked && e.Detail == $"{url} {BranchUrl(repo, branch)}"), "no recorded PR/branch link");
                var pushed = history.LastOrDefault(e => e.Step == Steps.Pushed);
                Require(changeset is not null || (pushed is not null && (IsCommit(pushed.Detail) || pushed.Detail == branch)),
                    "no recorded worker push or pushed changeset");
                // Replay capture before checking sticky taint. Recovery accepts an owner's remote repair, not an unsafe
                // worker's unpublished work or a way to launder a tainted session into a fresh attempt.
                var sessions = history.Where(e => e.Step == Steps.Pushed).Select(e => e.ClaudeSessionId).OfType<string>().Distinct().ToList();
                Require(changeset is not null || (sessions.Count > 0 && pushed!.ClaudeSessionId is not null), "the recorded push has no worker provenance");
                foreach (var session in sessions) await ledger.ReplayTaintsAsync(item, session, ct);
                if (sessions.Count > 0) _ = await ledger.GrantPushAsync(sessions, ct);
                var started = history.FindLastIndex(e => e.Step == Steps.WorkerStarted);
                Require(started < 0 || history.Skip(started + 1).Any(e => e.Step == Steps.WorkerDone || e.Step == Steps.OrphanKilled),
                    "the last worker has no recorded completion; recover only after it is stopped");
                var rejected = Verdicts(history).LastOrDefault()?.HeadSha ?? Recovery.Of(history)?.RejectedHead;
                Require(IsCommit(rejected), "no recorded reviewed/rejected head");
                Require(head != rejected && head != pushed?.Detail, "the repaired head is unchanged from the rejected/pushed head");
                var pull = await Gate.GitHub.GetPullAsync(repo, number, ct);
                var recordedBase = ofPlan.Changeset?.Branch ?? changeset?.Base;
                Require(!string.IsNullOrWhiteSpace(assertedBase), "--base is required: legacy ordinary rows do not record the original base name");
                var expectedBase = recordedBase ?? pull.DefaultBranch;
                Require(!string.IsNullOrWhiteSpace(expectedBase) && assertedBase == expectedBase,
                    "--base must match the ledger's changeset base, or the repository's actual default branch for an ordinary story");
                recovery = new Recovery(owner, repo.FullName, branch, expectedBase!, url, rejected!, head,
                    recordedBase is null ? "owner assertion validated against repository default" : "ledger changeset base (owner assertion matched)");
                ValidateRecoveryPull(recovery, pull, head, requireOpen: true);
                // Re-read at the boundary: no approval can be based on a stale preflight answer.
                ValidateRecoveryPull(recovery, await Gate.GitHub.GetPullAsync(repo, number, ct), head, requireOpen: true);
                await RequireRecoveryControlsAsync(item, ct);
                if (await source.RecoveryClaimRefusalAsync(storyId, ct) is { } claimRefusal)
                    throw new InvalidOperationException(claimRefusal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not FactoryUnavailableException)
            {
                log.WriteLine($"[recovery] {item.ExternalId}: refused: {ex.Message}");
                return await OutcomeAsync(item, $"Recovery refused: {ex.Message}", ct);
            }
            // The only item write is the atomic admission boundary. Failures after it are not preflight refusals.
            await ledger.RecordAsync(item, WorkState.Recovering, null, recovery.ToDetail(), ct);
            log.WriteLine($"[recovery] {item.ExternalId}: {owner} adopted {recovery.Pull} at {head}; full normal gate required");
        }
        // Admission released the item lock. Any process can now resume the durable state; all run dispatches use that same
        // lock. Nothing after this point can turn the adopted branch back into a fresh implementation.
        return await RunAsync(storyId, ct);
    }

    private static bool SameRepo(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsCommit(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    private async Task RequireRecoveryControlsAsync(WorkItem item, CancellationToken ct)
    {
        if (await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) != ControlState.Running)
            throw new InvalidOperationException("a control holds the item (Pause, Stop, usage or freeze)");
        if (freeze is not null && await CheckFreezeAsync(ct) is { Frozen: true } frozen)
            throw new InvalidOperationException($"{frozen.Message}; {frozen.Remedy}");
    }

    private async Task RecoveringAsync(Run run, CancellationToken ct)
    {
        var (_, pull) = await ReadPullAsync(run, ct);
        await EnsureOpenAsync(run, pull, ct);
        await ledger.RecordAsync(run.Item, WorkState.Review, null, pull.HtmlUrl, ct);
    }

    private static void ValidateRecoveryPull(Recovery recovery, PullFacts pull, string expectedHead, bool requireOpen)
    {
        if ((requireOpen && (!pull.Open || pull.Merged)) || pull.Draft
            || pull.HtmlUrl != recovery.Pull || pull.HeadSha != expectedHead
            || pull.HeadRef != recovery.Branch || !SameRepo(pull.HeadRepo ?? "", recovery.Repo)
            || pull.BaseRef != recovery.Base
            || (recovery.BaseEvidence == "owner assertion validated against repository default" && pull.DefaultBranch != recovery.Base))
            throw new InvalidOperationException("the recorded repaired PR must be open, non-draft, in its own repository, on its recorded branch/base and exact expected head");
    }

    private static void EnsureRecoveryPull(List<LedgerEntry> history, Run run, PullFacts pull)
    {
        if (Recovery.Of(history) is not { } recovery) return;
        // A later ordinary re-run retains its original semantics; this boundary applies only to its own attempt.
        var boundary = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Recovering && e.Detail != "unpaused");
        if (history.Skip(boundary + 1).Any(e => e.Step is null && e.State is WorkState.Intake or WorkState.Planned)) return;
        if (!SameRepo(run.Repo.FullName, recovery.Repo) || run.Branch != recovery.Branch)
            throw new InvalidOperationException("The recovery's recorded repository/branch no longer matches the item.");
        var after = history.Skip(boundary + 1).ToList();
        // Only this attempt's own push or durable base-update can supersede the owner's adopted SHA.
        var advanced = after.LastOrDefault(e => e.Step is Steps.Pushed or Steps.BaseUpdate or Steps.RecoveryPushIntent);
        var update = advanced?.Step is Steps.BaseUpdate or Steps.RecoveryPushIntent ? BaseUpdate.FromDetail(advanced.Detail) : null;
        // A base update is recorded before it is pushed. From remains valid after a crash before that push; the normal
        // queue must publish/review the update, and every later verdict/merge approval is still SHA-bound.
        var published = advanced is not null && after.Any(e => e.Id > advanced.Id && e.Step == Steps.RecoveryBasePushed && e.Detail == update?.To);
        var expected = advanced?.Step == Steps.Pushed ? advanced.Detail
            : update is not null ? !published && pull.HeadSha == update.From ? update.From : update.To : recovery.Head;
        ValidateRecoveryPull(recovery, pull, expected ?? throw new InvalidOperationException("Unreadable recovery head advance."), requireOpen: false);
    }
}
