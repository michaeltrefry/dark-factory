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
/// Epics likewise (sc-25985; table <c>kanban_epics</c>): an epic's key is what the ledger's <see cref="WorkItem.EpicId"/> and the
/// scope <c>epic:kb-&lt;key&gt;</c> hold, in a number space of its own — an item and an epic may both have key 1.
/// </summary>
public sealed class KanbanKeys(IDbContextFactory<LedgerDbContext> contexts, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The item's key, allocated now if the factory has not seen the item before; its project (<paramref name="projectId"/>) is
    /// recorded with it, and again when the row has none or another (sc-25985: the dashboard's link is built from the ledger).
    /// </summary>
    public async Task<int> KeyAsync(Guid itemId, Guid projectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.KanbanItems.SingleOrDefaultAsync(i => i.ItemId == itemId, ct) is { } known)
        {
            if (known.ProjectId != projectId)
            {
                known.ProjectId = projectId;
                await db.SaveChangesAsync(ct);
            }
            return known.Id;
        }
        var row = new KanbanItem { ItemId = itemId, ProjectId = projectId, FirstSeenAt = _time.GetUtcNow() };
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

    /// <summary>
    /// The key of an epic (sc-25985), allocated now if the factory has not seen the epic before — so recorded before any work
    /// item or control names it (E4) — with the project it is in, recorded again when the row has another (as an item's is:
    /// the dashboard's epic link is built from the ledger).
    /// </summary>
    public async Task<int> EpicKeyAsync(Guid epicId, Guid projectId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.KanbanEpics.SingleOrDefaultAsync(e => e.EpicId == epicId, ct) is { } known)
        {
            if (known.ProjectId != projectId)
            {
                known.ProjectId = projectId;
                await db.SaveChangesAsync(ct);
            }
            return known.Id;
        }
        var row = new KanbanEpicKey { EpicId = epicId, ProjectId = projectId, FirstSeenAt = _time.GetUtcNow() };
        db.KanbanEpics.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (DbUpdateException)
        {
            // As for an item's key: another process may have allocated it meanwhile, and its key is the epic's.
            db.Entry(row).State = EntityState.Detached;
            if (await db.KanbanEpics.AsNoTracking().SingleOrDefaultAsync(e => e.EpicId == epicId, ct) is not { } winner)
            {
                throw;
            }
            return winner.Id;
        }
    }

    /// <summary>Whether the ledger gave this epic key out: an epic control on any other key would name no epic at all.</summary>
    public async Task<bool> KnowsEpicAsync(long key, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.KanbanEpics.AsNoTracking().AnyAsync(e => e.Id == key, ct);
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
    public string Item(KanbanWorkItem item) => Item(item.ProjectId, item.EpicId, item.Id);

    /// <summary>An item's link from its ids alone (the dashboard's, from the ledger): it opens the board of its project, or of its epic.</summary>
    public string Item(Guid projectId, Guid? epicId, Guid itemId) => $"{Board(projectId, epicId)}&item={itemId:D}";

    public string Epic(Guid projectId, Guid epicId) => Board(projectId, epicId);

    public string Document(Guid projectId, Guid epicId, Guid documentId) => $"{Board(projectId, epicId)}&document={documentId:D}";

    private string Board(Guid projectId, Guid? epicId) =>
        $"{webUrl.AbsoluteUri}?project={projectId:D}" + (epicId is { } epic ? $"&epic={epic:D}" : "");
}

/// <summary>
/// Links to Kanban items and their epics for the dashboard (sc-25985), from the ledger alone — the dashboard never asks a board:
/// an item's board id and project (<c>kanban_items</c>) and its epic's (<c>kanban_epics</c>, by <see cref="WorkItem.EpicId"/>),
/// through the one link builder (<see cref="KanbanLinks"/>). An item whose project the ledger has not recorded yet (a row from
/// before it was) gets no item link until it is next listed or read.
/// </summary>
public sealed class KanbanLedgerLinks(Uri webUrl) : IBoardLinks
{
    private readonly KanbanLinks _links = new(webUrl);

    public async Task<IReadOnlyDictionary<long, BoardLink>> OfAsync(LedgerDbContext db, IReadOnlyList<WorkItem> items, CancellationToken ct)
    {
        var mine = items.Where(i => i.Source == ItemNaming.Kanban.Source)
            .Select(i => (Item: i, Key: ItemNaming.Kanban.TryParse(i.ExternalId, out var key) ? key : 0))
            .ToList();
        if (mine.Count == 0)
        {
            return new Dictionary<long, BoardLink>();
        }
        var keys = mine.Select(m => m.Key).ToList();
        var epicKeys = mine.Select(m => m.Item.EpicId).OfType<long>().Distinct().ToList();
        var known = await db.KanbanItems.AsNoTracking().Where(k => keys.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
        var epics = (await db.KanbanEpics.AsNoTracking().Where(e => epicKeys.Contains(e.Id)).ToListAsync(ct)).ToDictionary(e => (long)e.Id);
        return mine.ToDictionary(m => m.Item.Id, m =>
        {
            var epic = m.Item.EpicId is { } e ? epics.GetValueOrDefault(e) : null;
            return new BoardLink(
                known.GetValueOrDefault(m.Key) is { ProjectId: { } project } row ? _links.Item(project, epic?.EpicId, row.ItemId) : null,
                epic is null ? null : _links.Epic(epic.ProjectId, epic.EpicId));
        });
    }
}
