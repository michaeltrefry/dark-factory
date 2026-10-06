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
}
