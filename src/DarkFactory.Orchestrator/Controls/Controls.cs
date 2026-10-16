using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Controls;

/// <summary>A scope's control. Stored by name, so members may be added but not renamed.</summary>
public enum ControlState
{
    /// <summary>Work in the scope runs (a Continue leaves this, with who/when).</summary>
    Running,

    /// <summary>
    /// Nothing new in the scope is claimed or dispatched; a running worker stops at its next tool boundary,
    /// keeping its Claude session and worktree for Continue.
    /// </summary>
    Paused,

    /// <summary>
    /// Item scope only: the item is being stopped (worker killed, Cancelled, PR back to draft, story commented). On a plan item
    /// (sc-25876) its unfinished stories are being stopped with it.
    /// </summary>
    Stopping,
}

/// <summary>
/// Control scopes: <c>factory</c>, <c>usage</c>, <c>freeze</c>, an epic's — <c>epic:&lt;id&gt;</c> for a Shortcut epic, <c>epic:kb-&lt;key&gt;</c>
/// for a Kanban epic (sc-25985) — a human repository scope <c>repo:owner/name</c>, or an item's:
/// <c>item:sc-&lt;id&gt;</c>, <c>item:gh-&lt;key&gt;</c>, <c>item:kb-&lt;key&gt;</c>.
/// </summary>
public static class ControlScope
{
    public const string Factory = "factory";

    /// <summary>
    /// The factory-wide usage pause (<see cref="UsagePause"/>): set when the router's plans are exhausted, never by a
    /// user's Pause, and lifted by its own <see cref="Control.ResumeAt"/>. Kept apart from <see cref="Factory"/> so a
    /// user's Continue does not lift it early and its resume does not lift a user's pause; only an explicit
    /// <c>factory continue --usage</c> (or the dashboard's Continue on it) forces it off.
    /// </summary>
    public const string Usage = "usage";

    /// <summary>
    /// The factory-wide automatic freeze (<see cref="FactoryFreeze"/>): set by the freeze evaluator when a trigger holds, never
    /// by a user's Pause, and cleared only by a human's Continue on it (<c>factory continue --freeze</c> or the dashboard). Kept
    /// apart from <see cref="Factory"/> so a user's Continue of a Pause does not clear a freeze, nor the reverse.
    /// </summary>
    public const string Freeze = "freeze";

    /// <summary>What an epic scope starts with, before the epic (<see cref="EpicRef.ToString"/>).</summary>
    public const string EpicPrefix = "epic:";

    /// <summary>
    /// An epic's scope. It carries the epic's source (sc-25985): <c>epic:12</c> is Shortcut epic 12 — as it always was, so rows
    /// written before Kanban epics had scopes still apply to Shortcut items, and only to them — and <c>epic:kb-12</c> the Kanban
    /// epic with ledger key 12.
    /// </summary>
    public static string Epic(EpicRef epic) => EpicPrefix + epic;

    /// <summary>What an item scope starts with, before the item's external id.</summary>
    public const string ItemPrefix = "item:";

    public static string Item(string externalId) => ItemPrefix + externalId;

    /// <summary>The item of an item scope (<c>item:sc-12</c>, <c>item:gh-3</c>, <c>item:kb-4</c>), or null for another scope.</summary>
    public static ItemRef? ItemOf(string scope) =>
        scope.StartsWith("item:", StringComparison.Ordinal) ? ItemNaming.ParseAny(scope["item:".Length..]) : null;

    /// <summary>
    /// The epic of an epic scope exactly as <see cref="Epic"/> writes it, or null for anything else (another scope; <c>epic:KB-3</c>
    /// or <c>epic:012</c>, which no item's control would ever read).
    /// </summary>
    public static EpicRef? EpicOf(string scope) =>
        scope.StartsWith(EpicPrefix, StringComparison.Ordinal) && ItemNaming.ParseEpic(scope[EpicPrefix.Length..]) is { } epic && Epic(epic) == scope
            ? epic
            : null;

    /// <summary>A human repository control, separate from automatic recovery incidents.</summary>
    public static string Repository(RepoRef repo) => "repo:" + repo.Key;

    public static RepoRef? RepositoryOf(string scope)
    {
        if (!scope.StartsWith("repo:", StringComparison.Ordinal)) return null;
        var name = scope[5..];
        // Scope keys must be canonical and usable in every consumer, not merely splittable by RepoRef.Parse.
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z0-9_-][a-z0-9_.-]*/[a-z0-9_-][a-z0-9_.-]*$")) return null;
        var repo = RepoRef.Parse(name);
        return Repository(repo) == scope ? repo : null;
    }

    /// <summary>Whether <paramref name="scope"/> is a well-formed scope.</summary>
    public static bool IsValid(string scope) => scope == Factory || scope == Usage || scope == Freeze || ItemOf(scope) is not null || EpicOf(scope) is not null || RepositoryOf(scope) is not null;
}

/// <summary>The factory-wide usage pause (<see cref="ControlScope.Usage"/>): its reasons and backoff.</summary>
public static class UsagePause
{
    /// <summary>The router reports every plan the factory can use exhausted (<c>GET /v1/subscriptions/usage</c>).</summary>
    public const string UsageExhausted = "usage-exhausted";

    /// <summary>A worker failed with the router's exhaustion or a rate-limit error (the backstop).</summary>
    public const string WorkerRateLimited = "worker-rate-limited";

    /// <summary>The router refused a worker session with <c>model_class_unavailable</c>: no model of its class could serve it (E8).</summary>
    public const string WorkerModelClassUnavailable = "worker-model-class-unavailable";

    /// <summary>The router refused a reviewer call with its exhaustion or a rate-limit error.</summary>
    public const string ReviewerRateLimited = "reviewer-rate-limited";

    /// <summary>Who writes usage pauses (<see cref="Control.ChangedBy"/>).</summary>
    public const string By = "usage";

    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(60);
}

/// <summary>The control state every process shares (the ledger's <c>controls</c> table).</summary>
public interface IControls
{
    /// <summary>
    /// What applies to one item: <see cref="ControlState.Stopping"/> when the item is being stopped, else
    /// <see cref="ControlState.Paused"/> when the factory, the item's epic or the item is paused, else running. The epic is
    /// <paramref name="epicId"/> in the item's own source (<see cref="ItemNaming.EpicOf"/>, sc-25985).
    /// A story of a plan (sc-25876, <see cref="WorkItem.PlanItemId"/>) is held to its plan item's control as well — the plan's own
    /// <c>item:&lt;plan&gt;</c> scope, so a Pause, Continue or Stop on a plan item reaches its stories with no scope kind of its own:
    /// paused while the plan item is, and being stopped while the plan item is unless the factory already merged it.
    /// </summary>
    Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct);

    /// <summary>Human/factory/usage/plan controls before the fresh spec resolves the operation repository.</summary>
    Task<ControlState> EffectiveWithoutRepositoryAsync(string externalId, long? epicId, CancellationToken ct) =>
        EffectiveAsync(externalId, epicId, ct);

    /// <summary>
    /// Composes the item's human/global/plan controls with the repository this operation targets. A waiting multi-repository
    /// plan uses this per child/pass, never its primary repository to hold unrelated children. Human item/epic/plan and global
    /// controls still apply. No incident grant or manual-control bypass is implied.
    /// </summary>
    async Task<ControlState> EffectiveInRepositoryAsync(string externalId, long? epicId, string repository, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var state = await EffectiveAsync(externalId, epicId, ct);
        if (state != ControlState.Running) return state;
        return (await GetAsync(ControlScope.Repository(RepoRef.Parse(repository)), ct)) is { State: ControlState.Paused }
            ? ControlState.Paused : state;
    }

    /// <summary>Every scope's control row.</summary>
    Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct);

    /// <summary>The usage pause while it is in effect (before its <see cref="Control.ResumeAt"/>), or null.</summary>
    Task<Control?> UsagePauseAsync(CancellationToken ct);

    /// <summary>
    /// Pauses the factory for usage until <paramref name="resumeAt"/> (never shortening a pause in effect), or, with no
    /// reset time known, for a backoff: <see cref="UsagePause.InitialBackoff"/>, doubled (up to
    /// <see cref="UsagePause.MaxBackoff"/>) when the last backoff pause ended less than <see cref="UsagePause.MaxBackoff"/>
    /// ago. A backoff pause in effect is left as it is. A pause a user lifted early (<c>continue --usage</c>) is not set
    /// again by a reset no later than the one it was lifted from; a later reset or a backoff (the worker backstop) sets it.
    /// Returns the control row as it now stands.
    /// </summary>
    Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct);

    /// <summary>
    /// Freezes the factory (<see cref="ControlScope.Freeze"/>) for <paramref name="trigger"/>, unless it is frozen already or its
    /// row changed since the evaluator read it (<paramref name="readChangedAt"/>: that row's <see cref="Control.ChangedAt"/>, null
    /// for no row) — a human's Continue that landed meanwhile is never overwritten; the next evaluation decides again. Returns
    /// the row as it now stands.
    /// </summary>
    Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct);

    /// <summary>The scope's control row, or null when it has none.</summary>
    Task<Control?> GetAsync(string scope, CancellationToken ct);

    Task SetAsync(string scope, ControlState state, string by, CancellationToken ct);

    /// <summary>Removes the scope's row (a stopped item's, once it is Cancelled).</summary>
    Task ClearAsync(string scope, CancellationToken ct);
}

/// <summary>No controls: everything runs (tests and hosts without a ledger-backed control table).</summary>
public sealed class NoControls : IControls
{
    public static readonly NoControls Instance = new();

    public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct) => Task.FromResult(ControlState.Running);
    public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Control>>([]);
    public Task<Control?> UsagePauseAsync(CancellationToken ct) => Task.FromResult<Control?>(null);

    /// <summary>Records nothing: without a control table nothing could honour the pause.</summary>
    public Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct) =>
        Task.FromResult(new Control { Scope = ControlScope.Usage, State = ControlState.Running, ChangedBy = UsagePause.By, Reason = reason });
    /// <summary>Records nothing: without a control table nothing could honour the freeze.</summary>
    public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) =>
        Task.FromResult(new Control { Scope = ControlScope.Freeze, State = ControlState.Running, ChangedBy = FreezeTrigger.By, Reason = trigger, Detail = detail });
    public Task<Control?> GetAsync(string scope, CancellationToken ct) => Task.FromResult<Control?>(null);
    public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => Task.CompletedTask;
    public Task ClearAsync(string scope, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Controls in the ledger. Each call uses its own short-lived context, so a pipeline's watcher can read them
/// beside the pipeline's own context. In Postgres a trigger NOTIFYs <c>work_items</c> on every write, so the
/// dashboard redraws at once; pipelines poll (<see cref="RunPipeline"/>).
/// </summary>
public sealed class LedgerControls(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time) : IControls
{
    public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct) =>
        EffectiveCoreAsync(externalId, epicId, null, resolveRepository: true, ct);

    public Task<ControlState> EffectiveWithoutRepositoryAsync(string externalId, long? epicId, CancellationToken ct) =>
        EffectiveCoreAsync(externalId, epicId, null, resolveRepository: false, ct);

    public Task<ControlState> EffectiveInRepositoryAsync(string externalId, long? epicId, string repository, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return EffectiveCoreAsync(externalId, epicId, repository, resolveRepository: false, ct);
    }

    private async Task<ControlState> EffectiveCoreAsync(string externalId, long? epicId, string? repository, bool resolveRepository, CancellationToken ct)
    {
        var own = ControlScope.Item(externalId);
        var scopes = new List<string> { ControlScope.Factory, ControlScope.Usage, ControlScope.Freeze, own };
        var repoKey = repository is null ? null : RepoRef.Parse(repository).Key;
        if (repoKey is not null) scopes.Add(ControlScope.Repository(RepoRef.Parse(repoKey)));
        // The epic of the item's own source (sc-25985): a Kanban item is held to `epic:kb-<key>`, never to the Shortcut epic whose
        // number its epic's key happens to be. One more scope in the one query.
        if (ItemNaming.EpicOf(externalId, epicId) is { } epic)
        {
            scopes.Add(ControlScope.Epic(epic));
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        // A story of a plan (sc-25876) is also held to the control on its plan item: no new scope kind, the plan's own item scope,
        // found by a self-join on the ledger's (Source, ExternalId) index inside the one query that reads the rows — this runs every
        // second for every running worker and test run. No part of it is optional: a read that fails throws, and the caller counts
        // an unreadable control as a Pause (E2), never as "no plan".
        var source = ItemNaming.ParseAny(externalId)?.Naming.Source;
        var planScopes = db.WorkItems.Where(s => s.Source == source && s.ExternalId == externalId && s.PlanItemId != null)
            .Join(db.WorkItems, s => s.PlanItemId, p => (long?)p.Id, (s, p) => ControlScope.ItemPrefix + p.ExternalId);
        // Resolve repository scope inside the same query as human/plan controls. Planned coordinates children separately;
        // during a gate pass WorkItem.Repo names that pass. No extra per-second repository or incident round trip.
        var repositories = db.WorkItems.Where(i => i.Source == source && i.ExternalId == externalId && i.State != WorkState.Planned)
            .Select(i => i.Repo.ToLower());
        var repositoryScopes = repositories.Select(r => "repo:" + r);
        var manual = db.Controls.AsNoTracking().Where(c => scopes.Contains(c.Scope) || planScopes.Contains(c.Scope)
                || (resolveRepository && repositoryScopes.Contains(c.Scope)))
            .Select(c => new { c.Scope, Stopping = c.State == ControlState.Stopping, Paused = c.State == ControlState.Paused, c.ResumeAt });
        var automatic = db.HotFileIncidents.AsNoTracking().Where(i => i.State != "Resolved"
                && (resolveRepository ? repositories.Contains(i.Repo) : repoKey != null && i.Repo == repoKey))
            .Select(i => new { Scope = "recovery", Stopping = false, Paused = true, ResumeAt = (DateTimeOffset?)null });
        // These are read-only effective reasons, never persisted control rows and never a synthetic Running authorization.
        var rows = (await manual.Concat(automatic).ToListAsync(ct))
            .Select(r => new Control { Scope = r.Scope, State = r.Stopping ? ControlState.Stopping : r.Paused ? ControlState.Paused : ControlState.Running,
                ResumeAt = r.ResumeAt, ChangedBy = "" }).ToList();
        var now = time.GetUtcNow();
        if (rows.Any(r => r.State == ControlState.Stopping && r.Scope == own))
        {
            return ControlState.Stopping;
        }
        // A plan being stopped (a Stopping row that is none of the item's own scopes is its plan's) stops its unfinished stories;
        // one the factory already merged is left as it is. Asked only then.
        if (rows.Any(r => r.State == ControlState.Stopping && !scopes.Contains(r.Scope))
            && !await db.LedgerEntries.AsNoTracking().AnyAsync(e => e.Step == null && e.State == WorkState.Merge
                && db.WorkItems.Any(i => i.Id == e.WorkItemId && i.Source == source && i.ExternalId == externalId), ct))
        {
            return ControlState.Stopping;
        }
        // Stop wins even during recovery. Safe resolution changes only incident rows; it never replaces human controls.
        if (rows.Any(r => r.PausesAt(now))) return ControlState.Paused;
        return ControlState.Running;
    }

    public async Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Controls.AsNoTracking().OrderBy(c => c.Scope).ToListAsync(ct);
    }

    public async Task<Control?> GetAsync(string scope, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Controls.AsNoTracking().SingleOrDefaultAsync(c => c.Scope == scope, ct);
    }

    public async Task<Control?> UsagePauseAsync(CancellationToken ct) =>
        await GetAsync(ControlScope.Usage, ct) is { } row && row.PausesAt(time.GetUtcNow()) ? row : null;

    /// <summary>Retries of a control write that lost a race to another writer (each retry re-reads the row and decides again).</summary>
    private const int MaxWriteAttempts = 5;

    public async Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var row = await db.Controls.SingleOrDefaultAsync(c => c.Scope == ControlScope.Usage, ct);
            var now = time.GetUtcNow();
            var inEffect = row is not null && row.PausesAt(now);
            var until = resumeAt;
            TimeSpan? backoff = null;
            if (until is null)
            {
                if (inEffect)
                {
                    return row!;
                }
                backoff = row is { Backoff: { } last, ResumeAt: { } ended } && ended > now - UsagePause.MaxBackoff
                    ? TimeSpan.FromTicks(Math.Min(last.Ticks * 2, UsagePause.MaxBackoff.Ticks))
                    : UsagePause.InitialBackoff;
                until = now + backoff;
            }
            else if (inEffect && row!.ResumeAt >= until)
            {
                return row;
            }
            else if (row is { State: ControlState.Running, ResumeAt: { } lifted } && row.ChangedBy != UsagePause.By && until <= lifted)
            {
                // A user lifted this pause early (`factory continue --usage`): the reading that set it does not set it
                // again. A later reset does, and so does the worker backstop (no reset time), which proves the limit holds.
                return row;
            }
            if (row is null)
            {
                row = new Control { Scope = ControlScope.Usage, ChangedBy = UsagePause.By };
                db.Controls.Add(row);
            }
            (row.State, row.ChangedBy, row.ChangedAt, row.Reason, row.ResumeAt, row.Backoff) =
                (ControlState.Paused, UsagePause.By, now, reason, until, backoff);
            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException ex) when (attempt < MaxWriteAttempts && (ex is DbUpdateConcurrencyException || db.Entry(row).State == EntityState.Added))
            {
                // Another writer got there first (inserted the row, or changed it since it was read): decide again
                // against its row, so a backoff never shortens a known reset it raced with.
            }
        }
    }

    public async Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var row = await db.Controls.SingleOrDefaultAsync(c => c.Scope == ControlScope.Freeze, ct);
            if (row is not null && (row.State != ControlState.Running || row.ChangedAt != readChangedAt))
            {
                return row; // frozen already, or a human's Continue landed since the evaluator read the row
            }
            if (row is null)
            {
                if (readChangedAt is not null)
                {
                    // The row the evaluator read is gone: decide again on the next evaluation.
                    return new Control { Scope = ControlScope.Freeze, State = ControlState.Running, ChangedBy = FreezeTrigger.By };
                }
                row = new Control { Scope = ControlScope.Freeze, ChangedBy = FreezeTrigger.By };
                db.Controls.Add(row);
            }
            (row.State, row.ChangedBy, row.ChangedAt, row.Reason, row.Detail, row.ResumeAt, row.Backoff) =
                (ControlState.Paused, FreezeTrigger.By, time.GetUtcNow(), trigger, detail, null, null);
            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException ex) when (attempt < MaxWriteAttempts && (ex is DbUpdateConcurrencyException || db.Entry(row).State == EntityState.Added))
            {
                // Another writer got there first (another process froze, or a human continued): decide again against its row.
            }
        }
    }

    public async Task SetAsync(string scope, ControlState state, string by, CancellationToken ct)
    {
        if (!ControlScope.IsValid(scope))
        {
            throw new ArgumentException($"'{scope}' is not a control scope (factory, usage, freeze, repo:<owner/name>, epic:<id>, epic:kb-<n>, item:sc-<id>, item:gh-<key> or item:kb-<n>).", nameof(scope));
        }
        if (state == ControlState.Stopping && ControlScope.ItemOf(scope) is null)
        {
            throw new ArgumentException("Only an item is stopped through its control; stop a repository, epic or the factory item by item.", nameof(state));
        }
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var row = await db.Controls.SingleOrDefaultAsync(c => c.Scope == scope, ct);
            if (row is null)
            {
                row = new Control { Scope = scope, ChangedBy = by };
                db.Controls.Add(row);
            }
            (row.State, row.ChangedBy, row.ChangedAt) = (state, by, time.GetUtcNow());
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxWriteAttempts && (ex is DbUpdateConcurrencyException || db.Entry(row).State == EntityState.Added))
            {
                // Another writer inserted or changed the scope first: write over theirs (last writer wins).
            }
        }
    }

    public async Task ClearAsync(string scope, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Controls.SingleOrDefaultAsync(c => c.Scope == scope, ct) is { } row)
        {
            db.Controls.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }
}
