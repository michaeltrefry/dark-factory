namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>One work item (a Shortcut story) as the orchestrator sees it.</summary>
public sealed record WorkStory(int Id, string Name, string? Description, string StoryType, string AppUrl);

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

public static class WorkSourceComments
{
    public const string Author = "[author: dark-factory]";

    public static string Attributed(string text) =>
        text.StartsWith(Author, StringComparison.Ordinal) ? text : $"{Author} {text}";
}
