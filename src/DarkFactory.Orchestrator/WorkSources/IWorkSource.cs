namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>One work item (a Shortcut story, a Kanban item, or a triaged GitHub issue) as the orchestrator sees it.</summary>
/// <param name="Naming">
/// The item's source naming, always given by the source that read it (sc-25992, E5): trust is decided from it
/// (<see cref="UntrustedName"/>), so no story is trusted by a default.
/// </param>
/// <param name="Closes">
/// The issue the item's PR closes on merge, as GitHub's closing keyword takes it (<c>owner/name#12</c>); null for none.
/// </param>
/// <param name="Labels">The item's labels on its board (none when the board has none).</param>
/// <param name="Estimate">The item's estimate in points; null when it has none (a GitHub issue never has one).</param>
/// <param name="BoardId">
/// The item's id on its board, when that is not its ledger name (Kanban: the item's GUID; its <c>kb-&lt;n&gt;</c> is the ledger's
/// own); null for a source whose ledger name is the board's id (<see cref="BoardRef"/>).
/// </param>
/// <param name="PlanRef">
/// For an item a plan created: the plan item its marker names, as the ledger names it, when the source had to translate it
/// (Kanban: the <c>kb-&lt;n&gt;</c> of the GUID in the marker); null otherwise (<see cref="PlannedFrom"/>).
/// </param>
public sealed record WorkStory(int Id, string Name, string? Description, string StoryType, string AppUrl, ItemNaming Naming,
    string? Closes = null, IReadOnlyList<string>? Labels = null, int? Estimate = null, string? BoardId = null, string? PlanRef = null)
{
    public ItemNaming Kind => Naming;

    /// <summary>
    /// The name as the factory writes it on GitHub (commit message, pull request title and description): an untrusted name
    /// (<see cref="UntrustedName"/>) is made inert (<see cref="UntrustedText.Inert"/>).
    /// </summary>
    public string PublicName => UntrustedName ? UntrustedText.Inert(Name) : Name;

    /// <summary>
    /// Whether <see cref="Name"/> and <see cref="Description"/> are data rather than the owner's instructions — the one trust
    /// decision every prompt, commit message and pull request asks (sc-25984, E5). It has two halves and no third: the item's
    /// source is not trusted (<see cref="ItemNaming.Trusted"/>, a property of the source: a Shortcut story and a Kanban item are
    /// written on the owner's board, a GitHub issue's item is a triage of text anyone may file), or the factory created the item
    /// from a plan (<see cref="PlannerAuthored"/>, sc-25870: a planning model's text, untrusted on every source).
    /// </summary>
    public bool UntrustedName => !Kind.Trusted || PlannerAuthored;

    /// <summary>
    /// Whether the factory created this story from a plan: its description carries the plan's marker (<see cref="Plans.PlannedStory"/>),
    /// which the code writes on every source, never the model.
    /// </summary>
    public bool PlannerAuthored => Plans.PlannedStory.IsMarked(Description);

    /// <summary>The item's external id, e.g. <c>sc-12</c>.</summary>
    public string Ref => Kind.Format(Id);

    /// <summary>
    /// How the item is named in what the factory writes ONTO its board about it (E4: a marker, an idempotency key): its id on the
    /// board (<see cref="BoardId"/>), which is the ledger name itself (<see cref="Ref"/>) for a source that has no other.
    /// </summary>
    public string BoardRef => BoardId ?? Ref;

    /// <summary>
    /// The plan item this story's marker names (<see cref="Plans.PlannedStory.ParentOf"/>), in the ledger's name when the source
    /// gave one (<see cref="PlanRef"/>); null without a marker. Text anyone may write: only ever shown, never trusted.
    /// </summary>
    public string? PlannedFrom => PlanRef ?? Plans.PlannedStory.ParentOf(Description);
}

/// <param name="Id">
/// The epic's id as the ledger holds it (<see cref="Ledger.WorkItem.EpicId"/>) and an epic-scope control names it, within the
/// item's source (<see cref="ItemNaming.Epic"/>): a Shortcut epic's own number (<c>epic:12</c>), or the key the ledger gave a Kanban
/// epic, whose board id is a GUID (<c>epic:kb-3</c>; sc-25985) — recorded before the source answers with it (E4).
/// </param>
public sealed record WorkEpic(long Id, string Name, string? Description, string AppUrl);

public sealed record WorkDocument(string Title, string? Markdown, string AppUrl);

/// <summary>The item's spec: the story, its epic (if any) and the epic's documents.</summary>
public sealed record WorkSpec(WorkStory Story, WorkEpic? Epic, IReadOnlyList<WorkDocument> Documents);

/// <summary>
/// The coarse states the factory reports on a board. The detailed factory state stays in the ledger.
/// </summary>
public enum BoardState
{
    /// <summary>The factory picked the item up (Shortcut: In Progress).</summary>
    Claimed,
    /// <summary>The item's change merged (Shortcut: Done).</summary>
    Merged,
    /// <summary>The item was stopped (Shortcut: Backlog, claim released, with a comment).</summary>
    Stopped,
}

/// <summary>The result of <see cref="IWorkSource.ClaimAsync"/>: claimed, or why the item was left untouched.</summary>
public sealed record ClaimResult(bool Claimed, string? Refusal)
{
    public static readonly ClaimResult Ok = new(true, null);

    public static ClaimResult Refused(string reason) => new(false, reason);
}

/// <summary>
/// The result of <see cref="IWorkSource.RenewClaimAsync"/>. <see cref="Held"/> (both reasons null): nothing shows anyone else
/// holds the item — its claim was renewed, or taken again, or there was nothing to renew. <see cref="Lost"/>: why the factory no
/// longer holds it, in words that name whoever holds it now. <see cref="NotRenewed"/>: the board answered and refused this
/// renewal for a reason that says nothing about who holds the item (the call's or the item's own: a revoked token, an item the
/// source does not know) — the claim is neither confirmed nor lost. A board that does not answer at all (down, hanging,
/// overloaded) is not a result: the source throws.
/// </summary>
public sealed record ClaimRenewal(string? Lost, string? NotRenewed = null)
{
    public static readonly ClaimRenewal Held = new((string?)null);

    /// <summary>Whether nothing stands in the way of working the item: neither lost nor refused.</summary>
    public bool IsHeld => Lost is null && NotRenewed is null;

    public static ClaimRenewal LostBecause(string reason) => new(reason);

    public static ClaimRenewal NotRenewedBecause(string reason) => new(null, reason);
}

/// <summary>
/// The work source will not create children under this parent and has written nothing (<see cref="IWorkSource.CreateChildrenAsync"/>):
/// the message says why, in words the item's owner can act on.
/// </summary>
public sealed class ChildrenRefusedException(string reason) : Exception(reason);

/// <summary>A story to create from a plan. <see cref="BlockedBy"/> holds indices of earlier children.</summary>
public sealed record ChildItem(string Name, string Description, string StoryType, IReadOnlyList<int> BlockedBy)
{
    /// <summary>What every idempotency key starts with: only the factory's code writes one.</summary>
    public const string KeyPrefix = "dark-factory:child:";

    /// <summary>
    /// The idempotency key of this child under the parent <paramref name="parentRef"/> (the parent as its board names it:
    /// <c>sc-12</c>, a Kanban item's GUID — never a key only the ledger knows, E4; sc-25877): written by code, the same on every retry of the same plan — <see cref="KeyPrefix"/>, the parent,
    /// then the SHA-256 of the name and the description as sent (each UTF-8, the name's byte length first so no two pairs share
    /// a hash). A source stores it on the item it creates, in whatever field of its board holds an opaque id, and finds the child
    /// again by it (<see cref="IWorkSource.CreateChildrenAsync"/>), so it need not get the description back unchanged.
    /// Two children of one parent with the same name and the same description would have the same key, and a retry could take
    /// one board story for both. What rules that out is the plan's validation, not the key: <see cref="Plans.PlanParser"/>
    /// refuses a plan in which two stories share a title ("title … is used twice"), a child's name is its story's title, and the
    /// name is hashed (length first) — so within one plan the name alone makes every key unique, whatever the stories share
    /// otherwise. A caller that builds children any other way must keep their names unique itself.
    /// </summary>
    public string Key(string parentRef)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(Name);
        var description = System.Text.Encoding.UTF8.GetBytes(Description);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(name.Length)));
        hash.AppendData(name);
        hash.AppendData(description);
        return $"{KeyPrefix}{parentRef}:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }
}

/// <summary>
/// The only way the orchestrator touches a board (E6): the spec's work-sources table.
/// Boards are never synced; change notification is by polling <see cref="ListReadyAsync"/>.
/// </summary>
public interface IWorkSource
{
    /// <summary>
    /// How this source's items are named in the ledger and on git, and whether they are trusted (<see cref="ItemNaming.Trusted"/>).
    /// No default (sc-25992, E1/E5): every source declares its own, so none is trusted, or named like another, by omission.
    /// </summary>
    ItemNaming Naming { get; }

    /// <summary>Settle a source-owned identity that exists before pipeline intake. False when this source has no such item.</summary>
    Task<bool> StopBeforeIntakeAsync(int id, CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// How many claim refusals in a row an item may get before its run escalates it instead of parking it again (E10), for a source
    /// that lists a refused item again by itself (<see cref="ListReadyAsync"/>); null for a source that lists a refused item only once
    /// the board makes it ready again (Shortcut: the story back in To Do), which is no failure.
    /// </summary>
    int? MaxClaimRefusals => null;

    /// <summary>
    /// What a human does to bring an item that left the watch scope back into it, in this board's terms (posted on the item when it is
    /// parked out of scope): one sentence fragment, completed by ", or run `factory run … --ignore-scope`, to resume it."
    /// </summary>
    string ScopeReturnHint => "Move it back into scope and to To Do";

    /// <summary>
    /// Whether an item of this source labelled <see cref="Plans.PlanLabel.Name"/> is planned into child stories instead of implemented
    /// (sc-25870): the source is trusted (<see cref="ItemNaming.Trusted"/>: its labels are the owner's to set) and
    /// <see cref="CreateChildrenAsync"/> works. False (the default) for a source that creates no children or whose labels anyone
    /// may apply (a GitHub issue's): such an item is implemented like any other.
    /// </summary>
    bool PlansItems => false;

    /// <summary>Items ready for the factory, inside the configured watch scope; never items another claimant holds.</summary>
    Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct);

    /// <summary>
    /// Marks the item as the factory's so nothing else takes it. Idempotent. Re-reads the item first and
    /// refuses, writing nothing, unless it is still ready (or already the factory's), not held by another
    /// claimant and, unless <paramref name="ignoreScope"/>, inside the watch scope; after writing, reads the
    /// claim back and refuses if it did not stick.
    /// </summary>
    Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct);

    /// <summary>Read-only live ownership check for owner recovery. No claim, renewal or status write; unsupported sources refuse.</summary>
    Task<string?> RecoveryClaimRefusalAsync(int id, CancellationToken ct) =>
        Task.FromResult<string?>("this source cannot verify live recovery ownership");

    /// <summary>
    /// Keeps the factory's claim on an item it is working (sc-25983), for a source whose claims expire unless renewed: asked once
    /// per intake poll for every item of the source the ledger would resume by itself (<see cref="IItemRunner.RenewableAsync"/>,
    /// <see cref="IntakeLoop"/>), and by a run before it resumes an item that was paused because its claim was lost.
    /// <see cref="ClaimRenewal.Held"/> when nothing shows anyone else holds the item (renewed, or taken again after it lapsed with
    /// nobody else holding it); <see cref="ClaimRenewal.Lost"/> — naming who holds the item — when another caller has it, which
    /// pauses the item (<see cref="IItemRunner.ClaimLostAsync"/>); <see cref="ClaimRenewal.NotRenewed"/> when the board answered
    /// with a refusal that is this call's or this item's own. It throws only when the board does not answer (down, hanging,
    /// overloaded): the poll then asks it nothing more for the source's other items. Optional: a source whose claims do not expire
    /// (Shortcut's owner and label, a GitHub issue's assignment) has nothing to renew and always holds (the default).
    /// </summary>
    Task<ClaimRenewal> RenewClaimAsync(int id, CancellationToken ct) => Task.FromResult(ClaimRenewal.Held);

    /// <summary>Whether the item is (still) inside the configured watch scope.</summary>
    Task<bool> InScopeAsync(int id, CancellationToken ct);

    /// <summary>
    /// <c>factory work</c>'s start-up check: the board is reachable with the configured credentials and every
    /// part of the watch scope exists. Throws (<see cref="InvalidOperationException"/> or
    /// <see cref="HttpRequestException"/>) naming the first problem.
    /// </summary>
    Task ValidateScopeAsync(CancellationToken ct);

    /// <summary>Removes the factory's claim. Idempotent.</summary>
    Task ReleaseAsync(int id, CancellationToken ct);

    Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct);

    /// <summary>Internal sources may bind dispatch to immutable ledger authorization; ignore-scope never skips it.</summary>
    Task<ClaimResult> AuthorizeDispatchAsync(int id, RepoRef repository, CancellationToken ct) => Task.FromResult(ClaimResult.Ok);

    /// <summary>A code-bound delivery base, when an internal grant requires one. Never chosen by model text.</summary>
    Task<string?> DeliveryBaseAsync(int id, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// Moves the item on the board. <see cref="BoardState.Stopped"/> also releases the claim and
    /// requires <paramref name="comment"/>, which is posted on the item.
    /// </summary>
    Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct);

    /// <summary>Comments on the item; the body is led by <c>[author: dark-factory]</c>.</summary>
    Task CommentAsync(int id, string text, CancellationToken ct);

    /// <summary>Adds external links (PR, branch, repo) to the item, keeping existing ones.</summary>
    Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct);

    /// <summary>
    /// Creates stories from a plan in <paramref name="parentId"/>'s team and epic, waiting in the backlog,
    /// joined by blocker relations. Returns their ids in order. Idempotent: a retry reuses the children
    /// (and blocker relations) an earlier, partly failed call already created. A child is found again by its idempotency key
    /// (<see cref="ChildItem.Key"/>, which the source stores on the item it creates; sc-25877) — never by name alone: the name
    /// may be a model's text, and a namesake someone else wrote is not this call's child. A child created before items carried
    /// a key is still found by its name and description together, so a caller leads each description with text only it
    /// writes. Refuses before any board write
    /// (<see cref="ChildrenRefusedException"/>) when it could not find the children again after a failed attempt.
    /// </summary>
    Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct);

    /// <summary>
    /// The items that block <paramref name="id"/> on the board (sc-25871): every item the board relates to it as its blocker, whoever
    /// made the relation, inside a plan or not, each with whether the board shows it finished (<see cref="Blocker.Finished"/>: in the
    /// source's done state and not archived). Reads only. Whether a blocker is satisfied is not the board's to say: the pipeline
    /// decides it (<see cref="Plans.PlanRelease.Satisfied(IReadOnlyList{Ledger.LedgerEntry}?, bool, bool)"/>). A source without blocker
    /// relations has none (the default).
    /// </summary>
    Task<IReadOnlyList<Blocker>> BlockersAsync(int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<Blocker>>([]);

    /// <summary>
    /// Moves an item <see cref="CreateChildrenAsync"/> created out of the backlog into the source's ready state (Shortcut: To Do), where
    /// <see cref="ListReadyAsync"/> finds it if it is in the watch scope (sc-25871). Idempotent, and it never moves an item back: it
    /// writes only while the item still waits in the backlog (<see cref="ReadyResult.Moved"/>); otherwise it writes nothing and says
    /// why — someone moved it already or it is being worked (<see cref="ReadyResult.AlreadyLeft"/>), or it is archived and so can
    /// never be worked (<see cref="ReadyResult.Archived"/>). A source that creates no children does not support it (the default).
    /// </summary>
    Task<ReadyResult> MakeReadyAsync(int id, CancellationToken ct) =>
        throw new NotSupportedException("This work source creates no plan children, so it has none to make ready.");

    /// <summary>
    /// How this board shelves an item for good (what such an item is, and the verb for doing it) and how a person undoes it
    /// (<see cref="ReadyResult.Archived"/>), in the board's own words: what a plan says about a story it could not release
    /// (<see cref="Plans.PlanRelease.HeldArchived"/>), a stopped plan's comment about a released story someone shelved, and the
    /// escalation that tells a person to finish or shelve a broken plan's stories.
    /// </summary>
    (string Is, string Undo, string Do) Shelved => ("archived", "unarchive it", "archive");

    /// <summary>
    /// Whether the item waits in the source's backlog (not archived), where nothing picks it up (sc-25871): how a plan notices a
    /// story it released that a person moved back. Reads only. A source that creates no children has none there (the default).
    /// </summary>
    Task<bool> InBacklogAsync(int id, CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Moves an item <see cref="MakeReadyAsync"/> released back to the backlog and posts <paramref name="comment"/> on it (sc-25876: its
    /// plan was stopped before anything claimed it) — only while the board still shows it as the release left it: not archived, in
    /// the ready state, and with no owner or claim but the factory's. Otherwise it writes nothing and returns false: someone took it,
    /// finished it or moved it, and it is theirs. Re-reads the item first. A source that creates no children has none to return
    /// (the default).
    /// </summary>
    Task<bool> ReturnUnstartedAsync(int id, string comment, CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Whether <paramref name="epic"/> can be an epic of this source (sc-25985), asked before an epic-scope Pause or Stop writes
    /// anything. True (the default) for a source whose epics are named by their own number on the board (Shortcut: any number may
    /// be an epic, and one may be paused before the factory has seen it); a source whose epics are named by a ledger key answers
    /// whether the ledger gave that key out — a control on any other key names nothing today and would land on whichever epic is
    /// given the key later. Reads the ledger only, never the board.
    /// </summary>
    Task<bool> KnowsEpicAsync(long epic, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>Links to one item on its board (<see cref="IBoardLinks"/>); null where the ledger does not hold what the link needs.</summary>
public sealed record BoardLink(string? Item, string? Epic);

/// <summary>
/// A source's links to its items for the dashboard (sc-25985), built from the ledger alone: the dashboard reads the ledger and
/// never asks a board (E8). <see cref="OfAsync"/> answers for the items of its own source among those given, by the work
/// item's ledger id; an item it leaves out is shown without a link.
/// </summary>
public interface IBoardLinks
{
    Task<IReadOnlyDictionary<long, BoardLink>> OfAsync(Ledger.LedgerDbContext db, IReadOnlyList<Ledger.WorkItem> items, CancellationToken ct);
}

/// <summary>
/// Makes untrusted one-line text inert everywhere the factory writes it on GitHub — commit messages and pull request titles (plain
/// text GitHub still links) as well as Markdown: one line, no control or format characters, and none of the characters a mention
/// (<c>@</c>), an issue reference or closing keyword (<c>#</c>, <c>://</c>), a link or image (<c>[ ]</c>), HTML (<c>&lt; &gt;</c>) or a
/// code span (<c>`</c>) needs: each is swapped for a look-alike that GitHub does not read. In Markdown, also put it in a code span.
/// </summary>
public static partial class UntrustedText
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();

    public static string Inert(string text)
    {
        var line = new System.Text.StringBuilder(text.Length);
        foreach (var c in Whitespace().Replace(text, " ").Trim())
        {
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }
            line.Append(c switch
            {
                '@' => '\uFF20', // fullwidth commercial at
                '#' => '\uFF03', // fullwidth number sign
                '[' => '\uFF3B', // fullwidth left square bracket
                ']' => '\uFF3D', // fullwidth right square bracket
                '<' => '\u2039', // single left-pointing angle quotation mark
                '>' => '\u203A', // single right-pointing angle quotation mark
                '`' => '\'',
                _ => c,
            });
        }
        return line.ToString().Replace("://", ":\u2044\u2044", StringComparison.Ordinal); // fraction slashes
    }

    /// <summary><see cref="Inert"/> text in a Markdown code span (it has no backtick left to close it).</summary>
    public static string CodeSpan(string text) => $"`{Inert(text)}`";

    /// <summary>
    /// Untrusted multi-line text inside a tilde fence nothing in it can close: kept verbatim (line breaks too), but no mention, link or
    /// markup in it renders.
    /// </summary>
    public static string Fenced(string text) => $"~~~~text\n{text.Replace("~~~", "~ ~ ~", StringComparison.Ordinal)}\n~~~~";
}

public static class WorkSourceComments
{
    public const string Author = "[author: dark-factory]";

    public static string Attributed(string text) =>
        text.StartsWith(Author, StringComparison.Ordinal) ? text : $"{Author} {text}";
}

/// <summary>An item the board relates to another as its blocker (<see cref="IWorkSource.BlockersAsync"/>).</summary>
/// <param name="Finished">The board shows it finished: in the source's done state (Shortcut: a workflow state of type <c>done</c>) and not archived.</param>
public sealed record Blocker(int Id, bool Finished);

/// <summary>What <see cref="IWorkSource.MakeReadyAsync"/> found and did.</summary>
public enum ReadyResult
{
    /// <summary>The item waited in the backlog and was moved to the ready state.</summary>
    Moved,
    /// <summary>The item had already left the backlog (moved by hand, or being worked): nothing written.</summary>
    AlreadyLeft,
    /// <summary>The item is archived (Kanban: <c>Closed</c>; <see cref="IWorkSource.Shelved"/>): nothing written, and nothing will ever pick it up.</summary>
    Archived,
}
