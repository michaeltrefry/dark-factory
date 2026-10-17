using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator;

public sealed partial class RunPipeline
{
    // Old verdicts, test results, overlays and merge authorization remain audit facts, never authority for a redirected attempt.
    internal static List<LedgerEntry> SinceRecoveryRestart(IReadOnlyList<LedgerEntry> history)
    {
        var start = 0;
        for (var i = 0; i < history.Count; i++)
            if (history[i].Step == Recovery.RecoveryRedirect.Started) start = i + 1;
        return history.Skip(start).ToList();
    }

    private async Task RestartRedirectedCodingAsync(Run run, CancellationToken ct)
    {
        if (run.Redirect is not { } redirect || run.Item.State == WorkState.Intake) return;
        var detail = JsonSerializer.Serialize(redirect);
        var history = await ledger.HistoryAsync(run.Item, ct);
        if (history.Any(e => e.Step == Recovery.RecoveryRedirect.Started && e.Detail == detail)) return;
        await InWorkerPhaseAsync(run.Item, "restart redirected coding", async () =>
        {
            await ThrowIfControlledAsync(run.Item, ct);
            var current = await recovery!.RedirectAsync(run.Item.ExternalId, ct);
            if (current != redirect) throw new Recovery.RecoveryContinuationDeferredException("Recovery disposition changed while waiting for a slot.");
            await ThrowIfControlledAsync(run.Item, ct);
            var attempt = CurrentWorkerAttempt(history);
            if (OrphanedWorkerPid(attempt) is { } pid)
                await worker.StopOrphanAsync(pid, ct);
            // PrepareAsync replaces the previous worktree as its own slot user. Never restore its old pushed branch.
            await ledger.RestartCodingAsync(run.Item, redirect, ct);
            await ledger.CheckpointAsync(run.Item, Recovery.RecoveryRedirect.Started, null, detail, ct);
        }, ct);
    }

    private const string RecoveryDelivered = "recovery-delivered";
    private const string RecoveryMergeAuthorized = "recovery-merge-authorized";
    private const string RecoveryPushedHead = "recovery-pushed-head";

    private async Task RecordCorrectionPushAsync(Run run, CancellationToken ct)
    {
        if (Naming == ItemNaming.Recovery)
            await ledger.CheckpointAsync(run.Item, RecoveryPushedHead, null, await workspaces.HeadAsync(run.Workspace!, ct), ct);
    }
    private sealed record CorrectionMerge(int Pull, string Base, string BaseSha, string Head);

    private async Task ValidateCorrectionPushAsync(Run run, CancellationToken ct)
    {
        await ThrowIfControlledAsync(run.Item, ct);
        var workspace = run.Workspace ?? throw new InvalidOperationException("Correction has no worktree to verify.");
        var pinned = recovery is null ? null : await recovery.CorrectionBaseShaAsync(run.Item.ExternalId, ct);
        if (pinned is null) throw ControlRequestedException.Deferred();
        var paths = await workspaces.CorrectionPathsAsync(workspace, pinned, ct);
        var authorized = await recovery!.AuthorizeCorrectionAsync(run.Item.ExternalId, run.Repo.FullName, ct,
            workspace.BaseBranch, pinned, paths);
        // Reads above may await network evidence. A human control arriving meanwhile still wins.
        await ThrowIfControlledAsync(run.Item, ct);
        if (!authorized) throw ControlRequestedException.Deferred();
    }

    private async Task ValidateCorrectionMergeAsync(Run run, PullFacts pull, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (Naming != ItemNaming.Recovery) return;
        // Called by the merge transport AFTER its write token is minted. These reads use their own read-only token.
        // GitHub binds only the head: a base/control/grant change after these last reads and before the PUT remains a race.
        var current = await Gate.GitHub.GetPullAsync(run.Repo, pull.Number, ct);
        if (!current.Open || current.HeadSha != pull.HeadSha)
            throw new GateBlockedException("Correction pull request changed before its authorized merge.");
        var authorized = recovery is not null && await recovery.AuthorizeCorrectionAsync(run.Item.ExternalId, run.Repo.FullName, ct,
            current.BaseRef, current.BaseSha, paths);
        await ThrowIfControlledAsync(run.Item, ct);
        if (!authorized) throw ControlRequestedException.Deferred();
        await ledger.CheckpointAsync(run.Item, RecoveryMergeAuthorized, null,
            JsonSerializer.Serialize(new CorrectionMerge(pull.Number, pull.BaseRef, pull.BaseSha, pull.HeadSha)), ct);
        await ThrowIfFrozenAsync(run.Item, ct, fresh: true);
    }

    // Observe, never execute, a merge whose authorized call was interrupted. The base moving because of our own merge
    // must not revoke its completed delivery or make a restart implement the same correction twice.
    private async Task ObserveCorrectionMergeAsync(WorkItem item, CancellationToken ct)
    {
        if (Naming != ItemNaming.Recovery || gate is null || item.State is not (WorkState.MergeGate or WorkState.Paused)) return;
        await using var held = await locks.TryAcquireAsync(item.Id, ct);
        if (held is null) return;
        await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
        var history = await ledger.HistoryAsync(item, ct);
        var context = TransitionContext.From(history.Where(e => e.Step is null).Select(e => e.State).ToList());
        if (item.State != WorkState.MergeGate && !(item.State == WorkState.Paused && context.PausedFrom == WorkState.MergeGate)) return;
        var authorized = history.LastOrDefault(e => e.Step == RecoveryMergeAuthorized);
        if (authorized?.Detail is null || LinkedPullRequestUrl(history) is not { } url) return;
        var expected = JsonSerializer.Deserialize<CorrectionMerge>(authorized.Detail)!;
        var (repo, number) = GitHubGate.ParsePullUrl(url);
        if (!repo.Is(RepoRef.Parse(item.Repo)) || number != expected.Pull) return;
        var pull = await Gate.GitHub.GetPullAsync(repo, number, ct);
        if (pull.BaseRef != expected.Base || pull.HeadSha != expected.Head || GatePassedAt(history, pull) is not { } commit
            || !await CorrectionMergeWasAuthorizedAsync(repo, history, pull, ct)) return;
        if (await _controls.EffectiveWithoutRepositoryAsync(item.ExternalId, item.EpicId, ct) != ControlState.Running) return;
        if (item.State == WorkState.Paused) await ledger.RecordAsync(item, WorkState.MergeGate, null, "unpaused", ct);
        await ledger.RecordAsync(item, WorkState.Merge, null, commit, ct);
    }

    private async Task<bool> CorrectionMergeWasAuthorizedAsync(RepoRef repo, IReadOnlyList<LedgerEntry> history, PullFacts pull, CancellationToken ct) =>
        Naming != ItemNaming.Recovery || history.LastOrDefault(e => e.Step == RecoveryMergeAuthorized)?.Detail is { } detail
            && JsonSerializer.Deserialize<CorrectionMerge>(detail) is { } expected
            && expected.Pull == pull.Number && expected.Base == pull.BaseRef && expected.Head == pull.HeadSha
            && await Gate.GitHub.IsGateMergeAsync(repo, pull, ct);

    private async Task RecordCorrectionDeliveryAsync(Run run, string? commit, CancellationToken ct)
    {
        if (Naming != ItemNaming.Recovery) return;
        var history = await ledger.HistoryAsync(run.Item, ct);
        if (history.Any(e => e.Step == RecoveryDelivered)) return;
        await ledger.CheckpointAsync(run.Item, RecoveryDelivered, null,
            $"{LinkedPullRequestUrl(history)} into {run.RequiredBase} at {commit}; awaiting fresh recovery assessment", ct);
    }
}
