using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>
/// Each commit's prepare steps for the gate's runs (sc-26232), by commit SHA: empty for a commit with no recipe. <see cref="Changes"/>
/// are the PR's changes as the stage read them (the check reuses them: one fetch per check); <see cref="Note"/> says when the base
/// was prepared with the head's recipe (decision A, <see cref="PrepareStage.ForCommitsAsync"/>).
/// </summary>
public sealed class PreparedCommits(IReadOnlyDictionary<string, IReadOnlyList<TestStep>> steps, IReadOnlyList<ChangedFile>? changes = null, string? note = null)
{
    public static readonly PreparedCommits None = new(new Dictionary<string, IReadOnlyList<TestStep>>());

    public IReadOnlyList<TestStep> StepsFor(string commit) => steps.TryGetValue(commit, out var s) ? s : [];

    /// <summary>Whether any commit has a recipe.</summary>
    public bool Any => steps.Values.Any(s => s.Count > 0);

    public IReadOnlyList<ChangedFile>? Changes => changes;

    public string? Note => note;
}

/// <summary>A commit's recipe could not be read, parsed or its artifacts fetched: the gate's check is an <c>error</c> (never a pass), naming the commit.</summary>
public sealed class PrepareFailedException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// The gate's side of a prepare recipe (sc-26232): before the new-tests check's sandboxed runs — and outside the worker slot's lock,
/// since a download needs no sandbox user — each commit's own <see cref="PrepareRecipe.Path"/> is read from the gate's clone (never
/// the PR's word), parsed and its artifacts fetched owner-side into the read-only cache (<see cref="ArtifactFetcher"/>); the steps are
/// then run as the sandbox user in each run of that commit (<see cref="NewTestsCheck.RunAsync"/>). A commit without the file has no
/// steps: its runs are as before (E7). The whole stage is bounded by <see cref="Timeout"/> (<c>Gate:TestTimeoutMinutes</c>).
/// </summary>
public sealed class PrepareStage(ArtifactFetcher fetcher, TimeSpan? timeout = null)
{
    /// <summary>The bound when none is given: <c>Gate:TestTimeoutMinutes</c>' default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    public ArtifactFetcher Fetcher => fetcher;

    public TimeSpan Timeout => timeout ?? DefaultTimeout;

    /// <summary>
    /// The steps of <paramref name="baseSha"/> and <paramref name="headSha"/> (the clone fetched first by reading the PR's changes, as
    /// the check's own <see cref="IGateTestRunner.ChangesAsync"/> does, unless <paramref name="changes"/> are given), each commit by its
    /// own recipe — with one exception (decision A): when the BASE's recipe cannot be fetched (its artifact gone, say) and the PR
    /// changes <see cref="PrepareRecipe.Path"/>, the base is prepared with the HEAD's recipe and <see cref="PreparedCommits.Note"/>
    /// says so, so the PR that fixes a recipe is not refused by the recipe it fixes. <paramref name="onDownloaded"/> is told of each
    /// download. A recipe that does not parse, an artifact that cannot be fetched, or the stage outlasting <see cref="Timeout"/>
    /// throws <see cref="PrepareFailedException"/> naming the commit.
    /// </summary>
    public async Task<PreparedCommits> ForCommitsAsync(IGateTestRunner runner, RepoRef repo, string baseSha, string headSha,
        Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct, IReadOnlyList<ChangedFile>? changes = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Timeout);
        try
        {
            return await PrepareAsync(runner, repo, baseSha, headSha, onDownloaded, changes, bounded.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PrepareFailedException(
                $"the commits' prepare recipes were not ready within {(Timeout.TotalMinutes >= 1 ? $"{Timeout.TotalMinutes:0.##} minutes" : $"{Timeout.TotalSeconds:0.##} seconds")} (Gate:TestTimeoutMinutes), so nothing was run", ex);
        }
    }

    private async Task<PreparedCommits> PrepareAsync(IGateTestRunner runner, RepoRef repo, string baseSha, string headSha,
        Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, IReadOnlyList<ChangedFile>? changes, CancellationToken ct)
    {
        changes ??= await runner.ChangesAsync(repo, baseSha, headSha, ct);
        async Task<IReadOnlyList<TestStep>> StepsOf(string commit)
        {
            if (await runner.ReadAsync(repo, commit, PrepareRecipe.Path, ct) is not { } text)
            {
                return [];
            }
            var recipe = PrepareRecipe.Parse(text);
            return PrepareSteps.TestSteps(recipe, await fetcher.EnsureAsync(recipe, onDownloaded, ct));
        }
        static PrepareFailedException Failed(string commit, Exception ex) => new($"{Ci.Short(commit)}'s prepare recipe: {ex.Message}", ex);

        var steps = new Dictionary<string, IReadOnlyList<TestStep>>(StringComparer.Ordinal);
        IReadOnlyList<TestStep>? headSteps = null;
        string? note = null;
        var recipeChanged = changes.Any(c => c.Paths.Contains(PrepareRecipe.Path, StringComparer.Ordinal));
        try
        {
            steps[baseSha] = await StepsOf(baseSha);
        }
        catch (ArtifactFetchException ex) when (headSha != baseSha && recipeChanged)
        {
            // Decision A: the base's own recipe cannot be fetched any more and the PR changes the recipe — the base is prepared
            // with the head's (its artifacts and its steps), said in the result. A step that fails is no fetch failure: never here.
            try
            {
                headSteps = await StepsOf(headSha);
            }
            catch (Exception inner) when (inner is PrepareRecipeException or ArtifactFetchException)
            {
                throw Failed(headSha, inner);
            }
            steps[baseSha] = headSteps;
            note = $"base prepared with the head's recipe: the base's own recipe could not be fetched: {ex.Message}";
        }
        catch (Exception ex) when (ex is PrepareRecipeException or ArtifactFetchException)
        {
            throw Failed(baseSha, ex);
        }
        if (headSha != baseSha)
        {
            try
            {
                steps[headSha] = headSteps ?? await StepsOf(headSha);
            }
            catch (Exception ex) when (ex is PrepareRecipeException or ArtifactFetchException)
            {
                throw Failed(headSha, ex);
            }
        }
        return new PreparedCommits(steps, changes, note);
    }
}
