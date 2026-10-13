using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <c>factory run kb-N</c> (sc-25981): a Kanban item through the pipeline, over the Kanban work source and a fake KanbanBoard
/// (<see cref="FakeKanbanBoard"/>) — claimed, worked, its pull request opened and linked — and a second run that writes nothing twice.
/// </summary>
public class KanbanPipelineTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>One pipeline run of the item (no gate stage: Intake and Implement, ending at Review with the pull request open).</summary>
    private static async Task<RunOutcome> Run(KanbanWorkSourceTests.Harness h, int id, FakeWorker worker, FakeWorkspaces workspaces, FakePullRequests prs,
        bool ignoreScope = false, string[]? watch = null)
    {
        await using var db = h.Db();
        return await new RunPipeline(h.Source(watch: watch), new WorkLedger(db, TimeProvider.System), new InProcessRunLocks(), workspaces, worker, prs,
            Sandbox, TextWriter.Null, ignoreScope: ignoreScope).RunAsync(id, None);
    }

    [Fact]
    public async Task A_ready_kanban_item_is_claimed_worked_and_its_pull_request_and_branch_linked_and_a_second_run_writes_nothing_twice()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count whitespace-only input as zero words", description: "WordCount(\"  \") returns 1.");
        item.Labels.Add("simple");
        var id = Assert.Single(await h.Source().ListReadyAsync(None));
        var (worker, workspaces, prs) = (new FakeWorker(Reports(Ok)), new FakeWorkspaces(), new FakePullRequests());

        var outcome = await Run(h, id, worker, workspaces, prs);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Review, outcome.State);
        // Claimed on the board: the factory's claim, read back, and the item in progress.
        Assert.Equal(("InProgress", "factory", KanbanWorkSource.Marker(item.Id)), (item.Status, item.Claim!.Value.Name, item.Claim.Value.Marker));
        // Worked in the project's repository, on the item's own branch.
        var repo = new RepoRef("acme", "widgets");
        var pr = Assert.Single(prs.Opened);
        Assert.Equal((repo, "factory/kb-1", "kb-1: Count whitespace-only input as zero words"), (pr.Repo, pr.Head, pr.Title));
        Assert.Contains($"Implements Kanban item [kb-1](https://kanban.test/?project={h.Project.Id}&item={item.Id}): Count whitespace-only input as zero words", pr.Body);
        Assert.Contains("prepare acme/widgets factory/kb-1", workspaces.Calls);
        // The pull request and the branch are links on the item.
        Assert.Equal(new[] { ("pr", "https://github.com/acme/widgets/pull/1"), ("branch", "https://github.com/acme/widgets/tree/factory/kb-1") }, item.Links);
        // A Kanban item is the owner's own text (E5): instructions, not fenced data; simple work runs on the low class (E8).
        var call = Assert.Single(worker.Calls);
        Assert.Contains("Implement Kanban item kb-1 (feature): Count whitespace-only input as zero words", call.Prompt);
        Assert.DoesNotContain("<triage>", call.Prompt);
        Assert.Equal("low", call.ModelClass);
        await using (var db = h.Db())
        {
            var row = await db.WorkItems.SingleAsync();
            Assert.Equal(("kanban", "kb-1", "acme/widgets", (long?)null), (row.Source, row.ExternalId, row.Repo, row.EpicId));
            Assert.Equal(["claimed", "linked"], (await db.LedgerEntries.OrderBy(e => e.Id).ToListAsync())
                .Where(e => e.Step is "claimed" or "linked").Select(e => e.Step));
        }
        Assert.Equal(["claim_work_item", "update_work_item", "add_work_item_link", "add_work_item_link"], h.Board.Writes);

        // A second run resumes from the ledger: nothing is claimed, moved, linked or opened again.
        // (This pipeline has no gate stage, so Review is where it stops and the run says so once on the item — production always
        // has one. That notice is the second run's only write, and a third run does not repeat it.)
        var version = item.Version;
        var second = await Run(h, id, worker, workspaces, prs);
        var third = await Run(h, id, worker, workspaces, prs);

        Assert.Equal((WorkState.Review, WorkState.Review), (second.State, third.State));
        Assert.Equal(["claim_work_item", "update_work_item", "add_work_item_link", "add_work_item_link", "add_work_item_comment"], h.Board.Writes);
        Assert.Equal((version, 2, "InProgress"), (item.Version, item.Links.Count, item.Status));
        Assert.StartsWith("[author: dark-factory] kb-1", Assert.Single(item.Comments).Body);
        Assert.Single(prs.Opened);
        Assert.Single(worker.Calls);
    }

    [Theory]
    [InlineData("claimed", "claim refused: the item is claimed by someone")]
    [InlineData("backlog", "claim refused: the item is Backlog, not Ready")]
    [InlineData("unwatched", "claim refused: the item's project is outside the watched projects (Kanban:Watch:Projects)")]
    public async Task An_item_that_is_not_the_factorys_to_take_is_parked_with_the_reason_and_the_board_is_not_written(string what, string reason)
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: what == "backlog" ? "Backlog" : "Ready");
        var id = await h.Key(item);
        if (what == "claimed")
        {
            h.Board.ClaimAsSomeoneElse(item);
        }
        var (worker, prs) = (new FakeWorker(Reports(Ok)), new FakePullRequests());

        var outcome = await Run(h, id, worker, new FakeWorkspaces(), prs, watch: what == "unwatched" ? ["ELSEWHERE"] : null);

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Empty(h.Board.Writes);
        Assert.Empty(worker.Calls);
        Assert.Empty(prs.Opened);
        await using var db = h.Db();
        Assert.Equal(reason, (await db.LedgerEntries.OrderBy(e => e.Id).LastAsync(e => e.Step == null)).Detail);
    }

    [Fact]
    public async Task An_escalated_item_is_run_again_more_than_a_day_later_when_its_claim_has_lapsed()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        var (workspaces, prs) = (new FakeWorkspaces(), new FakePullRequests());
        var failed = await Run(h, id, new FakeWorker(Reports(new Worker.WorkerResult("sess-x", 1, true, "error_during_execution", "Not logged in", "boom"))),
            workspaces, prs);
        Assert.Equal((WorkState.Escalated, "InProgress"), (failed.State, item.Status));
        var firstClaim = item.Claim!.Value.Expires;

        // The escalation's remedy, a day and an hour later: the claim has lapsed and nothing renewed it.
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        Assert.True(firstClaim < h.Board.Time.Now);
        var outcome = await Run(h, id, new FakeWorker(Reports(Ok)), workspaces, prs);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Review, outcome.State);
        Assert.Single(prs.Opened);
        Assert.Equal(("InProgress", KanbanWorkSource.Marker(item.Id), h.Board.Time.Now.AddDays(1)),
            (item.Status, item.Claim!.Value.Marker, item.Claim.Value.Expires));
        await using var db = h.Db();
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), e => RunPipeline.IsClaimRefused(e.Detail));
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("archived-project")]
    [InlineData("no-github-repository")]
    [InlineData("gone-from-the-board")]
    public async Task Factory_run_on_a_kanban_item_that_cannot_be_read_prints_what_to_do_and_exits_1_without_a_stack_trace(string what)
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = what == "unknown-key" ? 42 : await h.Key(item);
        switch (what)
        {
            case "archived-project": h.Project.Archived = true; break;
            case "no-github-repository": h.Project.RepositoryUrl = "https://gitlab.com/acme/widgets"; break;
            case "gone-from-the-board": h.Board.Items.Remove(item); break;
        }
        var (worker, prs, stderr) = (new FakeWorker(Reports(Ok)), new FakePullRequests(), new StringWriter());
        var options = new FactoryOptions(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CodeGraph:Token"] = "cg_test" }).Build(), new InMemorySecrets());

        var exit = await FactoryRunner.RunCommandAsync(options, _ => Task.FromResult<string?>(null),
            _ => Run(h, id, worker, new FakeWorkspaces(), prs, ignoreScope: true), stderr, None);

        Assert.Equal(1, exit);
        Assert.Equal(what switch
        {
            "unknown-key" => "kb-42 is no Kanban item the factory knows: the ledger never gave out that key. Run `factory kanban key <item GUID>` first; it prints the item's kb-<n>.",
            "archived-project" => "kb-1: its Kanban project is archived or gone, so its repository is unknown; restore the project, or add a "
                + "`Repo: owner/name` line to the item.",
            "no-github-repository" => "kb-1: the repository_url of its Kanban project DF names no github.com repository "
                + "('https://gitlab.com/acme/widgets'); set it to one, or add a `Repo: owner/name` line to the item.",
            _ => $"Kanban's get_work_item was refused (not_found): Work item {item.Id} was not found.",
        } + Environment.NewLine, stderr.ToString());
        Assert.Empty(h.Board.Writes);
        Assert.Empty(worker.Calls);
        Assert.Empty(prs.Opened);
        await using var db = h.Db();
        Assert.Empty(await db.WorkItems.ToListAsync()); // nothing was recorded for an item that could not be read
    }

    [Fact]
    public async Task Ignoring_the_scope_runs_an_item_of_a_project_that_is_not_watched()
    {
        var h = new KanbanWorkSourceTests.Harness();
        h.Board.Projects.Add(new FakeKanbanBoard.Project { Key = "ELSEWHERE" });
        var item = h.Board.AddItem(h.Project, "Count words");
        var prs = new FakePullRequests();

        var outcome = await Run(h, await h.Key(item), new FakeWorker(Reports(Ok)), new FakeWorkspaces(), prs, ignoreScope: true, watch: ["ELSEWHERE"]);

        Assert.Equal(WorkState.Review, outcome.State);
        Assert.Equal("InProgress", item.Status);
        Assert.Single(prs.Opened);
    }
}

/// <summary>
/// E2 (sc-25981): Kanban is written only by the orchestrator. The full-scope token and the write tools are reachable from no MCP
/// upstream, allowlist, profile or proxy grant — a model session keeps the separate read token and read tools.
/// </summary>
public class KanbanWriteIsolationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static FactoryOptions Options(Dictionary<string, string?> config) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), new InMemorySecrets());

    [Fact]
    public void No_write_tool_is_on_kanbans_allowlist_or_in_any_profile_and_the_adapters_writes_are_the_contracts()
    {
        // The adapter's own tools are the contract's: its write tools are write tools there, its read tools are not.
        Assert.All(KanbanBoard.WriteTools, t => Assert.Contains(t, FakeKanbanBoard.WriteTools));
        Assert.Empty(KanbanBoard.ReadTools.Intersect(FakeKanbanBoard.WriteTools));
        Assert.Equal(23, FakeKanbanBoard.WriteTools.Distinct().Count());

        // No write tool of the contract — the adapter's or any other — is on the upstream's allowlist …
        Assert.Empty(McpServers.KanbanTools.Intersect(FakeKanbanBoard.WriteTools));
        Assert.Empty(McpServers.AllowlistOf(McpServers.Kanban).Intersect(FakeKanbanBoard.WriteTools));
        // … so none is an allow rule a session can be given, or a tool of a profile (a profile is a subset of the allowlists).
        Assert.DoesNotContain(McpServers.AllToolRules, r => FakeKanbanBoard.WriteTools.Any(t => r == McpServers.ToolRule(McpServers.Kanban, t)));
        foreach (var profile in new[] { McpProfile.Triage, McpProfile.Planner })
        {
            Assert.Empty(profile.ToolsOf(McpServers.Kanban).Intersect(FakeKanbanBoard.WriteTools));
        }
        Assert.Empty(McpProfile.Triage.ToolsOf(McpServers.Kanban)); // triage gets no Kanban at all
    }

    [Fact]
    public async Task The_upstream_registry_holds_the_read_token_only_and_never_sends_a_write_tool()
    {
        var board = new FakeKanbanBoard();
        board.AddProject("DF");
        var options = Options(new() { ["Kanban:Token"] = FakeKanbanBoard.ReadToken, ["Kanban:WriteToken"] = FakeKanbanBoard.FullToken });
        using var codeGraphHttp = new HttpClient();

        var upstreams = FactoryRunner.CreateUpstreams(options, codeGraphHttp, board.Client(), TextWriter.Null);
        var kanban = Assert.IsType<McpUpstream>(upstreams.Kanban);

        // What a session could list and call through it: read tools, with the read token.
        var listed = (await kanban.ListToolsAsync(None)).Select(t => t.Name).ToList();
        Assert.NotEmpty(listed);
        Assert.Empty(listed.Intersect(FakeKanbanBoard.WriteTools));
        await kanban.CallAsync("list_projects", new JsonObject(), None);
        foreach (var write in FakeKanbanBoard.WriteTools)
        {
            await Assert.ThrowsAsync<ToolNotAllowedException>(() => kanban.CallAsync(write, new JsonObject(), None));
        }
        Assert.Equal(["list_projects"], board.Calls.Select(c => c.Tool)); // no write tool was sent
        Assert.NotEmpty(board.Server.Api.Requests);
        Assert.All(board.Server.Api.Requests, r => Assert.Equal($"Bearer {FakeKanbanBoard.ReadToken}", r.Headers["Authorization"]));
        Assert.Equal([kanban], upstreams.All);

        // The write token alone makes no upstream: a session gets no Kanban then, rather than the orchestrator's token.
        if (Environment.GetEnvironmentVariable("FACTORY_KANBAN_TOKEN") is null)
        {
            var writeOnly = Options(new() { ["Kanban:WriteToken"] = FakeKanbanBoard.FullToken });
            Assert.Null(FactoryRunner.CreateUpstreams(writeOnly, codeGraphHttp, board.Client(), TextWriter.Null).Kanban);
        }
    }

    [Fact]
    public async Task The_work_source_alone_holds_the_write_token()
    {
        var board = new FakeKanbanBoard();
        board.AddItem(board.AddProject("DF"), "Count words");
        var options = Options(new()
        {
            ["Kanban:Token"] = FakeKanbanBoard.ReadToken, ["Kanban:WriteToken"] = FakeKanbanBoard.FullToken, ["Kanban:Watch:Projects"] = "df",
        });

        var source = FactoryRunner.CreateKanbanSource(options, board.Client());
        await source.ValidateScopeAsync(None);

        Assert.All(board.Server.Api.Requests, r => Assert.Equal($"Bearer {FakeKanbanBoard.FullToken}", r.Headers["Authorization"]));
        Assert.Equal(ItemNaming.Kanban, source.Naming);
        // Without the token there is no Kanban work source: the credential is named, nothing is built with the read token instead.
        if (Environment.GetEnvironmentVariable("FACTORY_KANBAN_WRITE_TOKEN") is null)
        {
            var missing = Assert.Throws<MissingCredentialException>(() =>
                FactoryRunner.CreateKanbanSource(Options(new() { ["Kanban:Token"] = FakeKanbanBoard.ReadToken }), board.Client()));
            Assert.Contains("Kanban:WriteToken", missing.Message);
        }
    }

    [Fact]
    public async Task A_planner_grant_of_the_loopback_proxy_lists_and_forwards_no_kanban_write_tool()
    {
        var board = new FakeKanbanBoard();
        board.AddProject("DF");
        var upstreams = new McpUpstreams(null, new McpUpstream(McpServers.Kanban,
            new McpHttpClient(board.Client(), FakeKanbanBoard.ReadToken, "Kanban", ""), McpServers.KanbanTools, repoScoped: false));
        await using var proxy = await McpProxy.StartAsync(upstreams, None);
        using var grant = proxy.Grant(new RepoRef("o", "r"), McpProfile.Planner);
        using var http = new HttpClient();
        async Task<JsonObject> Rpc(string method, JsonObject parameters)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, proxy.Endpoint(McpServers.Kanban))
            {
                Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = method, ["params"] = parameters }.ToJsonString(),
                    Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Credential);
            using var response = await http.SendAsync(request, None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(None))!.AsObject();
        }

        var listed = (await Rpc("tools/list", new JsonObject()))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("list_projects", listed);
        Assert.Empty(listed.Intersect(FakeKanbanBoard.WriteTools));
        foreach (var write in KanbanBoard.WriteTools)
        {
            var refused = await Rpc("tools/call", new JsonObject { ["name"] = write, ["arguments"] = new JsonObject() });
            Assert.Equal(-32602, refused["error"]!["code"]!.GetValue<int>());
        }
        Assert.Empty(board.Writes); // nothing was forwarded
    }

    [Fact]
    public void The_write_token_and_its_client_are_named_only_where_the_work_source_is_built()
    {
        var src = Path.Combine(RepoRoot(), "src", "DarkFactory.Orchestrator");
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText);
        // The keychain account's name (SecretAccounts) is not the token.
        List<string> Naming(string text) => files
            .Where(f => f.Value.Replace("SecretAccounts.KanbanWriteToken", "", StringComparison.Ordinal).Contains(text, StringComparison.Ordinal))
            .Select(f => f.Key).Order().ToList();

        // The token is read from the options in one place, and the client that holds it is built in one place: the work source's factory.
        Assert.Equal(["FactoryRunner.cs"], Naming(".KanbanWriteToken"));
        Assert.Equal(["FactoryRunner.cs"], Naming("KanbanWrites.Client("));
        // The typed caller is made there and nowhere else, and nothing that serves a model session names it or the work source.
        Assert.Equal(["FactoryRunner.cs"], Naming("new Kanban.KanbanBoard("));
        Assert.Empty(Naming("new KanbanBoard("));
        foreach (var folder in new[] { "Mcp/", "Gate/", "Plans/", "Issues/", "Worker/", "CodeGraph/", "Sessions/", "Dashboard/" })
        {
            Assert.DoesNotContain(files, f => f.Key.StartsWith(folder, StringComparison.Ordinal)
                && (f.Value.Contains("KanbanWrite", StringComparison.Ordinal) || f.Value.Contains("KanbanBoard(", StringComparison.Ordinal)
                    || f.Value.Contains("KanbanWorkSource", StringComparison.Ordinal)));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DarkFactory.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}

/// <summary>The ledger's Kanban keys on a real Postgres (sc-25981): the migration, and allocation by processes racing for one item.</summary>
public sealed class KanbanKeysPostgresTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_kanban");
        _cs = _db.ConnectionString;
        await LedgerMigrations.MigrateAsync(_cs, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    /// <summary>A separate "process": its own context factory (and so its own connections).</summary>
    private KanbanKeys Process() => new(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)));

    [Fact]
    public async Task The_migration_creates_the_key_table_with_one_row_per_item()
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_cs));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var item = Guid.NewGuid();
        db.KanbanItems.Add(new KanbanItem { ItemId = item, FirstSeenAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // The database itself refuses a second key for the same item.
        db.KanbanItems.Add(new KanbanItem { ItemId = item, FirstSeenAt = DateTimeOffset.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task The_ledger_says_which_kanban_items_this_factory_claimed()
    {
        // KanbanKeys.ClaimedAsync as Postgres runs it: only a `claimed` checkpoint of that kb-<n> counts.
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_cs)))
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            var claimed = await ledger.GetOrCreateAsync(ItemNaming.Kanban.Source, "kb-1", "Claimed", "acme/widgets", null, CancellationToken.None);
            await ledger.CheckpointAsync(claimed, RunPipeline.Steps.Claimed, null, null, CancellationToken.None);
            var parked = await ledger.GetOrCreateAsync(ItemNaming.Kanban.Source, "kb-2", "Parked", "acme/widgets", null, CancellationToken.None);
            await ledger.CheckpointAsync(parked, RunPipeline.Steps.Parked, null, null, CancellationToken.None);
            // Another source's item 3 is not Kanban's kb-3.
            var story = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, "sc-3", "Story", "acme/widgets", null, CancellationToken.None);
            await ledger.CheckpointAsync(story, RunPipeline.Steps.Claimed, null, null, CancellationToken.None);
        }

        Assert.True(await Process().ClaimedAsync(1, CancellationToken.None));
        Assert.False(await Process().ClaimedAsync(2, CancellationToken.None));
        Assert.False(await Process().ClaimedAsync(3, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_item_always_gets_the_same_key_across_restarts_and_processes_allocating_at_once()
    {
        var items = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();

        // Several processes ask for every item's key at the same moment: each goes through the items in the same order, started
        // together, so they race for each one. One connection per process (8 at once, not one per question: the compose Postgres
        // takes 100 connections, shared with every other test running).
        using var start = new SemaphoreSlim(0);
        var processes = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var answers = new List<(Guid Item, int Key)>();
            await start.WaitAsync();
            foreach (var item in items)
            {
                answers.Add((item, await Process().KeyAsync(item, CancellationToken.None)));
            }
            return answers;
        })).ToList();
        start.Release(processes.Count);
        var asked = (await Task.WhenAll(processes)).SelectMany(a => a).ToList();

        var byItem = asked.GroupBy(a => a.Item).ToDictionary(g => g.Key, g => g.Select(a => a.Key).Distinct().ToList());
        Assert.All(byItem.Values, keys => Assert.Single(keys)); // one key per item, whoever asked
        Assert.Equal(items.Count, byItem.Values.Select(k => k[0]).Distinct().Count()); // and no two items share one
        Assert.All(byItem.Values, k => Assert.True(k[0] > 0));
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_cs)))
        {
            Assert.Equal(items.Count, await db.KanbanItems.CountAsync());
        }
        // After a restart the keys are what they were, both ways.
        foreach (var item in items)
        {
            var key = byItem[item][0];
            Assert.Equal(key, await Process().KeyAsync(item, CancellationToken.None));
            Assert.Equal(item, await Process().ItemAsync(key, CancellationToken.None));
        }
        Assert.Null(await Process().ItemAsync(int.MaxValue, CancellationToken.None));
    }
}
