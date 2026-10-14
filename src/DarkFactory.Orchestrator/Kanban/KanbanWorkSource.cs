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
/// carries changes nothing. No key written onto the board derives from a ledger key. A <c>busy</c> answer is retried and a
/// write whose answer was lost is read before it is repeated by the typed caller (<see cref="KanbanBoard"/>, sc-25983).
/// Conflicts (sc-25983): the board's claims are advisory — an update or a move does not look at them — so every status change is
/// conditional on the version read, and a <c>conflict</c> means someone else wrote the item in between: it is read again and the
/// move decided again (<see cref="MoveAsync"/>), never written over. Their edit of another field is kept and the move is made on
/// the new version; a status THEY gave the item stands, the factory's move is not made, and the caller says so (a refused claim,
/// or a comment on the item: <see cref="NotMoved"/>).
/// Claims expire (sc-25983): a claim is taken for <see cref="ClaimTtlSeconds"/>, the longest the board allows, and renewed on
/// each intake poll (<see cref="RenewClaimAsync"/>) for every item the ledger would resume by itself — one in a state the
/// pipeline drives, or paused in a way that resumes by itself — whatever a Pause, the usage pause or the freeze says now
/// (<c>RunPipeline.RenewableAsync</c>): a pause of any length does not cost the item its claim, and neither does a plan item's
/// wait for its stories (sc-25984). An item that is parked,
/// escalated or being stopped is not renewed: its claim lapses after a day and what keeps it out of everyone's ready list is its
/// <c>InProgress</c> column alone (this source lists only <c>Ready</c> items, and so does the contract's "polling for ready
/// work"); a lapsed claim of an <c>InProgress</c> item the ledger records as claimed by this factory is taken again, by a
/// renewal or by <see cref="ClaimAsync"/> on a re-run.
/// Plans (sc-25984): a Kanban item labelled <c>plan</c> (the board's own labels, <c>label_list</c>) is a plan item
/// (<see cref="PlansItems"/>), like a Shortcut story — the board is the owner's, so its items are trusted and its labels the
/// owner's to set (<see cref="ItemNaming.Trusted"/>). Its planned items are created one by one in the plan item's project and
/// epic, in <c>Backlog</c>, each under an idempotency key derived from the plan item's GUID and the item's own content, then
/// joined by the board's dependencies (<see cref="CreateChildrenAsync"/>; not <c>import_epic_plan</c>, which always makes a new
/// epic). Each gets its ledger key the moment the board has answered its GUID, before anything else is written for it, so the
/// ledger and the pipeline name it <c>kb-&lt;n&gt;</c> while everything written onto the board — the planned-item marker, the
/// idempotency key — names GUIDs (E4). Blockers are the board's dependencies (<see cref="BlockersAsync"/>: finished only in
/// <c>Done</c>; a <c>Closed</c> blocker still blocks), a release is <c>Backlog</c> → <c>Ready</c> on the version read
/// (<see cref="MakeReadyAsync"/>), and a released item nobody started goes back the same way (<see cref="ReturnUnstartedAsync"/>).
/// One difference from Shortcut, the board's own rule: its ready listing leaves out an item whose blocker is not <c>Done</c>, so
/// a released item is listed only once every blocker is <c>Done</c> on the board — which the factory's merge of a blocker makes
/// it (<c>BoardState.Merged</c>). Any blocker the plan counts as merged that is not <c>Done</c> on the board — one whose pull
/// request was merged outside the factory into the plan's changeset branch (its item stays Escalated and <c>InProgress</c>,
/// as that escalation says), one a person moved out of <c>Done</c> again, one whose move to <c>Done</c> the board refused —
/// keeps its released dependents out of the listing until a person moves it to <c>Done</c>; <c>factory run kb-&lt;n&gt;</c>
/// on such a dependent is refused by this source, naming the blocker (<see cref="ClaimAsync"/>).
/// </summary>
public sealed class KanbanWorkSource(KanbanBoard board, KanbanKeys keys, KanbanScope scope, Uri webUrl) : IWorkSource
{
    /// <summary>
    /// How long a claim lasts unless renewed: the longest KanbanBoard allows (a day). <c>factory work</c> polls every
    /// <c>Intake:PollSeconds</c> and renews then (<see cref="RenewClaimAsync"/>), so a claim lapses only on an item that is
    /// parked or escalated, or whose single run holds the intake loop, for a whole day. An item whose run died between the claim and the
    /// move to <c>InProgress</c> stays claimed that long.
    /// </summary>
    public const int ClaimTtlSeconds = 86400;

    /// <summary>
    /// How often one move is decided again after a <c>conflict</c> (the item read again each time) before the conflict is thrown:
    /// someone who edits the item between every read and write is not waited out.
    /// </summary>
    public const int MaxConflictRereads = 3;

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
    /// <c>InProgress</c>, on the version just read (a <c>conflict</c> there: <see cref="MoveAsync"/>; an item someone moved out
    /// of <c>Ready</c> meanwhile is theirs to place — the claim is given back and the claim refused).
    /// "Already the factory's and <c>InProgress</c>" is an item carrying the factory's live claim, or — a claim lasts
    /// <see cref="ClaimTtlSeconds"/> and a parked or escalated item's is not renewed — one with no claim at all that the ledger records as claimed by this
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
            return ClaimResult.Refused($"the item is claimed by {Holder(item)}");
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
            // A blocker's title may be a planner's text (a planned item, sc-25984) and the reason goes to the ledger and wherever
            // the refusal is reported: each is cut and made one inert code span.
            return ClaimResult.Refused($"the item is blocked by {string.Join(", ", blockers.Select(b => $"{Quoted(b.Title) ?? "an item"} ({b.Status})"))}");
        }

        KanbanWorkItem claimed;
        try
        {
            claimed = await board.ClaimAsync(itemId, marker, ClaimTtlSeconds, ct);
        }
        catch (KanbanException ex) when (ex.Code == KanbanException.AlreadyClaimed)
        {
            // Another caller won the race since the read: the item is read once more, only to name them.
            return ClaimResult.Refused($"the item is claimed by {Holder(await FindAsync(itemId, ct))}");
        }
        var after = await board.GetItemAsync(itemId, ct);
        if (after.ClaimMarker != marker || after.ClaimantId is null || after.ClaimantId != claimed.ClaimantId)
        {
            return ClaimResult.Refused(
                $"the claim did not stick (read back claimant {Quoted(after.Claimant) ?? "none"}, marker {Quoted(after.ClaimMarker) ?? "none"})");
        }
        if (after.Status == KanbanStatus.Ready)
        {
            try
            {
                after = await MoveAsync(after, KanbanStatus.InProgress, ct);
            }
            catch (KanbanException ex) when (ex.Code == KanbanException.Conflict)
            {
                // The move kept conflicting (MaxConflictRereads): the item is still Ready, and a claim left on it would hide it
                // from every ready listing (claim_state: unclaimed) for a day. The claim is given back; the conflict is thrown.
                try
                {
                    await ReleaseOursAsync(after, ct);
                }
                catch (Exception release) when (release is not OperationCanceledException)
                {
                    throw new AggregateException(ex, release);
                }
                throw;
            }
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
        + $"{ClaimTtlSeconds / 3600} hours and is renewed only while the item is in flight, so the factory's claim may have lapsed): move it "
        + $"back to {KanbanStatus.Ready} to hand it to the factory again";

    /// <summary>
    /// Keeps the factory's claim on an item for another <see cref="ClaimTtlSeconds"/> (sc-25983; asked once per intake poll, and
    /// before a claim-lost item is resumed): <c>renew_work_item_claim</c> under the item's marker. A renewal the board refuses —
    /// <c>conflict</c>: the claim lapsed or was released; <c>already_claimed</c>: someone else holds the item — is decided on a
    /// fresh read. An item that carries another marker is lost — the reason names the claimant — and so is one that is gone from
    /// the board. An item with no claim, or one that reads as carrying the factory's marker, is claimed again
    /// (<c>claim_work_item</c>; the board tells the factory's own claim from another identity's under the same marker, and a claim
    /// another caller wins is lost to them) ONLY when it is the factory's by <see cref="ClaimAsync"/>'s own rule for an item with
    /// no claim: it is <c>InProgress</c> and the ledger records that this factory claimed it
    /// (<see cref="KanbanKeys.ClaimedAsync"/>). Any other item is left as it is, nothing written, and reported
    /// <see cref="ClaimRenewal.Held"/> — nothing shows the factory lost it to anyone: an item whose run died before its
    /// <c>claimed</c> checkpoint (in flight at Intake, never recorded as claimed: taking it here would skip every check of
    /// <see cref="ClaimAsync"/>, which that item's run makes next, <see cref="LapsedRefusal"/> included), and one a person moved
    /// out of <c>InProgress</c> (theirs to place; a claim on it would only hide it from the ready listings). Nothing but the claim
    /// is ever written: the item's column, version and content are left alone.
    /// A typed refusal that says nothing about who holds the item (<c>scope_denied</c>, <c>invalid_argument</c>; a key the ledger
    /// never gave out) is <see cref="ClaimRenewal.NotRenewed"/>: the board answered, and the next item can be asked. Anything
    /// thrown — <c>busy</c> after its retries, a lost answer, a board that is down — is the board not answering.
    /// </summary>
    public async Task<ClaimRenewal> RenewClaimAsync(int id, CancellationToken ct)
    {
        if (await keys.ItemAsync(id, ct) is not { } itemId)
        {
            return ClaimRenewal.NotRenewedBecause($"{Naming.Format(id)} is no Kanban item the factory knows");
        }
        try
        {
            return await RenewAsync(id, itemId, ct);
        }
        catch (KanbanException ex) when (ex.Refused && ex.Code != KanbanException.Busy)
        {
            return ClaimRenewal.NotRenewedBecause(ex.Message);
        }
    }

    private async Task<ClaimRenewal> RenewAsync(int id, Guid itemId, CancellationToken ct)
    {
        var marker = Marker(itemId);
        try
        {
            await board.RenewClaimAsync(itemId, marker, ClaimTtlSeconds, ct);
            return ClaimRenewal.Held;
        }
        catch (KanbanException ex) when (ex.Code is KanbanException.Conflict or KanbanException.AlreadyClaimed or KanbanException.NotFound)
        {
        }
        if (await FindAsync(itemId, ct) is not { } item)
        {
            return ClaimRenewal.LostBecause(GoneFromBoard);
        }
        if (item.ClaimMarker is null || item.ClaimMarker == marker)
        {
            if (item.Status != KanbanStatus.InProgress || !await keys.ClaimedAsync(id, ct))
            {
                // Not the factory's to take here: left as it is, for ClaimAsync (the item's run) or a person to decide.
                return ClaimRenewal.Held;
            }
            try
            {
                await board.ClaimAsync(itemId, marker, ClaimTtlSeconds, ct);
                return ClaimRenewal.Held;
            }
            catch (KanbanException ex) when (ex.Code is KanbanException.AlreadyClaimed or KanbanException.NotFound)
            {
            }
            if (await FindAsync(itemId, ct) is not { } taken)
            {
                return ClaimRenewal.LostBecause(GoneFromBoard);
            }
            item = taken;
        }
        return ClaimRenewal.LostBecause(ClaimedBy(item));
    }

    private const string GoneFromBoard = "the item is no longer on the board";

    /// <summary>
    /// Why a claim is lost to whoever holds the item now, naming them. The claimant's name and the marker are another caller's
    /// text (a marker is up to 200 characters of their choosing), and the reason goes into a board comment and the ledger: each is
    /// cut and made one inert code span (<see cref="UntrustedText.CodeSpan"/>).
    /// </summary>
    private static string ClaimedBy(KanbanWorkItem item) => item.ClaimMarker is null
        ? "the board refused the factory's claim, and the item now reads as unclaimed"
        : $"the item is claimed by {Holder(item)} (marker {Quoted(item.ClaimMarker)})";

    /// <summary>Who holds the item, for a refusal: the claimant's name, cut and inert in a code span, or "another caller".</summary>
    private static string Holder(KanbanWorkItem? item) => Quoted(item?.Claimant) ?? "another caller";

    /// <summary>Another caller's text (a claimant's name, a marker), cut and inert in a code span; null for none.</summary>
    private static string? Quoted(string? theirs) => string.IsNullOrEmpty(theirs) ? null : UntrustedText.CodeSpan(Cut(theirs));

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
        // What the factory writes onto the board about the item names its GUID (E4); a planned item's marker names its plan item's
        // GUID, which the ledger's own name stands in for wherever a person reads it.
        var planRef = Plans.PlannedStory.ParentOf(description) is { } parent && Guid.TryParseExact(parent, "D", out var plan)
            && await keys.KnownAsync(plan, ct) is { } planKey ? Naming.Format(planKey) : null;
        var story = new WorkStory(id, item.Title, description, StoryType(item.Type), _links.Item(item), Naming, Labels: item.LabelList ?? [],
            Estimate: item.Estimate, BoardId: item.Id.ToString("D"), PlanRef: planRef);
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
    /// posted. An item already in the target column is not written; a move is conditional on the version just read
    /// (<see cref="MoveAsync"/>). An item someone else moved to another column between that read and the write stays where they
    /// put it: the factory's move is not made, and the item is told so in the comment (<see cref="NotMoved"/>, after
    /// <paramref name="comment"/> when there is one) — the claim is still released on a stop. A later report (a re-run) decides
    /// afresh on what it then reads.
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
            item = await MoveAsync(item, target, ct);
        }
        if (state == BoardState.Stopped)
        {
            await ReleaseOursAsync(item, ct);
        }
        if (item.Status != target)
        {
            comment = comment is null ? NotMoved(target, item.Status) : $"{comment}\n\n{NotMoved(target, item.Status)}";
        }
        if (comment is not null)
        {
            await CommentOnAsync(item, comment, ct);
        }
    }

    /// <summary>What the item is told when someone else moved it while the factory was moving it: their column stands.</summary>
    public static string NotMoved(string target, string status) =>
        $"The factory was moving this item to {target} when someone else moved it to {status}; it is left in {status}, where they put it.";

    /// <summary>
    /// Moves <paramref name="item"/> to <paramref name="target"/> on the version it was read at. A <c>conflict</c> — someone
    /// else updated or moved the item since — is never written over: the item is read again and the move decided again, on what
    /// it now is. Still in the column the move was decided from (they changed something else: a title, a label, the order): the
    /// move is made on the new version, and their change is kept (a status change writes nothing else). In any other column:
    /// they moved it, and it is returned as they left it, unmoved — the caller compares its status with
    /// <paramref name="target"/>. At most <see cref="MaxConflictRereads"/> re-reads; the conflict after the last is thrown
    /// (<see cref="ClaimAsync"/> then gives its claim back before it rethrows).
    /// </summary>
    private async Task<KanbanWorkItem> MoveAsync(KanbanWorkItem item, string target, CancellationToken ct)
    {
        var from = item.Status;
        for (var reread = 0; item.Status == from && item.Status != target; reread++)
        {
            try
            {
                return await board.SetStatusAsync(item.Id, target, item.Version, ct);
            }
            catch (KanbanException ex) when (ex.Code == KanbanException.Conflict && reread < MaxConflictRereads)
            {
                item = await board.GetItemAsync(item.Id, ct);
            }
        }
        return item;
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

    public bool PlansItems => true;

    /// <summary>A <c>Closed</c> item is the board's archived one: hidden from its listings, and never worked.</summary>
    public (string Is, string Undo, string Do) Shelved => (KanbanStatus.Closed, $"move it back to {KanbanStatus.Backlog}", "close");

    /// <summary>
    /// The idempotency key of a planned item (E3, E4): <see cref="ChildItem.Key"/> under the plan item's board id — its GUID, never
    /// its <c>kb-&lt;n&gt;</c> — so it is the same on every retry and after a restart, and on a ledger that was rebuilt. 120
    /// characters, inside the board's limit (<see cref="MaxIdempotencyKeyLength"/>).
    /// </summary>
    public static string ChildKey(Guid parentId, ChildItem child) => child.Key(parentId.ToString("D"));

    /// <summary>The longest idempotency key the board takes (printable ASCII without spaces).</summary>
    public const int MaxIdempotencyKeyLength = 128;

    /// <summary>
    /// Creates a plan's items (sc-25984) in the plan item's project and — when it has one — its epic, in <c>Backlog</c>, in order,
    /// and joins them by the board's dependencies; returns their ledger keys in order. Each is one <c>create_work_item</c> under
    /// <see cref="ChildKey"/>: the board creates the item once per key and answers the same item on every repeat
    /// (<c>created</c> false), so a retry after a failure part-way — or a restart, or a lost answer — creates nothing twice and
    /// needs no search for what an earlier attempt made (a namesake someone else wrote has no such key and is never taken for
    /// one). The item's key is allocated as soon as the board has answered its GUID, before its dependencies are written. A
    /// dependency is <c>add_work_item_dependency</c>, which changes nothing when it exists already; one the created item's own
    /// answer already lists is not sent again.
    /// Refused before any board write: a child blocked by itself or by a later child (the only way a plan's own order could be a
    /// cycle; <see cref="ArgumentException"/>) and a story type the board has no item type for. A dependency the board refuses as
    /// a <c>cycle</c> — someone made the blocker depend on the item since — fails the call naming both items
    /// (<see cref="KanbanItemException"/>): the items stay, nothing is recorded as the plan's, and a re-run after the stray
    /// dependency is removed creates nothing twice.
    /// </summary>
    public async Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i].BlockedBy.Any(b => b < 0 || b >= i))
            {
                throw new ArgumentException($"Child {i} may only be blocked by earlier children.", nameof(children));
            }
        }
        var types = children.Select(c => ItemType(c.StoryType)).ToList();
        var parent = await board.GetItemAsync(await ItemIdAsync(parentId, ct), ct);
        var made = new List<KanbanWorkItem>();
        var ids = new List<int>();
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var item = await board.CreateItemAsync(parent.ProjectId, child.Name, types[i], child.Description, parent.EpicId, ChildKey(parent.Id, child), ct);
            // Its key before anything else is written for it (E4).
            var id = await keys.KeyAsync(item.Id, ct);
            foreach (var blocker in child.BlockedBy.Distinct())
            {
                if ((item.Blockers ?? []).Any(b => b.Id == made[blocker].Id))
                {
                    continue;
                }
                try
                {
                    await board.AddDependencyAsync(item.Id, made[blocker].Id, ct);
                }
                catch (KanbanException ex) when (ex.Code == KanbanException.Cycle)
                {
                    var (blocked, by) = (Naming.Format(id), Naming.Format(ids[blocker]));
                    throw new KanbanItemException($"{blocked} could not be made to wait for {by}: the board refuses the dependency as a "
                        + $"cycle, so {by} already depends on {blocked} (directly or through other items) — a dependency the plan did not "
                        + "make. Remove it on the board and re-run the plan item; its items are not created twice.");
                }
            }
            made.Add(item);
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The item's blockers on the board (<c>get_work_item</c>'s <c>blockers</c>: whoever made the dependency), each by its ledger
    /// key — allocated now for an item the factory has not seen — and whether the board shows it finished: only in <c>Done</c>
    /// (a <c>Closed</c> blocker still blocks, as the board's own ready listing has it). One read.
    /// </summary>
    public async Task<IReadOnlyList<Blocker>> BlockersAsync(int id, CancellationToken ct)
    {
        var blockers = new List<Blocker>();
        foreach (var blocker in (await board.GetItemAsync(await ItemIdAsync(id, ct), ct)).Blockers ?? [])
        {
            blockers.Add(new Blocker(await keys.KeyAsync(blocker.Id, ct), blocker.Status == KanbanStatus.Done));
        }
        return [.. blockers.DistinctBy(b => b.Id).OrderBy(b => b.Id)];
    }

    /// <summary>
    /// <c>Backlog</c> → <c>Ready</c>, on the version just read, only for an item still in <c>Backlog</c>. A <c>Closed</c> item is
    /// the board's archived one (<see cref="ReadyResult.Archived"/>); one in any other column has left the backlog already and is
    /// not written. A <c>conflict</c> — someone edited the item since the read — is decided again on a fresh read
    /// (<see cref="MoveAsync"/>): still in <c>Backlog</c>, it is moved on the new version and their edit kept; moved by them, it
    /// stays where they put it.
    /// </summary>
    public async Task<ReadyResult> MakeReadyAsync(int id, CancellationToken ct)
    {
        var item = await board.GetItemAsync(await ItemIdAsync(id, ct), ct);
        if (item.Status == KanbanStatus.Backlog)
        {
            item = await MoveAsync(item, KanbanStatus.Ready, ct);
            if (item.Status == KanbanStatus.Ready)
            {
                return ReadyResult.Moved;
            }
        }
        return item.Status == KanbanStatus.Closed ? ReadyResult.Archived : ReadyResult.AlreadyLeft;
    }

    public async Task<bool> InBacklogAsync(int id, CancellationToken ct) =>
        (await board.GetItemAsync(await ItemIdAsync(id, ct), ct)).Status == KanbanStatus.Backlog;

    /// <summary>
    /// <c>Ready</c> → <c>Backlog</c> with the comment, only for an item that is still <c>Ready</c> and carries no claim but the
    /// factory's own; any other is left as it is (false, nothing written): someone claimed it, moved it or closed it, and it is
    /// theirs. The move is conditional on the version read, and a <c>conflict</c> — someone wrote the item since — is decided
    /// again on a fresh read, by the same rule (status AND claim, so not <see cref="MoveAsync"/>, which looks at the status
    /// alone): an item someone moved or claimed meanwhile stays where they put it (false, no comment). A claim of the factory's left on it (a run that died between its claim and its move) is released, so the item
    /// does not read as held while it waits in the backlog.
    /// </summary>
    public async Task<bool> ReturnUnstartedAsync(int id, string comment, CancellationToken ct)
    {
        var item = await board.GetItemAsync(await ItemIdAsync(id, ct), ct);
        KanbanWorkItem moved;
        for (var reread = 0; ; reread++)
        {
            if (item.Status != KanbanStatus.Ready || (item.ClaimMarker is { } held && held != Marker(item.Id)))
            {
                return false;
            }
            try
            {
                moved = await board.SetStatusAsync(item.Id, KanbanStatus.Backlog, item.Version, ct);
                break;
            }
            catch (KanbanException ex) when (ex.Code == KanbanException.Conflict && reread < MaxConflictRereads)
            {
                item = await board.GetItemAsync(item.Id, ct);
            }
        }
        await ReleaseOursAsync(moved, ct);
        await CommentOnAsync(moved, comment, ct);
        return true;
    }

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

    /// <summary>The board's item types in the words the other sources use (prompts and the intake row), and the planner's.</summary>
    public static string StoryType(string type) => type switch
    {
        "Story" => "feature",
        "Issue" => "bug",
        "Task" => "chore",
        _ => type.ToLowerInvariant(),
    };

    /// <summary>
    /// The board's item type of a planned story's type (<see cref="Plans.PlanParser"/> allows exactly these three): the inverse of
    /// <see cref="StoryType"/>. Any other type has no item type and is refused (nothing is created as something it is not).
    /// </summary>
    public static string ItemType(string storyType) => storyType switch
    {
        "feature" => "Story",
        "bug" => "Issue",
        "chore" => "Task",
        _ => throw new ArgumentException($"'{storyType}' is no story type the Kanban board has an item type for (feature, bug, chore).", nameof(storyType)),
    };

    private static string Cut(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>
/// A Kanban item the factory cannot work as it is named or set up (sc-25981): a <c>kb-&lt;n&gt;</c> the ledger never gave out, or an
/// item whose repository is unknown (its project archived or gone, or a <c>repository_url</c> that names no github.com repository).
/// The message says what to do. <c>factory run</c> prints it and exits 1 (<c>FactoryRunner.RunCommandAsync</c>): it is raised
/// reading the item's spec, before the item has a ledger row to escalate. Also (sc-25984) a plan's item the board will not let
/// wait for its blocker (a dependency refused as a cycle): raised inside the plan item's Plan step, which escalates it with the
/// message like any failed step.
/// </summary>
public sealed class KanbanItemException(string message) : InvalidOperationException(message);
