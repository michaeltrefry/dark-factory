using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Shortcut;

public sealed record RepoRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public static RepoRef Parse(string fullName)
    {
        var parts = fullName.Trim().Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"Expected owner/name, got '{fullName}'.", nameof(fullName));
        }
        // Owner and name become path segments under the work root; "." / ".." / dot-prefixed would escape or hide.
        if (parts.Any(p => p.TrimStart().StartsWith('.')))
        {
            throw new ArgumentException($"Repo owner and name may not start with '.', got '{fullName}'.", nameof(fullName));
        }
        return new RepoRef(parts[0], parts[1]);
    }

    public override string ToString() => FullName;
}

/// <summary>
/// A story targets the configured default repo unless its description has a
/// <c>Repo: owner/name</c> line.
/// </summary>
public static partial class RepoResolver
{
    [GeneratedRegex(@"^[ \t>*_-]*Repo:[ \t*_]*`?([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)`?[ \t*_]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RepoLine();

    public static RepoRef Resolve(string? storyDescription, RepoRef defaultRepo)
    {
        var match = RepoLine().Match(storyDescription ?? "");
        return match.Success ? RepoRef.Parse(match.Groups[1].Value) : defaultRepo;
    }

    /// <summary>
    /// Every repository the description names, one per <c>Repo: owner/name</c> line, in order and without repeats (GitHub's names
    /// are case-insensitive); <paramref name="defaultRepo"/> alone when it has no such line. The first is <see cref="Resolve"/>'s.
    /// Only a plan item's planner reads more than the first (sc-25874): every other item is worked in one repository.
    /// </summary>
    public static IReadOnlyList<RepoRef> ResolveAll(string? storyDescription, RepoRef defaultRepo)
    {
        var named = RepoLine().Matches(storyDescription ?? "").Select(m => RepoRef.Parse(m.Groups[1].Value))
            .DistinctBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        return named.Count > 0 ? named : [defaultRepo];
    }
}
