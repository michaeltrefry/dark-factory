using System.Security.Cryptography;
using System.Text;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Kanban;

/// <summary>
/// KanbanBoard as a work source (sc-25981), behind <see cref="IWorkSource"/> like the Shortcut board. An item is a work item of a
/// watched project (<see cref="KanbanScope"/>); its id is the key the ledger gave its GUID (<c>kb-&lt;key&gt;</c>,
/// <see cref="KanbanKeys"/>). Ready = in the <c>Ready</c> column, every blocker <c>Done</c>, and no live claim. The board is read
/// and written only through its MCP by the orchestrator (<see cref="KanbanBoard"/>, with the orchestrator's own full-scope token;
/// E2), never by a model session. A Kanban item is written on the owner's board and is trusted like a Shortcut story (E5): its
/// name and description are instructions, unfenced.
/// Writes (E3, E4): the claim is KanbanBoard's own (<c>claim_work_item</c>, advisory and expiring) under a marker derived from the
/// item's board id (<see cref="Marker"/>); a status change is conditional on the version just read; a comment carries an
/// idempotency key derived from the item's board id, its version and the text (<see cref="CommentKey"/>); a link the item already
/// carries changes nothing. No key written onto the board derives from a ledger key. What this source does not do yet: renew a
/// claim (it claims for <see cref="ClaimTtlSeconds"/>; one that lapsed on an item the ledger records as claimed by this factory
/// is taken again by <see cref="ClaimAsync"/>), retry a <c>busy</c> answer or re-read after a <c>conflict</c> (the call
/// throws <see cref="KanbanException"/> and the run fails that step), and plans (it creates no children).
/// </summary>
public sealed class KanbanWorkSource(KanbanBoard board, KanbanKeys keys, KanbanScope scope, Uri webUrl) : IWorkSource
{
    /// <summary>
    /// How long a claim lasts: the longest KanbanBoard allows (a day), since nothing renews it yet. An item whose run died
    /// between the claim and the move to <c>InProgress</c> stays claimed that long.
    /// </summary>
    public const int ClaimTtlSeconds = 86400;

    /// <summary>The most pages of one project's ready items read in one listing (a board that answers a cursor forever is refused).</summary>
    public const int MaxPages = 50;

    /// <summary>
    /// The default <see cref="PageSize"/>: the contract's own default, not the board's maximum
    /// (<see cref="KanbanBoard.MaxPageSize"/>) — an MCP answer over 1 MiB is refused by the client (<c>McpHttpClient.MaxAnswerBytes</c>),
    /// and <see cref="MaxPages"/> pages of it still cover 2,500 ready items of one project.
    /// </summary>
    public const int DefaultPageSize = 50;

    /// <summary>Page size of the ready listing.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    private readonly KanbanLinks _links = new(webUrl);

    public ItemNaming Naming => ItemNaming.Kanban;

    public string ScopeReturnHint => $"Add its project back to {KanbanScope.Setting} and move it to Ready";

    /// <summary>
    /// The claim marker of an item: derived from its board id (E4), so every run and every process of the factory claims it
    /// under the same marker (a claim by the same identity and marker is the factory's own and resets the expiry).
    /// </summary>
    public static string Marker(Guid itemId) => $"dark-factory:{itemId:D}";

    /// <summary>
    /// The idempotency key of a comment (E3, E4): the SHA-256 of the item's board id, its version when the comment is written
    /// and the body. A retry of the same comment — after a crash or a failed answer — has the same key and adds nothing; the same
    /// words said again after the item was moved or edited (its version went up) are a new comment. Both halves are limits, since
    /// the version moves only on an update or a move (a comment, a link and a claim leave it): (1) the same words said again on
    /// purpose while the version is unchanged are dropped by the board — it answers the earlier comment — while the caller counts
    /// them as posted; (2) a retry of one comment after the version moved in between posts it a second time. Intended: a key
    /// written onto the board derives from board ids only, never from a ledger key or a counter of the factory's.
    /// </summary>
    public static string CommentKey(Guid itemId, long version, string body) =>
        $"dark-factory:comment:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{itemId:D}\n{version}\n{body}")))}";

    /// <summary>
    /// The kind of an external link, from its URL: <c>pr</c> for a pull request (<c>…/pull/&lt;n&gt;</c>), <c>branch</c> for a
    /// branch (<c>…/tree/&lt;name&gt;</c>), else <c>other</c>.
    /// </summary>
    public static string LinkKind(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "other";
        }
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path switch
        {
            [_, _, "pull", var number, ..] when number.All(char.IsAsciiDigit) => "pr",
            [_, _, "tree", _, ..] => "branch",
            _ => "other",
        };
    }

    /// <summary>
    /// The key of an item the factory has not listed yet (<c>factory kanban key</c>, and the live tests): the item is read from the
    /// board first — an id the board does not know throws (<see cref="KanbanException.NotFound"/>) and gets no key — and nothing
    /// is written to the board.
    /// </summary>
    public async Task<int> KeyOfAsync(Guid itemId, CancellationToken ct) => await keys.KeyAsync((await board.GetItemAsync(itemId, ct)).Id, ct);

    /// <summary>
    /// The ready items of every watched project, in the projects' configured order and oldest first within one, each given its
    /// key before anything else is written for it. Listed by the board (<c>list_work_items</c>, paged, comments left out) and
    /// checked again here, so nothing outside the scope, blocked, or claimed slips through.
    /// </summary>
    public async Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct)
    {
        if (scope.IsEmpty)
        {
            return [];
        }
        var ready = new List<int>();
        foreach (var project in await WatchedAsync(ct))
        {
            string? cursor = null;
            for (var page = 0; ; page++)
            {
                if (page == MaxPages)
                {
                    throw new InvalidOperationException(
                        $"Kanban project {project.Key} lists more than {MaxPages} pages of ready items; not reading on.");
                }
                var answer = await board.ListReadyAsync(project.Id, PageSize, cursor, ct);
                foreach (var item in answer.Items.Where(i => i.ProjectId == project.Id && i.Status == KanbanStatus.Ready
                    && i.ClaimMarker is null && i.OpenBlockers.Count == 0))
                {
                    var key = await keys.KeyAsync(item.Id, ct);
                    if (!ready.Contains(key))
                    {
                        ready.Add(key);
                    }
                }
                cursor = answer.NextCursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }
            }
        }
        return ready;
    }

    /// <summary>
    /// Claims a ready item: refuses, writing nothing, a key the ledger never gave out, an item gone from the board, one outside
    /// the watched projects (unless <paramref name="ignoreScope"/>), one another caller holds, one that is not <c>Ready</c> (or
    /// already the factory's and <c>InProgress</c>), and one a blocker still blocks. Else it claims the item
    /// (<c>claim_work_item</c>; another caller winning the race is a refusal too), reads the claim back, and moves the item to
    /// <c>InProgress</c>, on the version just read.
    /// "Already the factory's and <c>InProgress</c>" is an item carrying the factory's live claim, or — a claim lasts
    /// <see cref="ClaimTtlSeconds"/> and nothing renews it — one with no claim at all that the ledger records as claimed by this
    /// factory (<see cref="KanbanKeys.ClaimedAsync"/>): its claim lapsed and is taken again, so an escalated item can be re-run
    /// after a day. An <c>InProgress</c> item with no claim that the ledger does not record as the factory's is refused
    /// (<see cref="LapsedRefusal"/>): it may be a person's work.
    /// </summary>
    public async Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct)
    {
        if (await keys.ItemAsync(id, ct) is not { } itemId)
        {
            return ClaimResult.Refused($"{Naming.Format(id)} is no Kanban item the factory knows");
        }
        if (await FindAsync(itemId, ct) is not { } item)
        {
            return ClaimResult.Refused("the item is no longer on the board");
        }
        if (!ignoreScope && !await WatchesAsync(item.ProjectId, ct))
        {
            return ClaimResult.Refused($"the item's project is outside the watched projects ({KanbanScope.Setting})");
        }
        var marker = Marker(itemId);
        if (item.ClaimMarker is { } held && held != marker)
        {
            return ClaimResult.Refused($"the item is claimed by {item.Claimant ?? "another caller"}");
        }
        if (item.Status == KanbanStatus.InProgress && item.ClaimMarker is null)
        {
            // No live claim: ours only if the ledger says this factory claimed it (the claim lapsed) — then it is claimed again below.
            if (!await keys.ClaimedAsync(id, ct))
            {
                return ClaimResult.Refused(LapsedRefusal);
            }
        }
        else if (item.Status != KanbanStatus.Ready && !(item.Status == KanbanStatus.InProgress && item.ClaimMarker == marker))
        {
            return ClaimResult.Refused($"the item is {item.Status}, not {KanbanStatus.Ready}");
        }
        if (item.Status == KanbanStatus.Ready && item.OpenBlockers is { Count: > 0 } blockers)
        {
            return ClaimResult.Refused($"the item is blocked by {string.Join(", ", blockers.Select(b => $"\"{b.Title}\" ({b.Status})"))}");
        }

        KanbanWorkItem claimed;
        try
        {
            claimed = await board.ClaimAsync(itemId, marker, ClaimTtlSeconds, ct);
        }
        catch (KanbanException ex) when (ex.Code == KanbanException.AlreadyClaimed)
        {
            return ClaimResult.Refused("the item is claimed by another caller");
        }
        var after = await board.GetItemAsync(itemId, ct);
        if (after.ClaimMarker != marker || after.ClaimantId is null || after.ClaimantId != claimed.ClaimantId)
        {
            return ClaimResult.Refused(
                $"the claim did not stick (read back claimant {after.Claimant ?? "none"}, marker {after.ClaimMarker ?? "none"})");
        }
        if (after.Status == KanbanStatus.Ready)
        {
            after = await board.SetStatusAsync(itemId, KanbanStatus.InProgress, after.Version, ct);
        }
        if (after.Status != KanbanStatus.InProgress)
        {
            // Moved by someone else between the read and the claim: it is theirs to place, so the claim is given back.
            await ReleaseOursAsync(after, ct);
            return ClaimResult.Refused($"the item was moved to {after.Status} while it was being claimed");
        }
        return ClaimResult.Ok;
    }

    /// <summary>
    /// Why an <c>InProgress</c> item with no claim, which the ledger does not record as claimed by this factory, is not taken: the
    /// factory cannot tell its own lapsed claim (a run that died before its <c>claimed</c> checkpoint, more than
    /// <see cref="ClaimTtlSeconds"/> ago) from a person's work.
    /// </summary>
    public static readonly string LapsedRefusal =
        $"the item is {KanbanStatus.InProgress} with no claim, and the ledger records no claim of it by this factory (a claim lasts "
        + $"{ClaimTtlSeconds / 3600} hours and is not renewed, so the factory's claim may have lapsed): move it back to {KanbanStatus.Ready} "
        + "to hand it to the factory again";

    /// <summary>In scope while the item is on the board and its project is one of the watched ones.</summary>
    public async Task<bool> InScopeAsync(int id, CancellationToken ct) =>
        !scope.IsEmpty && await keys.ItemAsync(id, ct) is { } itemId && await FindAsync(itemId, ct) is { } item
        && await WatchesAsync(item.ProjectId, ct);

    /// <summary>Reads the board's projects with the configured token; throws naming the first watched key no project has.</summary>
    public async Task ValidateScopeAsync(CancellationToken ct) => await WatchedAsync(ct);

    /// <summary>Releases the factory's claim, if the item still carries it.</summary>
    public async Task ReleaseAsync(int id, CancellationToken ct)
    {
        if (await FindAsync(await ItemIdAsync(id, ct), ct) is { } item)
        {
            await ReleaseOursAsync(item, ct);
        }
    }

    /// <summary>
    /// The item, its epic and the epic's documents. The item's repository is its project's <c>repository_url</c> unless its
    /// description has a <c>Repo: owner/name</c> line (<see cref="RepoResolver"/>): without one, the description is led by a
    /// code-written <c>Repo:</c> line naming the project's repository, which is how every work source tells the pipeline where an
    /// item is worked. A project with no <c>repository_url</c> leaves the item to <c>Factory:DefaultRepo</c>; one whose
    /// <c>repository_url</c> names no github.com repository, and a project that is archived or gone, is refused here
    /// (<see cref="KanbanItemException"/>, as is a key the ledger never gave out).
    /// </summary>
    public async Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct)
    {
        var item = await board.GetItemAsync(await ItemIdAsync(id, ct), ct);
        var description = item.Description;
        if (RepoResolver.Named(description) is null)
        {
            var project = (await board.ListProjectsAsync(ct)).FirstOrDefault(p => p.Id == item.ProjectId)
                ?? throw new KanbanItemException($"{Naming.Format(id)}: its Kanban project is archived or gone, so its repository is "
                    + "unknown; restore the project, or add a `Repo: owner/name` line to the item.");
            if (!string.IsNullOrWhiteSpace(project.RepositoryUrl))
            {
                var repo = Gateway.GitRemoteReads.RepoOf(project.RepositoryUrl)
                    ?? throw new KanbanItemException($"{Naming.Format(id)}: the repository_url of its Kanban project {project.Key} "
                        + $"names no github.com repository ('{Cut(project.RepositoryUrl)}'); set it to one, or add a `Repo: owner/name` "
                        + "line to the item.");
                description = string.IsNullOrWhiteSpace(description) ? $"Repo: {repo.FullName}" : $"Repo: {repo.FullName}\n\n{description}";
            }
        }
        // The labels and estimate decide the item's coding model class (WorkerModelClass.Coding, E8).
        var story = new WorkStory(id, item.Title, description, StoryType(item.Type), _links.Item(item), Naming, Labels: item.LabelList ?? [],
            Estimate: item.Estimate);
        if (item.EpicId is not { } epicId)
        {
            return new WorkSpec(story, null, []);
        }
        var epic = await board.GetEpicAsync(epicId, ct);
        var documents = (await board.ListEpicDocumentsAsync(epicId, ct))
            .Select(d => new WorkDocument(d.Title, d.Body, _links.Document(item.ProjectId, epicId, d.Id)))
            .ToList();
        // A Kanban epic has no number an epic-scope control addresses: none reaches the item.
        return new WorkSpec(story, new WorkEpic(null, epic.Name, epic.Description, _links.Epic(item.ProjectId, epicId)), documents);
    }

    /// <summary>
    /// Claimed: <c>InProgress</c>. Merged: <c>Done</c>. Stopped: <c>Backlog</c>, the factory's claim released, and the comment
    /// posted. An item already in the target column is not written; a move is conditional on the version just read.
    /// </summary>
    public async Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct)
    {
        if (state == BoardState.Stopped && string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Stopping an item needs a comment saying why.", nameof(comment));
        }
        var target = state switch
        {
            BoardState.Claimed => KanbanStatus.InProgress,
            BoardState.Merged => KanbanStatus.Done,
            BoardState.Stopped => KanbanStatus.Backlog,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        var item = await board.GetItemAsync(await ItemIdAsync(id, ct), ct);
        if (item.Status != target)
        {
            item = await board.SetStatusAsync(item.Id, target, item.Version, ct);
        }
        if (state == BoardState.Stopped)
        {
            await ReleaseOursAsync(item, ct);
        }
        if (comment is not null)
        {
            await CommentOnAsync(item, comment, ct);
        }
    }

    public async Task CommentAsync(int id, string text, CancellationToken ct) =>
        await CommentOnAsync(await board.GetItemAsync(await ItemIdAsync(id, ct), ct), text, ct);

    /// <summary>Attaches each URL to the item under its kind (<see cref="LinkKind"/>); one the item already carries changes nothing.</summary>
    public async Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var itemId = await ItemIdAsync(id, ct);
        foreach (var url in urls)
        {
            await board.LinkAsync(itemId, LinkKind(url), url, ct);
        }
    }

    public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) =>
        throw new NotSupportedException("The Kanban work source creates no plan children.");

    private Task CommentOnAsync(KanbanWorkItem item, string text, CancellationToken ct)
    {
        var body = WorkSourceComments.Attributed(text);
        return board.CommentAsync(item.Id, body, CommentKey(item.Id, item.Version, body), ct);
    }

    /// <summary>
    /// Releases the claim when the item carries the factory's marker. A release the board refuses because the caller holds no
    /// claim (it lapsed) or someone else holds the item leaves nothing of the factory's to release.
    /// </summary>
    private async Task ReleaseOursAsync(KanbanWorkItem item, CancellationToken ct)
    {
        var marker = Marker(item.Id);
        if (item.ClaimMarker != marker)
        {
            return;
        }
        try
        {
            await board.ReleaseClaimAsync(item.Id, marker, ct);
        }
        catch (KanbanException ex) when (ex.Code is KanbanException.Conflict or KanbanException.AlreadyClaimed)
        {
        }
    }

    private async Task<Guid> ItemIdAsync(int id, CancellationToken ct) =>
        await keys.ItemAsync(id, ct) ?? throw new KanbanItemException($"{Naming.Format(id)} is no Kanban item the factory knows: the "
            + "ledger never gave out that key. Run `factory kanban key <item GUID>` first; it prints the item's kb-<n>.");

    /// <summary>The item, or null when the board no longer has it.</summary>
    private async Task<KanbanWorkItem?> FindAsync(Guid itemId, CancellationToken ct)
    {
        try
        {
            return await board.GetItemAsync(itemId, ct);
        }
        catch (KanbanException ex) when (ex.Code == KanbanException.NotFound)
        {
            return null;
        }
    }

    /// <summary>The watched projects, in the configured order; throws naming the first key no (unarchived) project has.</summary>
    private async Task<IReadOnlyList<KanbanProject>> WatchedAsync(CancellationToken ct)
    {
        if (scope.IsEmpty)
        {
            return [];
        }
        var projects = await board.ListProjectsAsync(ct);
        return scope.Projects
            .Select(key => projects.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{KanbanScope.Setting}: KanbanBoard has no project with the key '{key}' (or it is archived)."))
            .ToList();
    }

    private async Task<bool> WatchesAsync(Guid projectId, CancellationToken ct) =>
        !scope.IsEmpty && (await board.ListProjectsAsync(ct))
            .Any(p => p.Id == projectId && scope.Projects.Contains(p.Key, StringComparer.OrdinalIgnoreCase));

    /// <summary>The board's item types in the words the other sources use (prompts and the intake row).</summary>
    private static string StoryType(string type) => type switch
    {
        "Story" => "feature",
        "Issue" => "bug",
        "Task" => "chore",
        _ => type.ToLowerInvariant(),
    };

    private static string Cut(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>
/// A Kanban item the factory cannot work as it is named or set up (sc-25981): a <c>kb-&lt;n&gt;</c> the ledger never gave out, or an
/// item whose repository is unknown (its project archived or gone, or a <c>repository_url</c> that names no github.com repository).
/// The message says what to do. <c>factory run</c> prints it and exits 1 (<c>FactoryRunner.RunCommandAsync</c>): it is raised
/// reading the item's spec, before the item has a ledger row to escalate.
/// </summary>
public sealed class KanbanItemException(string message) : InvalidOperationException(message);
