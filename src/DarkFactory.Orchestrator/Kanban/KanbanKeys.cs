using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator.Kanban;

/// <summary>
/// The ledger's keys of KanbanBoard items (sc-25981, E4; table <c>kanban_items</c>, like <c>github_issues</c>): the board names an
/// item by a GUID, the factory by <c>kb-&lt;key&gt;</c>. An item gets its key once, the first time the factory sees it, before
/// anything else is written for it, and keeps it for good: the same GUID always has the same key, across restarts and across
/// processes allocating at once (the table's unique index on the GUID decides a race; the loser reads the winner's key).
/// </summary>
public sealed class KanbanKeys(IDbContextFactory<LedgerDbContext> contexts, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The item's key, allocated now if the factory has not seen the item before.</summary>
    public async Task<int> KeyAsync(Guid itemId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.KanbanItems.AsNoTracking().SingleOrDefaultAsync(i => i.ItemId == itemId, ct) is { } known)
        {
            return known.Id;
        }
        var row = new KanbanItem { ItemId = itemId, FirstSeenAt = _time.GetUtcNow() };
        db.KanbanItems.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (DbUpdateException)
        {
            // Another process may have allocated it meanwhile (the unique index on the GUID): its key is the item's. A save that
            // failed for any other reason left no row, and that failure is what the caller must see.
            db.Entry(row).State = EntityState.Detached;
            if (await db.KanbanItems.AsNoTracking().SingleOrDefaultAsync(i => i.ItemId == itemId, ct) is not { } winner)
            {
                throw;
            }
            return winner.Id;
        }
    }

    /// <summary>The board id of the item with this key, or null for a key the ledger never gave out.</summary>
    public async Task<Guid?> ItemAsync(int key, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await db.KanbanItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == key, ct))?.ItemId;
    }

    /// <summary>
    /// Whether the ledger records that this factory claimed the item with this key: its work item (<c>kb-&lt;key&gt;</c>) has a
    /// <c>claimed</c> checkpoint, at any time. What tells the factory's own item whose board claim lapsed from one it never held.
    /// </summary>
    public async Task<bool> ClaimedAsync(int key, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (source, externalId) = (ItemNaming.Kanban.Source, ItemNaming.Kanban.Format(key));
        return await db.WorkItems.AsNoTracking().AnyAsync(
            w => w.Source == source && w.ExternalId == externalId && w.Entries.Any(e => e.Step == RunPipeline.Steps.Claimed), ct);
    }
}

/// <summary>
/// The KanbanBoard projects the factory watches (<c>Kanban:Watch:Projects</c>), by project key as the board stores it (upper case).
/// An item is in scope if its project is one of them. An empty scope watches nothing (fail safe), never the whole board.
/// </summary>
public sealed record KanbanScope(IReadOnlyList<string> Projects)
{
    public static readonly KanbanScope None = new([]);

    /// <summary>The longest key KanbanBoard gives a project.</summary>
    public const int MaxKeyLength = 24;

    public const string Setting = "Kanban:Watch:Projects";

    public bool IsEmpty => Projects.Count == 0;

    /// <summary>
    /// Reads <c>Kanban:Watch:Projects</c>, a comma-separated value or an array section (<c>Kanban__Watch__Projects__0</c>): each
    /// entry a project key — at most <see cref="MaxKeyLength"/> characters, none of them white space or a control character —
    /// upper-cased, once each. Anything else throws, naming the setting.
    /// </summary>
    public static KanbanScope From(IConfiguration config)
    {
        var section = config.GetSection(Setting);
        var raw = section.Value is { } single ? single.Split(',') : section.GetChildren().Select(c => c.Value ?? "");
        return new KanbanScope(raw.Select(v => v.Trim()).Where(v => v.Length > 0)
            .Select(v => v.Length <= MaxKeyLength && !v.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                ? v.ToUpperInvariant()
                : throw new InvalidOperationException(
                    $"{Setting}: '{v}' is not a project key (at most {MaxKeyLength} characters, without spaces)."))
            .Distinct(StringComparer.Ordinal)
            .ToList());
    }
}

/// <summary>
/// Links into KanbanBoard's web UI, built on <c>Kanban:WebUrl</c> (sc-25981): the board's MCP returns no URL. The web UI opens a
/// project's board, or one epic of it, from the query (<c>?project=&lt;id&gt;&amp;epic=&lt;id&gt;</c>); it has no page of one item or
/// document, so their links open the board they are on and carry the id (<c>item</c>, <c>document</c>) only to tell them apart.
/// </summary>
public sealed class KanbanLinks(Uri webUrl)
{
    public string Item(KanbanWorkItem item) => $"{Board(item.ProjectId, item.EpicId)}&item={item.Id:D}";

    public string Epic(Guid projectId, Guid epicId) => Board(projectId, epicId);

    public string Document(Guid projectId, Guid epicId, Guid documentId) => $"{Board(projectId, epicId)}&document={documentId:D}";

    private string Board(Guid projectId, Guid? epicId) =>
        $"{webUrl.AbsoluteUri}?project={projectId:D}" + (epicId is { } epic ? $"&epic={epic:D}" : "");
}
