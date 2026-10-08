using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25385: <see cref="GitHubIssuesClient"/> replayed against GitHub fixtures (<c>Fixtures/github</c>, strict request matching),
/// and the token each call holds: reads <c>issues: read</c>, the permission API <c>metadata: read</c>, a file <c>contents: read</c>,
/// every write <c>issues: write</c> and nothing else (E4: no token the triage side holds can push).
/// </summary>
public class GitHubIssuesContractTests
{
    private static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
    private static readonly RepoRef Widgets = new("acme", "widgets");

    private static GitHubIssuesClient Client(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = GitHubApp.DefaultBaseAddress };
        return new GitHubIssuesClient(http, new GitHubApp(http, "4242", Pem, TimeProvider.System));
    }

    [Fact]
    public async Task Every_operation_matches_the_api_and_holds_only_the_token_it_needs()
    {
        var replay = new GitHubReplayHandler(GitHubFixture.Load("issues.json"));
        var github = Client(replay);
        var ct = CancellationToken.None;

        var issues = await github.ListUpdatedAsync(Widgets, DateTimeOffset.Parse("2026-10-08T12:00:00Z"), ct);
        // Pull requests are left out; a null body stays null; labels and the author are read.
        Assert.Equal([11, 12], issues.Select(i => i.Number));
        Assert.Equal(("visitor", false, true, (string?)"Run it with no arguments."), (issues[0].Author, issues[0].AuthorIsBot, issues[0].Open, issues[0].Body));
        Assert.Null(issues[1].Body);
        Assert.Equal(["awaiting-approval"], issues[1].Labels);

        var issue = await github.GetAsync(Widgets, 12, ct);
        Assert.Equal(["awaiting-approval", "bug"], issue.Labels);

        var comments = await github.ListCommentsAsync(Widgets, 12, ct);
        Assert.Equal((4242L, false), (comments[0].AppId!.Value, comments[0].Edited));
        Assert.Equal("0123456789abcdef", IssueComments.TriageHash(comments[0], github.AppId));
        Assert.Equal(((long?)null, true), (comments[1].AppId, comments[1].Edited)); // edited later: it never approves
        Assert.False(IssueComments.IsApproval(comments[1]));

        var maintainer = await github.PermissionAsync(Widgets, "maintainer", ct);
        var triager = await github.PermissionAsync(Widgets, "triager", ct);
        var stranger = await github.PermissionAsync(Widgets, "stranger", ct);
        Assert.Equal(("maintain (write)", true), (maintainer.ToString(), maintainer.IsCollaborator));
        Assert.Equal(("triage (read)", false), (triager.ToString(), triager.IsCollaborator));
        Assert.Equal((RepoPermission.None, false), (stranger, stranger.IsCollaborator));

        Assert.Equal("version: 2\n", await github.GetFileAsync(Widgets, "factory/gate.yaml", ct));
        Assert.Null(await github.GetFileAsync(new RepoRef("acme", "gadgets"), "factory/gate.yaml", ct));

        Assert.Equal(2003, await github.CommentAsync(Widgets, 12, "[author: dark-factory] hello", ct));
        await github.AddLabelsAsync(Widgets, 12, ["needs-human"], ct);
        await github.RemoveLabelAsync(Widgets, 12, "awaiting-approval", ct);
        await github.RemoveLabelAsync(Widgets, 12, "factory-claimed", ct); // not on the issue: not an error
        await github.CloseAsync(Widgets, 12, ct);

        Assert.True(replay.AllReplayed);
        // One fresh token per call, for the one repo, never with more than the call needs.
        Assert.All(replay.Mints, m => Assert.DoesNotContain("contents:write", m));
        Assert.All(replay.Mints, m => Assert.DoesNotContain("pull_requests", m));
        Assert.Equal(13, replay.Mints.Count);
        Assert.Equal(5, replay.Mints.Count(m => m == "widgets issues:write"));
    }

    [Fact]
    public async Task Issue_and_comment_listings_follow_every_page()
    {
        static string Issues(int from, int count) => new JsonArray(Enumerable.Range(from, count).Select(n => (JsonNode)new JsonObject
        {
            ["number"] = n, ["title"] = $"#{n}", ["state"] = "open", ["html_url"] = $"https://github.com/acme/widgets/issues/{n}",
            ["updated_at"] = "2026-10-08T12:00:00Z", ["user"] = new JsonObject { ["login"] = "u" },
        }).ToArray()).ToJsonString();
        var api = new FakeApi()
            .On("GET /repos/acme/widgets/installation", HttpStatusCode.OK, """{"id":1}""")
            .On("POST /app/installations/1/access_tokens", HttpStatusCode.Created, $$"""{"token":"t","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(30):O}}"}""")
            .On("GET /repos/acme/widgets/issues", r => FakeApi.Json(HttpStatusCode.OK, r.PathAndQuery.Contains("&page=1") ? Issues(1, 100) : Issues(101, 3)));
        var github = Client(api);

        var issues = await github.ListUpdatedAsync(Widgets, null, CancellationToken.None);

        Assert.Equal(103, issues.Count);
        var pages = api.Requests.Where(r => r.PathAndQuery.StartsWith("/repos/acme/widgets/issues", StringComparison.Ordinal)).Select(r => r.PathAndQuery).ToList();
        Assert.Equal(["/repos/acme/widgets/issues?state=open&sort=updated&direction=asc&per_page=100&page=1",
            "/repos/acme/widgets/issues?state=open&sort=updated&direction=asc&per_page=100&page=2"], pages);
    }

    [Fact]
    public async Task The_work_source_links_once_needs_a_reason_to_stop_and_names_items_gh()
    {
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        var github = new FakeGitHubIssues(time);
        github.Open(Widgets, 12, "t", "b", "maintainer");
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var db = new LedgerDbContext(options))
        {
            Assert.Equal(1, await IssueIntake.KeyAsync(db, Widgets, 12, CancellationToken.None));
            Assert.Equal(1, await IssueIntake.KeyAsync(db, Widgets, 12, CancellationToken.None));
            Assert.Equal(2, await IssueIntake.KeyAsync(db, new RepoRef("acme", "gadgets"), 12, CancellationToken.None));
        }
        IWorkSource source = new GitHubIssueWorkSource(github, new LedgerDbContextFactory(options), [Widgets], time);

        await source.LinkAsync(1, ["https://github.com/acme/widgets/pull/9", "https://github.com/acme/widgets/tree/factory/gh-1"], CancellationToken.None);
        await source.LinkAsync(1, ["https://github.com/acme/widgets/pull/9"], CancellationToken.None);

        Assert.Single(github.FactoryComments(Widgets, 12));
        Assert.Equal(ItemNaming.GitHubIssue, source.Naming);
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReportStateAsync(1, BoardState.Stopped, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.CreateChildrenAsync(1, [], CancellationToken.None));
        Assert.True(await source.InScopeAsync(1, CancellationToken.None));
        Assert.False(await source.InScopeAsync(2, CancellationToken.None));
    }

    [Fact]
    public void Item_names_keep_sources_apart()
    {
        Assert.Equal(new ItemRef(ItemNaming.GitHubIssue, 3), ItemNaming.ParseAny("gh-3"));
        Assert.Equal(new ItemRef(ItemNaming.Shortcut, 3), ItemNaming.ParseAny("sc-3"));
        Assert.Equal(new ItemRef(ItemNaming.Shortcut, 3), ItemNaming.ParseAny("3"));
        Assert.Null(ItemNaming.ParseAny("gh-"));
        Assert.Null(ItemNaming.ParseAny("gh-0"));
        Assert.Null(ItemNaming.ParseAny("gh-+3"));
        Assert.False(ItemNaming.GitHubIssue.TryParse("3", out _));
        Assert.Equal("factory/gh-3", ItemNaming.GitHubIssue.BranchName(3));
        Assert.Equal(new ItemRef(ItemNaming.GitHubIssue, 3), ControlScope.ItemOf("item:gh-3"));
        Assert.Null(ControlScope.ItemStory("item:gh-3"));
        Assert.True(ControlScope.IsValid("item:gh-3"));
    }

    private sealed class RecordingStops : IItemStops
    {
        public List<string> Stopped { get; } = [];

        public Task<ControlResult> StopAsync(int storyId, CancellationToken ct) => StopAsync(new ItemRef(ItemNaming.Shortcut, storyId), ct);

        public Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct)
        {
            Stopped.Add(item.ToString());
            return Task.FromResult(new ControlResult(true, $"{item} stopped"));
        }
    }

    [Fact]
    public async Task Stop_reaches_an_issue_item_by_its_own_name_and_the_factory_scope_stops_every_source()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var contexts = new LedgerDbContextFactory(options);
        await using (var db = new LedgerDbContext(options))
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            await ledger.GetOrCreateAsync("github", "gh-3", "t", "acme/widgets", null, CancellationToken.None);
            await ledger.GetOrCreateAsync("shortcut", "sc-3", "t", "acme/widgets", null, CancellationToken.None);
        }
        var stops = new RecordingStops();
        var controls = new LedgerControls(contexts, TimeProvider.System);
        var actions = new ControlActions(controls, contexts, stops);

        Assert.True((await actions.StopAsync("item:gh-3", "me", CancellationToken.None)).Ok);
        Assert.Equal(["gh-3"], stops.Stopped);
        Assert.Equal(ControlState.Stopping, (await controls.GetAsync("item:gh-3", CancellationToken.None))!.State);

        stops.Stopped.Clear();
        await actions.StopAsync(ControlScope.Factory, "me", CancellationToken.None);
        Assert.Equal(["gh-3", "sc-3"], stops.Stopped);
    }

    [Fact]
    public async Task A_resumable_issue_worktree_survives_the_sweep_and_a_triage_worktree_does_not()
    {
        await using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("github", "gh-3", "t", "acme/widgets", null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        // A Shortcut story with the same number is a different item.
        await ledger.GetOrCreateAsync("shortcut", "sc-3", "t", "acme/widgets", null, CancellationToken.None);

        Assert.True(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-gh-3", CancellationToken.None));
        Assert.False(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-sc-3", CancellationToken.None));
        Assert.False(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-triage-gh-3", CancellationToken.None));
    }
}
