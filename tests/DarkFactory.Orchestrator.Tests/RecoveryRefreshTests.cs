using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Recovery;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DarkFactory.Orchestrator.Tests;

public class RecoveryRefreshTests
{
    private const string Repo = "owner/alpha";

    private sealed class RecordingAssessor : IHotFileAssessor
    {
        public readonly List<HotFileSnapshot> Reviewed = [];
        public Func<Task>? DuringAssessment;

        public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
        {
            Reviewed.Add(snapshot);
            if (DuringAssessment is { } change)
            {
                DuringAssessment = null;
                await change();
            }
            return new(snapshot.Id, snapshot.Generation, snapshot.Repo, "high", "safe", snapshot.EvidenceHash);
        }
    }

    [Fact]
    public async Task Refresh_preserves_expired_contributors_and_excludes_over_fifty_unrelated_merges_and_other_bases()
    {
        var f = new HotFileRecoveryTests.Fixture
        {
            Options = new FreezeOptions { HotFileMerges = 2, HotFileWindow = TimeSpan.FromHours(1) }
        };
        await f.Merge(Repo, "main", "src/a.cs");
        await f.Merge(Repo, "main", "src/a.cs");
        var assessor = new RecordingAssessor();
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);
        var incident = Assert.IsType<HotFileIncident>(await recovery.HeldAsync(Repo, CancellationToken.None));
        var original = HotFileSnapshot.Parse(incident.Snapshot);
        f.Time.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 55; i++) await f.Merge(Repo, "main", $"unrelated/{i}.cs");
        await f.Merge(Repo, "other", "src/a.cs");
        await f.Merge(Repo, "main", "src/a.cs", "unrelated/sidecar.cs");
        await using (var db = f.Contexts.CreateDbContext())
        {
            var stale = await db.HotFileIncidents.SingleAsync();
            stale.State = "Refreshing";
            stale.Error = "Base tip changed";
            stale.Version++;
            await db.SaveChangesAsync();
        }

        await recovery.AdvanceAsync(Repo, CancellationToken.None);

        var refreshed = Assert.Single(assessor.Reviewed);
        Assert.Equal(3, refreshed.Merges.Count);
        Assert.All(original.Merges, old => Assert.Contains(refreshed.Merges, m =>
            m.Row == old.Row && m.Item == old.Item && m.Commit == old.Commit && m.Base == old.Base
            && m.Files.SequenceEqual(old.Files)));
        Assert.All(refreshed.Merges, m =>
        {
            Assert.Equal("main", m.Base);
            Assert.Equal(new[] { "src/a.cs" }, m.Files);
        });
        Assert.Null(await recovery.HeldAsync(Repo, CancellationToken.None));
    }

    [Fact]
    public async Task A_newly_hot_file_on_existing_merge_rows_invalidates_acceptance_and_is_reviewed_on_refresh()
    {
        var f = new HotFileRecoveryTests.Fixture
        {
            Options = new FreezeOptions { HotFileMerges = 2 }
        };
        await f.Merge(Repo, "main", "src/a.cs", "src/b.cs");
        await f.Merge(Repo, "main", "src/a.cs");
        var assessor = new RecordingAssessor
        {
            DuringAssessment = async () =>
            {
                for (var i = 0; i < 55; i++) await f.Merge(Repo, "main", $"unrelated/{i}.cs");
                await f.Merge(Repo, "other", "src/b.cs");
                await f.Merge(Repo, "main", "src/b.cs", "unrelated/sidecar.cs");
            }
        };
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        await using (var db = f.Contexts.CreateDbContext())
        {
            Assert.Equal("Refreshing", (await db.HotFileIncidents.SingleAsync()).State);
        }
        var initial = Assert.Single(assessor.Reviewed);
        Assert.All(initial.Merges, m => Assert.Equal(new[] { "src/a.cs" }, m.Files));

        await recovery.AdvanceAsync(Repo, CancellationToken.None);

        Assert.Equal(2, assessor.Reviewed.Count);
        var refreshed = assessor.Reviewed[1];
        Assert.NotEqual(initial.Id, refreshed.Id);
        Assert.Equal(initial.Generation, refreshed.Generation);
        Assert.Equal(3, refreshed.Merges.Count);
        Assert.All(refreshed.Merges, m => Assert.Equal("main", m.Base));
        Assert.Equal(new[] { "src/a.cs", "src/b.cs" }, refreshed.Merges.Single(m => m.Row == initial.Merges[0].Row).Files);
        Assert.Equal(new[] { "src/b.cs" }, refreshed.Merges.Last().Files);
        Assert.Null(await recovery.HeldAsync(Repo, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assessment_freshness_ignores_unrelated_traffic_but_reviews_new_same_base_overlaps(bool overlap)
    {
        var f = new HotFileRecoveryTests.Fixture { Options = new FreezeOptions { HotFileMerges = 2 } };
        await f.Merge(Repo, "main", "src/a.cs");
        await f.Merge(Repo, "main", "src/a.cs");
        var assessor = new RecordingAssessor
        {
            DuringAssessment = async () =>
            {
                for (var i = 0; i < 55; i++) await f.Merge(Repo, "main", $"unrelated/{i}.cs");
                await f.Merge(Repo, "other", "src/a.cs");
                if (overlap) await f.Merge(Repo, "main", "src/a.cs", "unrelated/sidecar.cs");
            }
        };
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);

        await recovery.AdvanceAsync(Repo, CancellationToken.None);

        await using (var db = f.Contexts.CreateDbContext())
            Assert.Equal(overlap ? "Refreshing" : "Resolved", (await db.HotFileIncidents.SingleAsync()).State);
        Assert.Single(assessor.Reviewed);
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Equal(overlap ? 2 : 1, assessor.Reviewed.Count);
        var latest = assessor.Reviewed.Last();
        Assert.Equal(overlap ? 3 : 2, latest.Merges.Count);
        Assert.All(latest.Merges, m =>
        {
            Assert.Equal("main", m.Base);
            Assert.Equal(new[] { "src/a.cs" }, m.Files);
        });
        Assert.Null(await recovery.HeldAsync(Repo, CancellationToken.None));
    }

    [Fact]
    public async Task Already_acknowledged_hot_tuples_do_not_pollute_another_files_refresh()
    {
        var f = new HotFileRecoveryTests.Fixture { Options = new FreezeOptions { HotFileMerges = 2 } };
        await f.Merge(Repo, "other", "src/old.cs");
        await f.Merge(Repo, "other", "src/old.cs");
        var assessor = new RecordingAssessor();
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        await f.Merge(Repo, "main", "src/a.cs");
        await f.Merge(Repo, "main", "src/a.cs");
        await recovery.HeldAsync(Repo, CancellationToken.None);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync(i => i.State != "Resolved");
            incident.State = "Refreshing";
            incident.Version++;
            await db.SaveChangesAsync();
        }

        await recovery.AdvanceAsync(Repo, CancellationToken.None);

        Assert.Equal(2, assessor.Reviewed.Count);
        Assert.All(assessor.Reviewed[1].Merges, m =>
        {
            Assert.Equal("main", m.Base);
            Assert.Equal(new[] { "src/a.cs" }, m.Files);
        });
        Assert.Null(await recovery.HeldAsync(Repo, CancellationToken.None));
    }
}
