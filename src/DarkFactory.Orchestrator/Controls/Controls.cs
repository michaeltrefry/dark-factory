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

/// <summary>Control scopes: <c>factory</c>, <c>epic:&lt;id&gt;</c>, <c>item:sc-&lt;id&gt;</c>.</summary>
public static class ControlScope
{
    public const string Factory = "factory";

    public static string Epic(long epicId) => $"epic:{epicId}";

    public static string Item(string externalId) => $"item:{externalId}";

    /// <summary>The story id of an item scope, or null for another scope.</summary>
    public static int? ItemStory(string scope) =>
        scope.StartsWith("item:", StringComparison.Ordinal) && StoryId.TryParse(scope["item:".Length..], out var id) ? id : null;

    /// <summary>The epic id of an epic scope, or null for another scope.</summary>
    public static long? EpicOf(string scope) =>
        scope.StartsWith("epic:", StringComparison.Ordinal) && long.TryParse(scope["epic:".Length..], out var id) && id > 0 ? id : null;

    /// <summary>Whether <paramref name="scope"/> is a well-formed scope.</summary>
    public static bool IsValid(string scope) => scope == Factory || ItemStory(scope) is not null || EpicOf(scope) is not null;
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
        var scopes = new List<string> { ControlScope.Factory, ControlScope.Item(externalId) };
        if (epicId is { } epic)
        {
            scopes.Add(ControlScope.Epic(epic));
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Controls.AsNoTracking().Where(c => scopes.Contains(c.Scope)).ToListAsync(ct);
        return rows.Any(r => r.State == ControlState.Stopping && r.Scope == ControlScope.Item(externalId)) ? ControlState.Stopping
            : rows.Any(r => r.State == ControlState.Paused) ? ControlState.Paused
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

    public async Task SetAsync(string scope, ControlState state, string by, CancellationToken ct)
    {
        if (!ControlScope.IsValid(scope))
        {
            throw new ArgumentException($"'{scope}' is not a control scope (factory, epic:<id> or item:sc-<id>).", nameof(scope));
        }
        if (state == ControlState.Stopping && ControlScope.ItemStory(scope) is null)
        {
            throw new ArgumentException("Only an item is stopped through its control; stop an epic or the factory item by item.", nameof(state));
        }
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
        }
        catch (DbUpdateException) when (db.Entry(row).State == EntityState.Added)
        {
            // Another writer inserted the scope first: write over theirs (last writer wins).
            await using var retry = await contexts.CreateDbContextAsync(ct);
            var existing = await retry.Controls.SingleAsync(c => c.Scope == scope, ct);
            (existing.State, existing.ChangedBy, existing.ChangedAt) = (state, by, time.GetUtcNow());
            await retry.SaveChangesAsync(ct);
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
