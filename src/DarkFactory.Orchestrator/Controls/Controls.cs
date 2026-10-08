using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
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

    /// <summary>Item scope only: the item is being stopped (worker killed, Cancelled, PR back to draft, story commented).</summary>
    Stopping,
}

/// <summary>Control scopes: <c>factory</c>, <c>usage</c>, <c>epic:&lt;id&gt;</c>, <c>item:sc-&lt;id&gt;</c>.</summary>
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

    public static string Epic(long epicId) => $"epic:{epicId}";

    public static string Item(string externalId) => $"item:{externalId}";

    /// <summary>The story id of an item scope, or null for another scope.</summary>
    public static int? ItemStory(string scope) =>
        scope.StartsWith("item:", StringComparison.Ordinal) && StoryId.TryParse(scope["item:".Length..], out var id) ? id : null;

    /// <summary>The epic id of an epic scope, or null for another scope.</summary>
    public static long? EpicOf(string scope) =>
        scope.StartsWith("epic:", StringComparison.Ordinal) && long.TryParse(scope["epic:".Length..], out var id) && id > 0 ? id : null;

    /// <summary>Whether <paramref name="scope"/> is a well-formed scope.</summary>
    public static bool IsValid(string scope) => scope == Factory || scope == Usage || ItemStory(scope) is not null || EpicOf(scope) is not null;
}

/// <summary>The factory-wide usage pause (<see cref="ControlScope.Usage"/>): its reasons and backoff.</summary>
public static class UsagePause
{
    /// <summary>The router reports every plan the factory can use exhausted (<c>GET /v1/subscriptions/usage</c>).</summary>
    public const string UsageExhausted = "usage-exhausted";

    /// <summary>A worker failed with the router's exhaustion or a rate-limit error (the backstop).</summary>
    public const string WorkerRateLimited = "worker-rate-limited";

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
    /// <see cref="ControlState.Paused"/> when the factory, the item's epic or the item is paused, else running.
    /// </summary>
    Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct);

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
    public async Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct)
    {
        var scopes = new List<string> { ControlScope.Factory, ControlScope.Usage, ControlScope.Item(externalId) };
        if (epicId is { } epic)
        {
            scopes.Add(ControlScope.Epic(epic));
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Controls.AsNoTracking().Where(c => scopes.Contains(c.Scope)).ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Any(r => r.State == ControlState.Stopping && r.Scope == ControlScope.Item(externalId)) ? ControlState.Stopping
            : rows.Any(r => r.PausesAt(now)) ? ControlState.Paused
            : ControlState.Running;
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

    public async Task SetAsync(string scope, ControlState state, string by, CancellationToken ct)
    {
        if (!ControlScope.IsValid(scope))
        {
            throw new ArgumentException($"'{scope}' is not a control scope (factory, usage, epic:<id> or item:sc-<id>).", nameof(scope));
        }
        if (state == ControlState.Stopping && ControlScope.ItemStory(scope) is null)
        {
            throw new ArgumentException("Only an item is stopped through its control; stop an epic or the factory item by item.", nameof(state));
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
