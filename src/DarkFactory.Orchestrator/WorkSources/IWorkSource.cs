namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>One work item (a Shortcut story, or a triaged GitHub issue) as the orchestrator sees it.</summary>
/// <param name="Naming">The item's source naming; null is a Shortcut story.</param>
/// <param name="Closes">
/// The issue the item's PR closes on merge, as GitHub's closing keyword takes it (<c>owner/name#12</c>); null for none.
/// </param>
public sealed record WorkStory(int Id, string Name, string? Description, string StoryType, string AppUrl, ItemNaming? Naming = null,
    string? Closes = null)
{
    public ItemNaming Kind => Naming ?? ItemNaming.Shortcut;

    /// <summary>
    /// The name as the factory writes it on GitHub (commit message, pull request title and description): a GitHub issue's name is
    /// the triage's title, model text derived from untrusted issue text, so it is made inert (<see cref="UntrustedText.Inert"/>).
    /// </summary>
    public string PublicName => Kind == ItemNaming.Shortcut ? Name : UntrustedText.Inert(Name);

    /// <summary>Whether <see cref="Name"/> is derived from untrusted text.</summary>
    public bool UntrustedName => Kind != ItemNaming.Shortcut;

    /// <summary>The item's external id, e.g. <c>sc-12</c>.</summary>
    public string Ref => Kind.Format(Id);
}

public sealed record WorkEpic(int Id, string Name, string? Description, string AppUrl);

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

/// <summary>A story to create from a plan. <see cref="BlockedBy"/> holds indices of earlier children.</summary>
public sealed record ChildItem(string Name, string Description, string StoryType, IReadOnlyList<int> BlockedBy);

/// <summary>
/// The only way the orchestrator touches a board (E6): the spec's work-sources table.
/// Boards are never synced; change notification is by polling <see cref="ListReadyAsync"/>.
/// </summary>
public interface IWorkSource
{
    /// <summary>How this source's items are named in the ledger and on git (default: Shortcut stories).</summary>
    ItemNaming Naming => ItemNaming.Shortcut;

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

    /// <summary>Items ready for the factory, inside the configured watch scope; never items another claimant holds.</summary>
    Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct);

    /// <summary>
    /// Marks the item as the factory's so nothing else takes it. Idempotent. Re-reads the item first and
    /// refuses, writing nothing, unless it is still ready (or already the factory's), not held by another
    /// claimant and, unless <paramref name="ignoreScope"/>, inside the watch scope; after writing, reads the
    /// claim back and refuses if it did not stick.
    /// </summary>
    Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct);

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
    /// (and blocker relations) an earlier, partly failed call already created.
    /// </summary>
    Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct);
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
