using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A repository's files at a few commits, in memory, as a reviewer's tools read them (<see cref="IReviewFiles"/>), recording every
/// read, listing and search (commit, path or directory).
/// </summary>
public sealed class FakeReviewFiles(Dictionary<string, Dictionary<string, string>> commits) : IReviewFiles
{
    /// <summary>One commit's files.</summary>
    public FakeReviewFiles(string sha, Dictionary<string, string> files) : this(new Dictionary<string, Dictionary<string, string>> { [sha] = files })
    {
    }

    public List<(RepoRef Repo, string Sha, string Path)> Reads { get; } = [];
    public List<(string Sha, string? Directory)> Listings { get; } = [];
    public List<(string Sha, string Pattern, string? Directory)> Searches { get; } = [];

    private IEnumerable<KeyValuePair<string, string>> Under(string sha, string? directory) =>
        commits.GetValueOrDefault(sha, []).Where(f => directory is null || f.Key == directory || f.Key.StartsWith(directory + "/", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal);

    public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct)
    {
        Reads.Add((repo, sha, path));
        return Task.FromResult(commits.GetValueOrDefault(sha)?.GetValueOrDefault(path));
    }

    public Task<Bounded<string>> ListAsync(RepoRef repo, string sha, string? directory, CancellationToken ct)
    {
        Listings.Add((sha, directory));
        var paths = Under(sha, directory).Select(f => f.Key).ToList();
        return Task.FromResult(new Bounded<string>(paths.Take(ReviewTools.MaxListedFiles).ToList(), paths.Count > ReviewTools.MaxListedFiles));
    }

    public Task<Bounded<GrepMatch>> GrepAsync(RepoRef repo, string sha, string pattern, string? directory, int perFile, CancellationToken ct)
    {
        Searches.Add((sha, pattern, directory));
        var regex = new Regex(pattern);
        var matches = Under(sha, directory)
            .SelectMany(f => f.Value.Split('\n').Select((line, i) => new GrepMatch(f.Key, i + 1, line)).Where(m => regex.IsMatch(m.Text)).Take(perFile))
            .ToList();
        return Task.FromResult(new Bounded<GrepMatch>(matches.Take(ReviewTools.MaxGrepMatches).ToList(), matches.Count > ReviewTools.MaxGrepMatches));
    }
}
