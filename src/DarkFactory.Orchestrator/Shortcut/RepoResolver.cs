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
    /// are case-insensitive); <paramref name="defaultRepo"/> alone when it has no such line. The first is <see cref="Resolve"/>'s
    /// unless that one is inside a fenced block: lines inside a Markdown fence (<c>```</c> or <c>~~~</c>) are not read here. A story
    /// the planner created carries planner text in a tilde fence after its code-written <c>Repo:</c> line (<c>UntrustedText.Fenced</c>);
    /// were it later labelled <c>plan</c>, a <c>Repo:</c> line in that text must not widen the next planner's checkouts and CodeGraph
    /// grant. Only a plan item's planner reads more than the first (sc-25874): every other item is worked in one repository.
    /// </summary>
    public static IReadOnlyList<RepoRef> ResolveAll(string? storyDescription, RepoRef defaultRepo)
    {
        var named = RepoLine().Matches(OutsideFences(storyDescription ?? "")).Select(m => RepoRef.Parse(m.Groups[1].Value))
            .DistinctBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        return named.Count > 0 ? named : [defaultRepo];
    }

    /// <summary>
    /// <paramref name="text"/> without its fenced blocks, fence lines included. A fence opens on a line that starts (after any
    /// indentation or quote markers: stricter than Markdown, which only skips more) with three or more backticks or tildes, and is
    /// closed only by a line of at least as many of the same marker and nothing else, as in Markdown; one never closed runs to the end.
    /// </summary>
    private static string OutsideFences(string text)
    {
        var kept = new List<string>();
        var marker = '\0';
        var length = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t', '>').TrimEnd();
            var run = trimmed.Length > 0 && trimmed[0] is '`' or '~' ? trimmed.Length - trimmed.TrimStart(trimmed[0]).Length : 0;
            if (length == 0)
            {
                if (run >= 3)
                {
                    (marker, length) = (trimmed[0], run);
                }
                else
                {
                    kept.Add(line);
                }
            }
            else if (run >= length && trimmed[0] == marker && run == trimmed.Length)
            {
                length = 0;
            }
        }
        return string.Join('\n', kept);
    }
}
