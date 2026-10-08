using System.Globalization;

namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>
/// How one work source's items are named in the ledger and on git: the ledger's <see cref="Ledger.WorkItem.Source"/>, the
/// external id (<c>sc-12</c>, <c>gh-3</c>), the item's <c>factory/&lt;id&gt;</c> branch and its worktree. Ids are numbers
/// within their source; the prefix keeps two sources' items apart everywhere a name is shared (control scopes, branches,
/// worktree directories, the dashboard).
/// </summary>
/// <param name="Source">The ledger's source column.</param>
/// <param name="Prefix">The external id's prefix, e.g. <c>sc</c>.</param>
/// <param name="Noun">What an item is called in prose (prompts, comments), e.g. <c>Shortcut story</c>.</param>
public sealed record ItemNaming(string Source, string Prefix, string Noun)
{
    /// <summary>Shortcut stories: <c>sc-&lt;story id&gt;</c>.</summary>
    public static readonly ItemNaming Shortcut = new("shortcut", "sc", "Shortcut story");

    /// <summary>GitHub issues: <c>gh-&lt;key&gt;</c>, the key the ledger gave the issue (<see cref="Ledger.GitHubIssue"/>), not its number.</summary>
    public static readonly ItemNaming GitHubIssue = new("github", "gh", "GitHub issue");

    /// <summary>Every source's naming.</summary>
    public static readonly IReadOnlyList<ItemNaming> All = [Shortcut, GitHubIssue];

    public string Format(int id) => $"{Prefix}-{id}";

    public string BranchName(int id) => $"factory/{Format(id)}";

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
