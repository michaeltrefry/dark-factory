using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>Each commit's prepare steps for the gate's runs (sc-26232), by commit SHA: empty for a commit with no recipe.</summary>
public sealed class PreparedCommits(IReadOnlyDictionary<string, IReadOnlyList<TestStep>> steps)
{
    public static readonly PreparedCommits None = new(new Dictionary<string, IReadOnlyList<TestStep>>());

    public IReadOnlyList<TestStep> StepsFor(string commit) => steps.TryGetValue(commit, out var s) ? s : [];

    /// <summary>Whether any commit has a recipe.</summary>
    public bool Any => steps.Values.Any(s => s.Count > 0);
}

/// <summary>A commit's recipe could not be read, parsed or its artifacts fetched: the gate's check is an <c>error</c> (never a pass), naming the commit.</summary>
public sealed class PrepareFailedException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// The gate's side of a prepare recipe (sc-26232): before the new-tests check's sandboxed runs — and outside the worker slot's lock,
/// since a download needs no sandbox user — each commit's own <see cref="PrepareRecipe.Path"/> is read from the gate's clone (never
/// the PR's word), parsed and its artifacts fetched owner-side into the read-only cache (<see cref="ArtifactFetcher"/>); the steps are
/// then run as the sandbox user in each run of that commit (<see cref="NewTestsCheck.RunAsync"/>). A commit without the file has no
/// steps: its runs are as before (E7).
/// </summary>
public sealed class PrepareStage(ArtifactFetcher fetcher)
{
    public ArtifactFetcher Fetcher => fetcher;

    /// <summary>
    /// The steps of <paramref name="baseSha"/> and <paramref name="headSha"/> (the clone fetched first, as the check's own
    /// <see cref="IGateTestRunner.ChangesAsync"/> does, so both commits are there to be read); <paramref name="onDownloaded"/> is told
    /// of each download. A recipe that does not parse, or an artifact that cannot be fetched, throws
    /// <see cref="PrepareFailedException"/> naming the commit.
    /// </summary>
    public async Task<PreparedCommits> ForCommitsAsync(IGateTestRunner runner, RepoRef repo, string baseSha, string headSha,
        Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct)
    {
        await runner.ChangesAsync(repo, baseSha, headSha, ct);
        var steps = new Dictionary<string, IReadOnlyList<TestStep>>(StringComparer.Ordinal);
        foreach (var commit in new[] { baseSha, headSha }.Distinct(StringComparer.Ordinal))
        {
            if (await runner.ReadAsync(repo, commit, PrepareRecipe.Path, ct) is not { } text)
            {
                steps[commit] = [];
                continue;
            }
            try
            {
                var recipe = PrepareRecipe.Parse(text);
                steps[commit] = PrepareSteps.TestSteps(recipe, await fetcher.EnsureAsync(recipe, onDownloaded, ct));
            }
            catch (Exception ex) when (ex is PrepareRecipeException or ArtifactFetchException)
            {
                throw new PrepareFailedException($"{Ci.Short(commit)}'s prepare recipe: {ex.Message}", ex);
            }
        }
        return new PreparedCommits(steps);
    }
}
