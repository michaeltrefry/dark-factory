using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.AcceptanceTests;

/// <summary>Pure maps/fake App operations: publication never means that the native acceptance passed.</summary>
public sealed class S6FixturePublisherTests
{
    private static readonly RepoRef Repo = new("fixture", "source");
    private static readonly GuiWorkflowSettings Settings = new(new("fixture", "collector"), [Repo], new string('a', 64));
    private const string Campaign = "1234567890abcdef1234567890abcdef";

    [Fact]
    public void Generated_fixture_repository_defaults_to_the_configured_controller_owner_with_an_explicit_override()
    {
        Assert.Equal(new RepoRef("fixture", "dark-factory-s6-fixtures"), S6FixtureProvisioning.FixtureRepository(Settings, null));
        Assert.Equal(new RepoRef("other", "approved-fixtures"), S6FixtureProvisioning.FixtureRepository(Settings, "other/approved-fixtures"));
    }

    [Fact]
    public async Task Every_generated_pair_is_discovered_with_the_exact_native_test_ids_and_execution_placement()
    {
        foreach (var plan in S6FixturePublisher.Plans())
        {
            var head = new Dictionary<string, string>(plan.BaseFiles, StringComparer.Ordinal);
            foreach (var file in plan.HeadChanges) head[file.Key] = file.Value;
            var maps = new Trees(plan.BaseFiles, head);
            var changes = await maps.ChangesAsync(Repo, "base", "head", TestContext.Current.CancellationToken);
            var set = await new XcodeNewTests().IdentifyAsync(new TestSource(maps, Repo, "base", "head"), changes, TestContext.Current.CancellationToken);
            Assert.Empty(set.Unisolable);
            Assert.Equal(plan.Tests.Order(StringComparer.Ordinal), set.Tests.Order(StringComparer.Ordinal));
            Assert.All(set.Tests, test => Assert.Equal(plan.Kind == "hosted-ios" ? XcodeTestPlacement.Hosted : XcodeTestPlacement.Gui,
                set.XcodePlacements[test]));
            Assert.All(set.Projects.Values, project => Assert.Equal(plan.Kind is "hosted-ios" or "gui-ios-ui", project.Split('\n').Last() == XcodeProject.IOS));
            Assert.Equal(0, maps.Runs);
        }
    }

    [Fact]
    public void Hosted_fixture_uses_native_environment_without_linking_the_executable_app()
    {
        var hosted = Assert.Single(S6FixturePublisher.Plans(), p => p.Kind == "hosted-ios");
        Assert.Equal(["TallyTests/HeadlessContractTests/testCommitOwnedEnvironment()"], hosted.Tests);
        var tests = hosted.BaseFiles.Concat(hosted.HeadChanges).Where(f => f.Key.StartsWith("TallyTests/", StringComparison.Ordinal)
            && f.Key.EndsWith(".swift", StringComparison.Ordinal)).Select(f => f.Value).ToArray();
        Assert.NotEmpty(tests);
        Assert.All(tests, source =>
        {
            Assert.DoesNotContain("import Tally", source);
            Assert.DoesNotContain("Counter.", source);
        });
        Assert.Contains("ProcessInfo.processInfo.environment[\"REPO_MODE\"]", hosted.HeadChanges["TallyTests/HeadlessContractTests.swift"]);
        Assert.Equal("base", DarkFactory.Orchestrator.Prepare.PrepareRecipe.Parse(hosted.BaseFiles[".factory/prepare.yaml"]).TestEnvironment["REPO_MODE"]);
        Assert.Equal("head", DarkFactory.Orchestrator.Prepare.PrepareRecipe.Parse(hosted.HeadChanges[".factory/prepare.yaml"]).TestEnvironment["REPO_MODE"]);
        Assert.DoesNotContain("Tally/Counter.swift", hosted.HeadChanges.Keys);
    }

    [Fact]
    public async Task Repeating_a_campaign_preserves_actual_commit_receipts_without_new_writes_or_branches()
    {
        var remote = new FakeRepository();
        var publisher = new S6FixturePublisher(Repo, Settings, remote);
        var first = await publisher.PublishAsync(Campaign, S6FixturePublisher.Plans(), TestContext.Current.CancellationToken);
        var writes = remote.Writes;
        var repeated = await publisher.PublishAsync(Campaign, S6FixturePublisher.Plans(), TestContext.Current.CancellationToken);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
        Assert.Equal(writes, remote.Writes);
        Assert.Equal(10, remote.Branches.Count);
        Assert.All(first.Rows, row => { Assert.True(RemoteIosWorkflow.Sha(row.Fixture.Base)); Assert.True(RemoteIosWorkflow.Sha(row.Fixture.Head)); Assert.NotEqual(row.Fixture.Base, row.Fixture.Head); });
    }

    [Fact]
    public async Task Lost_file_write_answer_is_read_back_on_retry_without_a_duplicate_commit()
    {
        var plans = S6FixturePublisher.Plans();
        var remote = new FakeRepository { LoseAnswerAt = plans[0].BaseFiles.Count + 1 };
        var publisher = new S6FixturePublisher(Repo, Settings, remote);
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(Campaign, plans, TestContext.Current.CancellationToken));
        var publication = await publisher.PublishAsync(Campaign, plans, TestContext.Current.CancellationToken);
        Assert.Equal(5, publication.Rows.Count);
        Assert.Equal(plans.Sum(p => p.BaseFiles.Count + p.HeadChanges.Count), remote.Writes);
        Assert.Equal(10, remote.Branches.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_allowlist_or_deployment_refusal_precedes_every_remote_write(bool deployment)
    {
        var remote = new FakeRepository { Authorized = !deployment };
        var settings = deployment ? Settings : Settings with { TrustedRepositories = [new("fixture", "other")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new S6FixturePublisher(Repo, settings, remote)
            .PublishAsync(Campaign, S6FixturePublisher.Plans(), TestContext.Current.CancellationToken));
        Assert.Empty(remote.Branches);
        Assert.Equal(0, remote.Writes);
        Assert.Equal(deployment ? 1 : 0, remote.AuthorityReads);
    }

    [Fact]
    public async Task A_receipt_cannot_authorize_cleanup_of_a_default_or_foreign_branch()
    {
        var remote = new FakeRepository();
        var publisher = new S6FixturePublisher(Repo, Settings, remote);
        var publication = await publisher.PublishAsync(Campaign, S6FixturePublisher.Plans(), TestContext.Current.CancellationToken);
        var rows = publication.Rows.ToArray();
        rows[0] = rows[0] with { BaseBranch = "main" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.CleanupAsync(publication with { Rows = rows }, TestContext.Current.CancellationToken));
        Assert.Equal(10, remote.Branches.Count);
        Assert.Empty(remote.Deleted);
    }

    [Fact]
    public async Task Cleanup_leaves_a_moved_ref_and_deletes_only_this_campaign_when_unchanged()
    {
        var remote = new FakeRepository();
        var publisher = new S6FixturePublisher(Repo, Settings, remote);
        var publication = await publisher.PublishAsync(Campaign, S6FixturePublisher.Plans(), TestContext.Current.CancellationToken);
        var first = publication.Rows[0];
        remote.Move(first.HeadBranch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.CleanupAsync(publication, TestContext.Current.CancellationToken));
        Assert.Equal(10, remote.Branches.Count);
        Assert.Empty(remote.Deleted);
        remote.Branches[first.HeadBranch] = first.Fixture.Head;
        await publisher.CleanupAsync(publication, TestContext.Current.CancellationToken);
        Assert.Empty(remote.Branches);
        Assert.Equal(10, remote.Deleted.Count);
        Assert.All(remote.Deleted, branch => Assert.StartsWith("factory/s6-" + Campaign + "/", branch));
    }

    private sealed class Trees(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> head) : IGateTestRunner
    {
        public int Runs;
        private IReadOnlyDictionary<string, string> Of(string sha) => sha == "base" ? baseline : head;
        public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>([.. head.Where(f => !baseline.TryGetValue(f.Key, out var old) || old != f.Value)
                .Select(f => new ChangedFile(baseline.ContainsKey(f.Key) ? 'M' : 'A', baseline.ContainsKey(f.Key) ? f.Key : null, f.Key))]);
        public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. Of(sha).Keys]);
        public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) => Task.FromResult(Of(sha).GetValueOrDefault(path));
        public Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct) { Runs++; throw new InvalidOperationException("Fixture discovery executes nothing."); }
    }

    private sealed class FakeRepository : IS6FixtureRepository
    {
        public bool Authorized = true;
        public int Writes, AuthorityReads, LoseAnswerAt;
        public readonly Dictionary<string, string> Branches = new(StringComparer.Ordinal);
        public readonly List<string> Deleted = [];
        private readonly Dictionary<string, Dictionary<string, string>> commits = new(StringComparer.Ordinal);
        private int serial;
        private string Commit(Dictionary<string, string> tree)
        {
            var sha = (++serial).ToString("x40");
            commits[sha] = new(tree, StringComparer.Ordinal);
            return sha;
        }
        public Task VerifyAuthorityAsync(CancellationToken ct)
        {
            AuthorityReads++;
            if (!Authorized) throw new InvalidOperationException("sealed deployment is absent");
            return Task.CompletedTask;
        }
        public Task CreateBranchAsync(string branch, string? start, CancellationToken ct)
        {
            if (!Branches.ContainsKey(branch)) Branches[branch] = start ?? Commit(new(StringComparer.Ordinal));
            return Task.CompletedTask;
        }
        public Task<string> HeadAsync(string branch, CancellationToken ct) => Task.FromResult(Branches[branch]);
        public Task PutIfDifferentAsync(string branch, string path, string content, CancellationToken ct)
        {
            var tree = new Dictionary<string, string>(commits[Branches[branch]], StringComparer.Ordinal);
            if (tree.GetValueOrDefault(path) == content) return Task.CompletedTask;
            tree[path] = content;
            Branches[branch] = Commit(tree);
            Writes++;
            if (Writes == LoseAnswerAt) { LoseAnswerAt = 0; throw new IOException("write succeeded but its answer was lost"); }
            return Task.CompletedTask;
        }
        public Task DeleteIfUnchangedAsync(string branch, string expected, CancellationToken ct)
        {
            if (!Branches.TryGetValue(branch, out var actual)) return Task.CompletedTask;
            if (actual != expected) throw new InvalidOperationException("ref moved");
            Branches.Remove(branch); Deleted.Add(branch);
            return Task.CompletedTask;
        }
        public void Move(string branch) => Branches[branch] = Commit(commits[Branches[branch]]);
    }
}
