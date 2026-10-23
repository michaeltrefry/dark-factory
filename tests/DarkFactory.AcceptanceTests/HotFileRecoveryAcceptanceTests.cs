using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using DarkFactory.Orchestrator.Shortcut;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Epic sc-26335 AT8's two controlled sandbox runs (sc-26345; docs/acceptance.md, "Autonomous hot-file recovery"): the real
/// <c>factory work</c> host (<see cref="FactoryHost.BuildWork"/>) — real implementers, the real panel and gate, the real merge
/// queue, and the production recovery scheduler with its router high-class assessor — against a throwaway ledger and work root
/// (<see cref="E2e"/>), on a disposable sandbox repository. The hot-file threshold is set to its minimum, 2 (an explicit valid
/// setting: E1), so two owner-written stories that touch one file open the incident. Nothing here writes a control: manual
/// Pause/Continue/Stop stay human-only, and the owner's real ledger, controls and freezes are never read or written.
/// Each test SKIPS without its opt-in (skipped is not passed); a run that ends unresolved, or whose assessor does not find the
/// seeded defect (INCONCLUSIVE), FAILS (<see cref="HotFileLive"/>).
/// </summary>
public class HotFileRecoveryAcceptanceTests
{
    private static readonly TimeSpan SafeTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan DefectTimeout = TimeSpan.FromHours(4);

    private static RepoRef SandboxRepo() =>
        RepoRef.Parse(Harness.RequireEnv("FACTORY_E2E_HOTFILE_REPO",
            "the disposable sandbox repository (owner/name) the hot-file runs merge into; both GitHub Apps installed there"));

    private static IReadOnlyList<int> Stories(string variable, string purpose)
    {
        var value = Harness.RequireEnv(variable, purpose);
        var ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => StoryId.TryParse(v, out var id) ? id : throw new InvalidOperationException($"{variable}: '{v}' is not a story id"))
            .ToList();
        Assert.True(ids.Count == 2, $"{variable} must name exactly 2 stories, not {ids.Count}");
        return ids;
    }

    private static Dictionary<string, string?> Overrides(RepoRef repo) => new(E2e.WatchScope())
    {
        ["Factory:DefaultRepo"] = repo.FullName,
        ["Freeze:HotFileMerges"] = "2",
        ["Freeze:HotFileWindowHours"] = "24",
    };

    private static async Task RequireStateAsync(IEnumerable<int> stories, string wanted, CancellationToken ct)
    {
        foreach (var story in stories)
        {
            var (state, _) = await E2e.StoryAsync(story, ct);
            if (state != wanted)
            {
                Assert.Skip($"Story {StoryId.Format(story)} is {state}, not {wanted}: write fresh stories (docs/acceptance.md, \"Autonomous hot-file recovery\").");
            }
        }
    }

    /// <summary>
    /// Runs the host until the repository's incident ends (Resolved or Escalated), then returns the ledger's evidence. With
    /// <paramref name="releaseAfterFirstMerge"/>, that Backlog story is moved to To Do (the factory's own Shortcut source,
    /// <see cref="IWorkSource.MakeReadyAsync"/>) only once <paramref name="first"/>'s Merge row is in the ledger: the factory
    /// does not order ready work by priority, so this, not a story's Priority, makes the defect merge first (HotFileLive.OrderProblem).
    /// </summary>
    private static async Task<HotFileLive.Evidence> RunUntilIncidentEndsAsync(E2e e2e, RepoRef repo, TimeSpan timeout, CancellationToken ct,
        int? first = null, int? releaseAfterFirstMerge = null)
    {
        var options = e2e.Options(Overrides(repo));
        await using var app = FactoryHost.BuildWork(options);
        await app.StartAsync(ct);
        try
        {
            if (first is { } d1 && releaseAfterFirstMerge is { } d2)
            {
                await E2e.WaitForAsync($"{StoryId.Format(d1)} to merge before {StoryId.Format(d2)} is released", timeout, async () =>
                    (await e2e.TransitionsAsync(d1, ct)).Any(e => e.State == WorkState.Merge), ct);
                using var sources = new WorkSourceRegistry(options);
                var released = await sources.Of(ItemNaming.Shortcut).MakeReadyAsync(d2, ct);
                Assert.True(released == ReadyResult.Moved, $"{StoryId.Format(d2)} could not be released to To Do: {released}");
            }
            await E2e.WaitForAsync($"the hot-file incident in {repo.FullName} to end", timeout, async () =>
            {
                await using var db = e2e.Db();
                var incidents = await db.HotFileIncidents.AsNoTracking().ToListAsync(ct);
                return incidents.Any(i => repo.Is(RepoRef.Parse(i.Repo)) && i.State is "Resolved" or "Escalated");
            }, ct);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
        await using var read = e2e.Db();
        return await HotFileLive.ReadAsync(read, ct);
    }

    private static async Task<long?> MergeRowAsync(LedgerDbContext db, int story, CancellationToken ct)
    {
        var id = StoryId.Format(story);
        return await db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && e.State == WorkState.Merge
                && db.WorkItems.Any(i => i.Id == e.WorkItemId && i.ExternalId == id))
            .OrderBy(e => e.Id).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);
    }

    private static async Task AssertNoGlobalHotFileFreezeAsync(E2e e2e, CancellationToken ct)
    {
        // E1/E6: a repository incident never writes the factory-wide freeze row.
        await using var db = e2e.Db();
        var freeze = await db.Controls.AsNoTracking().SingleOrDefaultAsync(c => c.Scope == ControlScope.Freeze, ct);
        Assert.True(freeze is null || freeze.State == ControlState.Running, $"the global freeze row was written: {freeze?.Reason}");
    }

    /// <summary>
    /// AT8 safe case: two stories each append one line to the same file; the second merge opens the incident, a fresh router
    /// high-class read-only assessment finds both merges conform to their stories, and the incident resolves with no correction.
    /// </summary>
    [Fact]
    public async Task AT8_safe_two_conforming_merges_of_one_file_are_assessed_safe_on_high_and_resolved()
    {
        Harness.RequireOptIn();
        var repo = SandboxRepo();
        var stories = Stories("FACTORY_E2E_HOTFILE_SAFE_STORIES",
            "two To Do stories in the watch scope, each appending one line to docs/hotfile-e2e.md in the sandbox repository");
        await using var e2e = await E2e.StartAsync("df_e2e_hotfile_safe");
        var ct = TestContext.Current.CancellationToken;
        await RequireStateAsync(stories, "To Do", ct);

        var evidence = await RunUntilIncidentEndsAsync(e2e, repo, SafeTimeout, ct);
        var problems = HotFileLive.SafeProblems(evidence, repo);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        await AssertNoGlobalHotFileFreezeAsync(e2e, ct);
        await using var db = e2e.Db();
        foreach (var story in stories)
        {
            var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(story), ct);
            Assert.Contains(await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).ToListAsync(ct),
                e => e.Step == null && e.State == WorkState.Merge);
        }
    }

    /// <summary>
    /// AT8 merged-defect case: the first story's exact instruction merges code that violates a requirement stated in its epic
    /// (docs/acceptance.md gives the seed); the second story touches the same file and opens the incident. The real assessor must
    /// find the merged defect (else INCONCLUSIVE, a failure), the factory creates exactly one correction item in the sandbox
    /// repository, the real implementer repairs it, the ordinary panel/CI/new-tests/gate/merge queue merge it, and a fresh
    /// high-class reassessment of the delivered commit is safe.
    /// </summary>
    [Fact]
    public async Task AT8_merged_defect_is_corrected_through_the_gate_and_a_fresh_high_assessment_resolves_it()
    {
        Harness.RequireOptIn();
        var repo = SandboxRepo();
        var stories = Stories("FACTORY_E2E_HOTFILE_DEFECT_STORIES",
            "two stories of the watch scope's seeded defect epic (docs/acceptance.md), in this order: the defective HotFileCount story "
            + "(d1, To Do), then the second HotFileCount story (d2, Backlog: the harness releases it after d1 merges)");
        await using var e2e = await E2e.StartAsync("df_e2e_hotfile_defect");
        var ct = TestContext.Current.CancellationToken;
        await RequireStateAsync([stories[0]], "To Do", ct);
        await RequireStateAsync([stories[1]], "Backlog", ct);

        var evidence = await RunUntilIncidentEndsAsync(e2e, repo, DefectTimeout, ct, first: stories[0], releaseAfterFirstMerge: stories[1]);
        var problems = HotFileLive.DefectProblems(evidence, repo);
        await using (var order = e2e.Db())
            problems.AddRange(HotFileLive.OrderProblem(await MergeRowAsync(order, stories[0], ct), await MergeRowAsync(order, stories[1], ct)));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        await AssertNoGlobalHotFileFreezeAsync(e2e, ct);
        await using var db = e2e.Db();
        var delivered = evidence.Corrections.Single(c => c.MergeRow is not null);
        var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == ItemNaming.Recovery.Format(delivered.Id), ct);
        Assert.True(repo.Is(RepoRef.Parse(item.Repo)), $"the correction was worked in {item.Repo}, not the incident's repository");
    }
}
