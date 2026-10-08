using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>Where a PR's head stands against the current tip of its base branch (GitHub's compare, read fresh).</summary>
public sealed record BaseComparison(string BaseSha, int BehindBy)
{
    public bool UpToDate => BehindBy == 0;
}

/// <summary>
/// Merging the base branch into a PR branch in a worktree (<see cref="Git.IRepoWorkspace.MergeBaseAsync"/>): the base commit
/// merged, the worktree's HEAD after it (the merge commit; unchanged when it conflicted or was already up to date), and the
/// files left conflicted (the merge is then still in progress in the worktree).
/// </summary>
public sealed record BaseMerge(string BaseSha, string Head, IReadOnlyList<string> Conflicts, bool UpToDate = false)
{
    public bool Conflicted => Conflicts.Count > 0;
}

/// <summary>
/// A ledger checkpoint's JSON for the merge queue: a base update (<see cref="Kinds.Update"/>: the head <see cref="From"/> merged
/// with base <see cref="Base"/> as <see cref="To"/>), a conflict (<see cref="Kinds.Conflict"/>: <see cref="From"/> does not merge
/// with <see cref="Base"/> in <see cref="Files"/>), or the conflict fix round's own merge (<see cref="Kinds.FixMerge"/>).
/// </summary>
public sealed record BaseUpdate(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("files")] IReadOnlyList<string>? Files = null)
{
    public static class Kinds
    {
        public const string Update = "update";
        public const string Conflict = "conflict";
        public const string FixMerge = "fix-merge";
    }

    public string ToDetail() => JsonSerializer.Serialize(this);

    public static BaseUpdate? FromDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<BaseUpdate>(detail) is { Kind: not null, From: not null, Base: not null } u
                ? u with { Files = u.Files ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The proof that carries a verdict across a base update (E3): the PR's diff against the updated base at the new head is
/// byte-for-byte the diff that was reviewed at the old head (both read from GitHub against the same base commit, hashed here),
/// so the reviewed change is unchanged and the update only brought in the base's own commits — which CI on the new head and
/// the gate's fresh evaluation still judge.
/// </summary>
public sealed record ReviewCarry(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("reviewed_diff_sha256")] string ReviewedDiff,
    [property: JsonPropertyName("updated_diff_sha256")] string UpdatedDiff,
    [property: JsonPropertyName("reason")] string Reason)
{
    public string ToDetail() => JsonSerializer.Serialize(this);

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// The per-repo merge queue (sc-25384), derived from the ledger alone so it is durable, shared by every process and survives
/// restarts. An item is queued from its gate approval (a <c>queued</c> checkpoint in its current MergeGate stint: since its
/// last entry into MergeGate other than a return from Paused) until it leaves MergeGate (an interruption keeps it queued,
/// <see cref="Member"/>); the queue is FIFO by that checkpoint's row (gate-approval order). An item holds the repo's turn from
/// its <c>queue-turn</c> checkpoint until it leaves the queue or is paused other than by an interruption
/// (<see cref="InTurn"/>). One item per repo holds the turn: it is taken only by the queue's head, only when no queued item
/// holds it, and only under the repo's lock (<see cref="LockKey"/>), so two processes cannot both take it.
/// The caller leaves out of the queue every other item a control holds (Pause on the factory, its epic or the item, the usage
/// pause, or Stop) whose run is not active — such an item writes no ledger row until it runs again, so the ledger alone would
/// keep it queued and hold up its repo. Once its control lets it run again it is back at its approval-time place, but never
/// ahead of the turn: a turn taken while it was left out is the newer one (<see cref="TurnOf"/>), so it waits for that item.
/// </summary>
public static class MergeQueue
{
    /// <summary>
    /// One queued item: its gate-approval row (the queue order) and, when it holds the repo's turn, the row of its
    /// <c>queue-turn</c> checkpoint (null otherwise).
    /// </summary>
    public sealed record Entry(long ItemId, string ExternalId, long QueuedRow, long? TurnRow = null)
    {
        public bool InTurn => TurnRow is not null;
    }

    public enum TurnKind
    {
        /// <summary>The item already holds the turn (e.g. a resumed run): go on.</summary>
        Held,
        /// <summary>The item is the queue's head and nobody holds the turn: take it, then go on.</summary>
        Take,
        /// <summary>Another item holds the turn, or is ahead in the queue: wait.</summary>
        Wait,
    }

    /// <summary>What an item may do now; <see cref="Ahead"/> names the item it waits for.</summary>
    public sealed record Turn(TurnKind Kind, Entry? Ahead = null, int Position = 0, int Count = 0);

    /// <summary>The index of the row that started the item's current MergeGate stint, or -1 when it is not in one.</summary>
    public static int StintStart(IReadOnlyList<LedgerEntry> history)
    {
        var start = -1;
        WorkState? previous = null;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Step is not null)
            {
                continue;
            }
            var state = history[i].State;
            if (state == WorkState.MergeGate && previous != WorkState.Paused)
            {
                start = i;
            }
            else if (state is not WorkState.MergeGate and not WorkState.Paused)
            {
                start = -1;
            }
            previous = state;
        }
        return start;
    }

    /// <summary>The <c>queued</c> checkpoint of the current stint (the gate approval that queued the item), or null.</summary>
    public static LedgerEntry? QueuedRow(IReadOnlyList<LedgerEntry> history, string step)
    {
        var start = StintStart(history);
        return start < 0 ? null : history.Skip(start + 1).FirstOrDefault(e => e.Step == step);
    }

    /// <summary>
    /// Whether the item is in the ledger's queue now: in MergeGate, or paused from it in a way that resumes by itself — an
    /// interruption (<see cref="RunPipeline.Interrupted"/>: Ctrl-C or <c>factory work</c> shutting down), a user's Pause
    /// (<see cref="RunPipeline.UserPaused"/>, resumed by Continue) or the usage pause (<see cref="RunPipeline.UsagePaused"/>) —
    /// so it keeps its approval-time place. While a control still holds such an item and no run of it is active, the caller
    /// leaves it out (<see cref="RunPipeline"/>'s queue build), so it never holds up its repo. A parked item (paused other ways,
    /// or parked since, e.g. its story left the watch scope) never resumes by itself and is out of the queue.
    /// </summary>
    public static bool Member(IReadOnlyList<LedgerEntry> history)
    {
        if (StintStart(history) < 0)
        {
            return false;
        }
        var last = history.Count - 1;
        while (last >= 0 && history[last].Step is not null)
        {
            last--;
        }
        return last >= 0
            && (history[last].State == WorkState.MergeGate
                || (ResumablePause(history[last]) && !history.Skip(last + 1).Any(e => e.Step == RunPipeline.Steps.Parked)));
    }

    private static bool ResumablePause(LedgerEntry row) =>
        row.State == WorkState.Paused && row.Detail is RunPipeline.Interrupted or RunPipeline.UserPaused or RunPipeline.UsagePaused;

    private static bool IsInterruption(LedgerEntry row) => row.State == WorkState.Paused && row.Detail == RunPipeline.Interrupted;

    /// <summary>
    /// Whether the item holds its repo's turn: it is in the queue (<see cref="Member"/>) and took the turn (a <c>queue-turn</c>
    /// checkpoint) in its current stint, after any pause other than an interruption (which releases it: a resumed item takes
    /// it again). A crash or an interruption keeps it, so the item resumes its turn where the ledger left it.
    /// </summary>
    public static bool InTurn(IReadOnlyList<LedgerEntry> history, string step) => TurnRowOf(history, step) is not null;

    /// <summary>The row of the <c>queue-turn</c> checkpoint by which the item holds its repo's turn (<see cref="InTurn"/>), or null.</summary>
    public static long? TurnRowOf(IReadOnlyList<LedgerEntry> history, string step)
    {
        if (!Member(history))
        {
            return null;
        }
        var from = StintStart(history);
        for (var i = history.Count - 1; i > from; i--)
        {
            if (history[i] is { Step: null, State: WorkState.Paused } paused && !IsInterruption(paused))
            {
                from = i;
                break;
            }
        }
        return history.Skip(from + 1).LastOrDefault(e => e.Step == step)?.Id;
    }

    /// <summary>The item's queue entry, or null when it is not queued (not in the queue, <see cref="Member"/>, or not approved in this stint).</summary>
    public static Entry? EntryOf(WorkItem item, IReadOnlyList<LedgerEntry> history, string queuedStep, string turnStep) =>
        Member(history) && QueuedRow(history, queuedStep) is { } queued
            ? new Entry(item.Id, item.ExternalId, queued.Id, TurnRowOf(history, turnStep))
            : null;

    /// <summary>
    /// What <paramref name="self"/> may do in <paramref name="queue"/> (every queued item of its repo, itself included). When
    /// more than one entry holds a turn (one was taken while an earlier holder was left out of the queue by a control and idle),
    /// the newest turn is the repo's: the earlier holder waits for it, then holds its own turn again once that item has left.
    /// </summary>
    public static Turn TurnOf(long self, IReadOnlyList<Entry> queue)
    {
        var ordered = queue.OrderBy(e => e.QueuedRow).ToList();
        var position = ordered.FindIndex(e => e.ItemId == self) + 1;
        var mine = ordered.FirstOrDefault(e => e.ItemId == self);
        if (mine is null)
        {
            throw new InvalidOperationException($"Work item {self} is not in its repo's merge queue.");
        }
        if (ordered.Where(e => e.InTurn).MaxBy(e => e.TurnRow) is { } holder)
        {
            return holder.ItemId == self
                ? new Turn(TurnKind.Held, null, position, ordered.Count)
                : new Turn(TurnKind.Wait, holder, position, ordered.Count);
        }
        return ordered[0].ItemId == self
            ? new Turn(TurnKind.Take, null, position, ordered.Count)
            : new Turn(TurnKind.Wait, ordered[0], position, ordered.Count);
    }

    /// <summary>
    /// The repo's queue lock key in the run locks' key space: negative, so it never collides with a work item's id (the per-item
    /// run lock), and stable across processes (the first 8 bytes of SHA-256 of the lower-cased repo name).
    /// </summary>
    public static long LockKey(RepoRef repo)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"merge-queue:{repo.FullName.ToLowerInvariant()}"));
        var value = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        return value == 0 ? -1 : -value;
    }
}
