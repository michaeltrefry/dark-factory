using System.Globalization;

namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>
/// How one work source's items are named in the ledger and on git: the ledger's <see cref="Ledger.WorkItem.Source"/>, the
/// external id (<c>sc-12</c>, <c>gh-3</c>, <c>kb-4</c>), the item's <c>factory/&lt;id&gt;</c> branch and its worktree. Ids are numbers
/// within their source; the prefix keeps two sources' items apart everywhere a name is shared (control scopes, branches,
/// worktree directories, the dashboard).
/// </summary>
/// <param name="Source">The ledger's source column.</param>
/// <param name="Prefix">The external id's prefix, e.g. <c>sc</c>.</param>
/// <param name="Noun">What an item is called in prose (prompts, comments), e.g. <c>Shortcut story</c>.</param>
/// <param name="NumberedEpics">
/// Whether the source's epics have a number an epic-scope control names (<c>epic:&lt;n&gt;</c>, <c>factory pause --epic N</c>): such a
/// control reaches only the items of a source that has them.
/// </param>
public sealed record ItemNaming(string Source, string Prefix, string Noun, bool NumberedEpics = false)
{
    /// <summary>
    /// Whether the source's items are written on the owner's own board, by people the owner lets write there (sc-25984, E5): the one
    /// place a source's trust is decided. A trusted source's item is the owner's text — its name and description are instructions
    /// to a worker, unfenced, and its name is written on GitHub as it is — and only a trusted source may plan
    /// (<see cref="IWorkSource.PlansItems"/>). An untrusted source's item (a GitHub issue: anyone may file one) is data everywhere
    /// (<see cref="WorkStory.UntrustedName"/>). False unless the source says otherwise (fail closed). Whatever the source, text a
    /// planning model wrote stays untrusted (<see cref="WorkStory.PlannerAuthored"/>).
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>Shortcut stories: <c>sc-&lt;story id&gt;</c>.</summary>
    public static readonly ItemNaming Shortcut = new("shortcut", "sc", "Shortcut story", NumberedEpics: true) { Trusted = true };

    /// <summary>GitHub issues: <c>gh-&lt;key&gt;</c>, the key the ledger gave the issue (<see cref="Ledger.GitHubIssue"/>), not its number.</summary>
    public static readonly ItemNaming GitHubIssue = new("github", "gh", "GitHub issue");

    /// <summary>
    /// KanbanBoard work items (sc-25981): <c>kb-&lt;key&gt;</c>, the key the ledger gave the item (<see cref="Ledger.KanbanItem"/>) —
    /// its id on the board is a GUID, and a GUID names no branch or control scope a person types.
    /// </summary>
    public static readonly ItemNaming Kanban = new("kanban", "kb", "Kanban item") { Trusted = true };

    /// <summary>Every source's naming.</summary>
    public static readonly IReadOnlyList<ItemNaming> All = [Shortcut, GitHubIssue, Kanban];

    public string Format(int id) => $"{Prefix}-{id}";

    public string BranchName(int id) => $"factory/{Format(id)}";

    /// <summary>The prefix of every changeset branch (<see cref="ChangesetBranchName"/>).</summary>
    public const string ChangesetBranchPrefix = "factory/changeset-";

    /// <summary>
    /// A plan item's changeset branch (sc-25872), <c>factory/changeset-&lt;id&gt;</c>: under <c>factory/</c>, the only place the
    /// factory's App may write, and never an item's own branch (no id starts with <c>changeset-</c>).
    /// </summary>
    public string ChangesetBranchName(int id) => $"{ChangesetBranchPrefix}{Format(id)}";

    /// <summary>Whether <paramref name="branch"/> is a changeset branch: stories merge into it, so it is never force-pushed.</summary>
    public static bool IsChangesetBranch(string? branch) => branch?.StartsWith(ChangesetBranchPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The item a worktree directory belongs to: <c>factory-sc-7</c> (the item's own branch) or <c>factory-changeset-sc-7</c> (the
    /// plan item's changeset branch, which its changeset pull request's fix rounds work in); null for any other name.
    /// </summary>
    public static ItemRef? OfWorktree(string worktreeName)
    {
        const string prefix = "factory-";
        const string changeset = "changeset-";
        if (!worktreeName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var name = worktreeName[prefix.Length..];
        return ParseAny(name.StartsWith(changeset, StringComparison.Ordinal) ? name[changeset.Length..] : name);
    }

    /// <summary>What follows <c>factory-</c> in the name of a triage's worktree and of a planner's seat, before the item.</summary>
    private static readonly string[] SeatPrefixes = ["triage-", "plan-"];

    private static readonly System.Text.RegularExpressions.Regex GateRun = new("^gate-(?<item>[a-z]+-[0-9]+)(-[a-z0-9-]*)?$");

    /// <summary>
    /// The item whose run made a directory under a worktrees root (sc-25987): what <see cref="OfWorktree"/> names, a triage's
    /// worktree (<c>factory-triage-gh-3</c>), a planner's seat (<c>factory-plan-sc-7</c>) or one of the gate's test worktrees
    /// (<c>gate-sc-7-base</c>, <c>-head</c>, <c>-base-retry</c>, <c>-base-retry-2</c>: <see cref="Gate.NewTestsCheck.RunName"/>);
    /// null for any other name. It says whose run lock guards the directory, never whether a run would resume in it.
    /// </summary>
    public static ItemRef? OwnerOfLeftover(string worktreeName)
    {
        const string prefix = "factory-";
        if (OfWorktree(worktreeName) is { } item)
        {
            return item;
        }
        if (GateRun.Match(worktreeName) is { Success: true } gate)
        {
            return Exact(gate.Groups["item"].Value);
        }
        return worktreeName.StartsWith(prefix, StringComparison.Ordinal)
            && SeatPrefixes.FirstOrDefault(p => worktreeName.AsSpan(prefix.Length).StartsWith(p, StringComparison.Ordinal)) is { } seat
            ? Exact(worktreeName[(prefix.Length + seat.Length)..])
            : null;
    }

    /// <summary>
    /// The item an MCP session file is named for, <c>&lt;item&gt;.&lt;guid&gt;.json</c> (<see cref="Mcp.McpProxySessions"/>); null for
    /// any other name (a file written before sessions were named for their item is nobody's).
    /// </summary>
    public static ItemRef? OwnerOfSessionFile(string fileName) =>
        fileName.Split('.') is [var item, _, "json"] ? Exact(item) : null;

    /// <summary>An external id exactly as <see cref="Format"/> writes it (<c>sc-7</c>): never a bare number, another case or padding.</summary>
    private static ItemRef? Exact(string value) => ParseAny(value) is { } item && item.ToString() == value ? item : null;

    /// <summary>
    /// Parses <c>&lt;prefix&gt;-&lt;n&gt;</c> (any case). A bare number is a Shortcut story id (what <c>factory run 1234</c>
    /// always meant); other sources need their prefix.
    /// </summary>
    public bool TryParse(string? value, out int id)
    {
        id = 0;
        var text = value?.Trim() ?? "";
        if (text.StartsWith(Prefix + "-", StringComparison.OrdinalIgnoreCase))
        {
            text = text[(Prefix.Length + 1)..];
        }
        else if (this != Shortcut)
        {
            return false;
        }
        return text.Length > 0 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>The naming and id of an external id of any source, or null.</summary>
    public static ItemRef? ParseAny(string? value)
    {
        foreach (var naming in All)
        {
            if (naming.TryParse(value, out var id))
            {
                return new ItemRef(naming, id);
            }
        }
        return null;
    }
}

/// <summary>One item of one source, e.g. <c>gh-3</c>.</summary>
public sealed record ItemRef(ItemNaming Naming, int Id)
{
    public override string ToString() => Naming.Format(Id);
}
