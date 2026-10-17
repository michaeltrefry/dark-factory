using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Several work sources (sc-25982): the one registry of configured sources and every place that takes a source from it — the
/// intake loop's lanes, the in-flight listing, run/stop/pause by an id of any source, the start-up check per source and its
/// refusals — over a fake Shortcut board and a fake KanbanBoard. No test here needs the network or Postgres.
/// </summary>
public class WorkSourceRegistryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly Dictionary<string, string?> Shortcut = new() { ["Shortcut:ApiToken"] = "tok", ["Shortcut:Watch:Teams"] = "darkfactory" };

    private static readonly Dictionary<string, string?> Kanban = new()
    {
        ["Kanban:WriteToken"] = FakeKanbanBoard.FullToken, ["Kanban:Watch:Projects"] = "DF", ["Kanban:WebUrl"] = "https://kanban.test/",
    };

    private static readonly Dictionary<string, string?> Issues = new() { ["GitHub:Watch:Repos"] = "acme/widgets" };

    /// <summary>What every run needs besides a work source: the router key, both GitHub Apps and the dashboard login.</summary>
    private static readonly Dictionary<string, string?> Core = new()
    {
        ["Router:Key"] = "rk", ["GitHub:AppId"] = "1", ["GitHub:PrivateKeyPem"] = "pem", ["GitHub:Gate:AppId"] = "2", ["GitHub:Gate:PrivateKeyPem"] = "pem",
        ["Dashboard:PasswordHash"] = "hash", ["CodeGraph:Token"] = "cg",
    };

    /// <summary>A source's token and no watch scope: enough to reach its items by id and to finish those in flight, not to be polled.</summary>
    private static readonly Dictionary<string, string?> ShortcutToken = new() { ["Shortcut:ApiToken"] = "tok" };

    private static readonly Dictionary<string, string?> KanbanToken = new() { ["Kanban:WriteToken"] = FakeKanbanBoard.FullToken, ["Kanban:WebUrl"] = "https://kanban.test/" };

    /// <summary>
    /// Options from these settings alone: no keychain (<see cref="InMemorySecrets"/>) and no process environment, so what is "not
    /// configured" here is so on any machine — a developer's <c>SHORTCUT_API_TOKEN</c> or <c>FACTORY_KANBAN_WRITE_TOKEN</c> included.
    /// </summary>
    private static FactoryOptions Options(params Dictionary<string, string?>[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
                settings.SelectMany(s => s).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build(), new InMemorySecrets(), // a later one wins
            environment: _ => null);

    /// <summary>Both fake boards and one in-memory ledger, behind a registry over the given settings.</summary>
    private sealed class Boards
    {
        public KanbanWorkSourceTests.Harness Kanban { get; } = new();
        public FakeShortcutBoard Shortcut { get; } = new();
        public FakeApi GitHub { get; } = new();
        public List<ItemNaming> Built { get; } = [];
        public InProcessRunLocks Locks { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public IControls Controls => new LedgerControls(Kanban.Contexts, TimeProvider.System);

        public WorkSourceRegistry Registry(FactoryOptions options) => new(options, naming =>
        {
            Built.Add(naming);
            return naming == ItemNaming.Kanban ? Kanban.Board.Client()
                : naming == ItemNaming.Shortcut ? Shortcut.Client()
                : GitHub.Client("https://github.test/");
        }, Kanban.Contexts);

        /// <summary>A lane's runner: the pipeline without a gate stage, over the shared ledger, like <c>FactoryItemRunner</c> over Postgres.</summary>
        public IItemRunner Runner(IWorkSource source) => new PipelineRunner(this, source);

        public async Task<WorkItem?> Item(string id)
        {
            await using var db = Kanban.Db();
            return await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == id);
        }

        private sealed class PipelineRunner(Boards b, IWorkSource source) : IItemRunner
        {
            public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
            {
                await using var db = b.Kanban.Db();
                return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), source.Naming, ct, b.Controls, RunPipeline.ImplementStates);
            }

            /// <summary>The claims a poll keeps (sc-25983), as <c>FactoryItemRunner</c> lists them: this source's, over the production states.</summary>
            public async Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct)
            {
                await using var db = b.Kanban.Db();
                return await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), source.Naming, ct, b.Controls);
            }

            public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
            {
                await using var db = b.Kanban.Db();
                return await new RunPipeline(source, new WorkLedger(db, TimeProvider.System), b.Locks, new FakeWorkspaces(), new FakeWorker(Reports(Ok)),
                    b.Prs, Sandbox, TextWriter.Null, controls: b.Controls).RunAsync(id, ct);
            }
        }

        /// <summary>What <c>FactoryItemStops</c> does, over the in-memory ledger: the stop on the source the item's id names.</summary>
        public sealed class Stops(Boards b, WorkSourceRegistry sources) : IItemStops
        {
            public async Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct)
            {
                await using var db = b.Kanban.Db();
                return await new ItemStopper(sources.Of(item.Naming), new WorkLedger(db, TimeProvider.System), b.Locks, b.Prs, b.Controls, TextWriter.Null)
                    .StopAsync(item.Id, ct);
            }
        }

        public ControlActions Actions(WorkSourceRegistry sources) => new(Controls, Kanban.Contexts, new Stops(this, sources), sources.Find, Locks);

        public IntakeLoop Loop(WorkSourceRegistry sources) =>
            new(sources.Lanes(Runner, new IntakeStatus(TimeProvider.System), TextWriter.Null), new IntakeOptions(TimeSpan.FromMinutes(1)), TimeProvider.System,
                NullLogger<IntakeLoop>.Instance, Controls);
    }

    // ---- The registry ----

    [Fact]
    public void There_is_one_kind_of_source_per_naming_and_each_says_which_settings_configure_it()
    {
        Assert.Equal(ItemNaming.All.OrderBy(n => n.Source), WorkSourceRegistry.Kinds.Select(k => k.Naming).OrderBy(n => n.Source));
        Assert.All(ItemNaming.All, naming => Assert.Equal(naming, WorkSourceRegistry.KindOf(naming).Naming));
        Assert.Contains("Shortcut:Watch:Teams", WorkSourceRegistry.KindOf(ItemNaming.Shortcut).Settings);
        Assert.Contains("SHORTCUT_API_TOKEN", WorkSourceRegistry.KindOf(ItemNaming.Shortcut).Settings);
        Assert.Contains("Kanban:WriteToken", WorkSourceRegistry.KindOf(ItemNaming.Kanban).Settings);
        Assert.Contains("Kanban:Watch:Projects", WorkSourceRegistry.KindOf(ItemNaming.Kanban).Settings);
        Assert.Contains("GitHub:Watch:Repos", WorkSourceRegistry.KindOf(ItemNaming.GitHubIssue).Settings);
        // The refusal with no source names all of them, and how work already in flight is finished by id.
        Assert.All(WorkSourceRegistry.Kinds.Where(k => !k.Internal), k => Assert.Contains(k.Settings, WorkSourceRegistry.NoneConfigured));
        Assert.Equal(WorkSourceRegistry.NoneConfigured + " An item the factory already works needs no watch scope to be finished by hand: "
            + "`factory stop --item <id>` cancels it, `factory run <id> --ignore-scope` runs it on.", WorkSourceRegistry.NothingWatched);
        // The credential alone is what an item named by id needs: it names no watch scope.
        Assert.All(WorkSourceRegistry.Kinds, k => Assert.DoesNotContain("Watch", k.Credential));
        Assert.Equal("a Shortcut API token (env SHORTCUT_API_TOKEN, config 'Shortcut:ApiToken' or keychain account 'shortcut-api-token')",
            WorkSourceRegistry.KindOf(ItemNaming.Shortcut).Credential);
        Assert.Equal("a KanbanBoard write token (env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account 'kanban-write-token')",
            WorkSourceRegistry.KindOf(ItemNaming.Kanban).Credential);
        Assert.Equal("the factory's GitHub App (config 'GitHub:AppId' and 'GitHub:PrivateKeyPem', or keychain accounts 'github-app-id' and "
            + "'github-app-private-key': `factory github-app setup`)", WorkSourceRegistry.KindOf(ItemNaming.GitHubIssue).Credential);
        Assert.StartsWith(WorkSourceRegistry.KindOf(ItemNaming.Shortcut).Credential + " and a watch scope", WorkSourceRegistry.KindOf(ItemNaming.Shortcut).Settings);
        Assert.StartsWith(WorkSourceRegistry.KindOf(ItemNaming.Kanban).Credential + " and the watched projects", WorkSourceRegistry.KindOf(ItemNaming.Kanban).Settings);
        Assert.Throws<ArgumentException>(() => WorkSourceRegistry.KindOf(new ItemNaming("jira", "jr", "Jira issue")));
    }

    public static TheoryData<string[], string[]> Sets => new()
    {
        { ["shortcut"], ["shortcut"] },
        { ["kanban"], ["kanban"] },
        { ["github"], ["github"] },
        { ["kanban", "shortcut"], ["shortcut", "kanban"] },
        { ["kanban", "github", "shortcut"], ["shortcut", "github", "kanban"] },
        { [], [] },
    };

    [Theory]
    [MemberData(nameof(Sets))]
    public void The_configured_sources_are_exactly_those_whose_watch_scope_is_set_in_a_fixed_order(string[] set, string[] expected)
    {
        var b = new Boards();
        var settings = new List<Dictionary<string, string?>> { Core };
        settings.AddRange(set.Select(s => s switch { "shortcut" => Shortcut, "kanban" => Kanban, _ => Issues }));
        using var sources = b.Registry(Options([.. settings]));

        Assert.Equal(expected, sources.Configured.Select(s => s.Naming.Source));
        // Built once each, and only the configured ones: an unconfigured source's client is never made.
        Assert.Equal(expected, sources.Configured.Select(s => s.Naming.Source));
        Assert.Equal(expected, b.Built.Select(n => n.Source));
        var types = sources.Configured.Select(s => s.GetType()).ToList();
        Assert.Equal(expected.Select(s => s switch { "shortcut" => typeof(ShortcutWorkSource), "kanban" => typeof(KanbanWorkSource), _ => typeof(GitHubIssueWorkSource) }), types);
    }

    [Fact]
    public void A_watched_source_without_its_credential_is_refused_naming_the_credential_never_left_out()
    {
        var b = new Boards();
        using var kanban = b.Registry(Options(Core, Shortcut, new() { ["Kanban:Watch:Projects"] = "DF" }));
        Assert.Contains("Kanban:WriteToken", Assert.Throws<MissingCredentialException>(() => kanban.Configured).Message);
        using var shortcut = b.Registry(Options(Core, Kanban, new() { ["Shortcut:Watch:Epics"] = "7" }));
        Assert.Contains("Shortcut:ApiToken", Assert.Throws<MissingCredentialException>(() => shortcut.Configured).Message);
        // The workers' GitHub App is every run's credential; watched issues without it are refused the same way.
        using var issues = b.Registry(Options(Issues));
        Assert.Contains("GitHub:AppId", Assert.Throws<MissingCredentialException>(() => issues.Configured).Message);
    }

    [Fact]
    public void An_item_named_by_id_needs_its_sources_credential_and_no_watch_scope()
    {
        var b = new Boards();
        // Shortcut's token and no scope (what `factory run sc-12 --ignore-scope` always worked with); nothing of KanbanBoard.
        using var sources = b.Registry(Options(Core, Issues, ShortcutToken));

        Assert.IsType<ShortcutWorkSource>(sources.Of(ItemNaming.Shortcut));
        Assert.Same(sources.Of(ItemNaming.Shortcut), sources.Find(ItemNaming.Shortcut));
        Assert.Equal(["github"], sources.Configured.Select(s => s.Naming.Source));
        Assert.Null(sources.Find(ItemNaming.Kanban));
        var missing = Assert.Throws<MissingCredentialException>(() => sources.Of(ItemNaming.Kanban));
        // The credential, which is all an item named by id needs: no watch scope is asked for.
        Assert.Equal("The KanbanBoard work source is not configured (kb-<n> names a Kanban item): it needs a KanbanBoard write token "
            + "(env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account 'kanban-write-token').", missing.Message);
        Assert.Equal(missing.Message, WorkSourceRegistry.NeedsCredential(ItemNaming.Kanban));
        Assert.DoesNotContain(ItemNaming.Kanban, b.Built);

        // With no source configured at all, the refusal names what would configure each one.
        using var none = b.Registry(Options(Core));
        var nothing = Assert.Throws<MissingCredentialException>(() => none.Of(ItemNaming.Shortcut));
        Assert.Equal("The Shortcut work source is not configured (sc-<n> names a Shortcut story): it needs a Shortcut API token "
            + "(env SHORTCUT_API_TOKEN, config 'Shortcut:ApiToken' or keychain account 'shortcut-api-token'). " + WorkSourceRegistry.NoneConfigured, nothing.Message);
    }

    [Fact]
    public void The_registry_disposes_the_clients_it_made()
    {
        var b = new Boards();
        var handler = new DisposeProbe();
        var sources = new WorkSourceRegistry(Options(Core, Kanban), _ => new HttpClient(handler), b.Kanban.Contexts);
        _ = sources.Configured;

        sources.Dispose();

        Assert.True(handler.Disposed);
    }

    private sealed class DisposeProbe : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // ---- Start-up ----

    [Fact]
    public async Task Factory_work_starts_with_kanban_configured_and_no_shortcut_token_and_validates_the_kanban_scope()
    {
        var b = new Boards();
        var stderr = new StringWriter();
        using var sources = b.Registry(Options(Core, Kanban));

        Assert.Null(await FactoryRunner.CheckWorkStartAsync(Options(Core, Kanban), sources, stderr, None));

        // The scope was checked on the board, with the orchestrator's own token; Shortcut was never built or asked.
        var call = Assert.Single(b.Kanban.Board.Calls);
        Assert.Equal(("list_projects", FakeKanbanBoard.FullToken), (call.Tool, call.Token));
        Assert.Empty(b.Shortcut.Requests);
        Assert.Equal([ItemNaming.Kanban], b.Built);
        // No Shortcut token is not worth a line: a source no setting names is not mentioned.
        Assert.Equal("No GitHub issues are watched (set GitHub:Watch:Repos to triage a repo's issues)." + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task Factory_work_with_only_shortcut_configured_checks_the_shortcut_scope_and_nothing_of_kanban()
    {
        var b = new Boards();
        var stderr = new StringWriter();
        using var sources = b.Registry(Options(Core, Shortcut));

        Assert.Null(await FactoryRunner.CheckWorkStartAsync(Options(Core, Shortcut), sources, stderr, None));

        Assert.Contains(b.Shortcut.Requests, r => r.PathAndQuery.StartsWith("/api/v3/groups", StringComparison.Ordinal));
        Assert.Empty(b.Kanban.Board.Calls);
        Assert.Equal([ItemNaming.Shortcut], b.Built);
        Assert.Equal("No GitHub issues are watched (set GitHub:Watch:Repos to triage a repo's issues)." + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task Factory_work_with_no_source_configured_is_refused_naming_the_settings_that_would_configure_one()
    {
        var b = new Boards();
        var stderr = new StringWriter();
        // A token alone watches nothing: that is no configured source either, and each such source says so.
        var options = Options(Core, ShortcutToken, KanbanToken);
        using var sources = b.Registry(options);

        var refusal = await FactoryRunner.CheckWorkStartAsync(options, sources, stderr, None);

        Assert.Equal(WorkSourceRegistry.NothingWatched, refusal);
        Assert.StartsWith("No work source is configured", refusal);
        // What is left in flight can still be finished by id, which needs no watch scope.
        Assert.Contains("`factory stop --item <id>`", refusal);
        Assert.Contains("`factory run <id> --ignore-scope`", refusal);
        foreach (var setting in new[] { "Shortcut:ApiToken", "Shortcut:Watch:Teams", "Shortcut:Watch:Epics", "Kanban:WriteToken", "Kanban:Watch:Projects", "GitHub:Watch:Repos" })
        {
            Assert.Contains(setting, refusal);
        }
        Assert.Equal(
        [
            "Shortcut: the watch scope is empty (set Shortcut:Watch:Teams and/or Shortcut:Watch:Epics); no Shortcut story will be picked up "
                + "(those already in flight are still finished).",
            "No GitHub issues are watched (set GitHub:Watch:Repos to triage a repo's issues).",
            "KanbanBoard: no project is watched (set Kanban:Watch:Projects); no Kanban item will be picked up (those already in flight are still finished).",
        ], stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(b.Built);
        Assert.Empty(b.Kanban.Board.Calls);
        Assert.Empty(b.Shortcut.Requests);
    }

    [Fact]
    public async Task Factory_work_reports_each_configured_sources_bad_scope_or_missing_credential_before_polling()
    {
        var b = new Boards();
        // Both sources configured; the Kanban one watches a project the board does not have.
        var unknown = Options(Core, Shortcut, Kanban, new() { ["Kanban:Watch:Projects"] = "DF,NOPE" });
        using (var sources = b.Registry(unknown))
        {
            Assert.Equal("Kanban:Watch:Projects: KanbanBoard has no project with the key 'NOPE' (or it is archived).",
                await FactoryRunner.CheckWorkStartAsync(unknown, sources, TextWriter.Null, None));
        }
        var team = Options(Core, Kanban, Shortcut, new() { ["Shortcut:Watch:Teams"] = "darkfactory,nope" });
        using (var sources = b.Registry(team))
        {
            Assert.Contains("no Shortcut team 'nope'", await FactoryRunner.CheckWorkStartAsync(team, sources, TextWriter.Null, None));
        }
        // A watched source without its credential, beside a configured one: refused, not skipped.
        var tokenless = Options(Core, Shortcut, new() { ["Kanban:Watch:Projects"] = "DF" });
        using (var sources = b.Registry(tokenless))
        {
            Assert.Contains("Kanban:WriteToken", await FactoryRunner.CheckWorkStartAsync(tokenless, sources, TextWriter.Null, None));
        }
        // And what every run needs is still required with any source.
        var keyless = Options(Kanban, new() { ["GitHub:AppId"] = "1", ["GitHub:PrivateKeyPem"] = "pem" });
        using (var sources = b.Registry(keyless))
        {
            Assert.Contains("router key", await FactoryRunner.CheckWorkStartAsync(keyless, sources, TextWriter.Null, None));
        }
    }

    [Fact]
    public async Task Factory_run_of_an_item_whose_source_is_not_configured_exits_2_naming_the_settings_before_anything_is_asked()
    {
        var b = new Boards();
        var calls = new List<string>();
        var stderr = new StringWriter();
        var options = Options(Core);
        using var sources = b.Registry(options);

        var exit = await FactoryRunner.RunCommandAsync(options,
            _ => { calls.Add("enrollment"); return Task.FromResult<string?>(null); },
            _ => { calls.Add("run"); return Task.FromResult(new RunOutcome(1, WorkState.Watch, null, null, null)); },
            stderr, None, requireSource: () => sources.Of(ItemNaming.Kanban));

        Assert.Equal(2, exit);
        Assert.Empty(calls);
        Assert.StartsWith(WorkSourceRegistry.NeedsCredential(ItemNaming.Kanban) + " No work source is configured", stderr.ToString());
        Assert.Contains("Kanban:WriteToken", stderr.ToString());
        Assert.Contains("Shortcut:Watch:Teams", stderr.ToString());
        Assert.DoesNotContain("   at ", stderr.ToString()); // the message, not a stack trace

        // The same refusal from the run itself and from a Stop by id — each before the ledger or a board is touched.
        await Assert.ThrowsAsync<MissingCredentialException>(() =>
            FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Kanban, 7), ignoreScope: false, TextWriter.Null, None));
        await Assert.ThrowsAsync<MissingCredentialException>(() =>
            FactoryRunner.ControlAsync(options, "stop", ControlScope.Item("kb-7"), "tester", TextWriter.Null, None, sources));
        Assert.Empty(b.Built);

        // With its source configured the command goes on to the run.
        using var configured = b.Registry(Options(Core, Kanban));
        Assert.Equal(0, await FactoryRunner.RunCommandAsync(Options(Core, Kanban),
            _ => Task.FromResult<string?>(null),
            _ => { calls.Add("run"); return Task.FromResult(new RunOutcome(1, WorkState.Watch, null, null, null)); },
            TextWriter.Null, None, requireSource: () => configured.Of(ItemNaming.Kanban)));
        Assert.Equal(["run"], calls);
    }

    // ---- The intake loop ----

    [Fact]
    public void The_intake_loop_gets_one_lane_per_source_that_is_watched_or_has_its_credential_and_only_watched_issues_are_prepared()
    {
        var b = new Boards();
        using var sources = b.Registry(Options(Core, Kanban, Issues, Shortcut));
        var runners = new List<IWorkSource>();

        var lanes = sources.Lanes(source => { runners.Add(source); return b.Runner(source); }, new IntakeStatus(TimeProvider.System), TextWriter.Null);

        Assert.Equal(["shortcut", "github", "kanban", "recovery"], lanes.Select(l => l.Source.Naming.Source));
        Assert.Equal(lanes.Select(l => l.Source), runners);
        // GitHub issues are triaged before they are listed; a board's ready items are listed as they are.
        Assert.Equal([false, true, false, false], lanes.Select(l => l.Prepare is not null));

        static List<(string Source, bool Prepared)> Of(Boards b, WorkSourceRegistry registry) =>
            [.. registry.Lanes(b.Runner, new IntakeStatus(TimeProvider.System), TextWriter.Null).Select(l => (l.Source.Naming.Source, l.Prepare is not null))];

        // A source with its credential and no watch scope still has a lane — the ledger may hold its items from before the scope
        // was emptied — but it is not prepared: no issue is triaged for an issues source that watches no repository. (The
        // workers' GitHub App, which every run needs, is the issues source's credential.)
        using var kanbanWatched = b.Registry(Options(Core, Kanban));
        Assert.Equal([("github", false), ("kanban", false), ("recovery", false)], Of(b, kanbanWatched));
        using var tokensOnly = b.Registry(Options(Core, Kanban, ShortcutToken));
        Assert.Equal([("shortcut", false), ("github", false), ("kanban", false), ("recovery", false)], Of(b, tokensOnly));
        using var kanbanCleared = b.Registry(Options(Core, Shortcut, KanbanToken));
        Assert.Equal([("shortcut", false), ("github", false), ("kanban", false), ("recovery", false)], Of(b, kanbanCleared));
        // No credential, no lane; and nothing at all gives no lane.
        using var kanbanAlone = b.Registry(Options(Kanban));
        Assert.Equal([("kanban", false), ("recovery", false)], Of(b, kanbanAlone));
        using var none = b.Registry(Options());
        Assert.Equal([("recovery", false)], Of(b, none));
        // A watched source without its credential is refused, not given a lane that cannot work.
        using var tokenless = b.Registry(Options(Core, Kanban, new() { ["Shortcut:Watch:Epics"] = "7" }));
        Assert.Contains("Shortcut:ApiToken", Assert.Throws<MissingCredentialException>(() => Of(b, tokenless)).Message);
    }

    [Fact]
    public async Task A_source_with_its_credential_and_no_watch_scope_is_never_asked_for_ready_work_and_its_scope_is_not_checked()
    {
        var b = new Boards();
        // Ready work on every board, none of it watched.
        b.Shortcut.Add(101, FakeShortcutBoard.FactoryTeam);
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        var options = Options(Core, ShortcutToken, KanbanToken);
        using var sources = b.Registry(options);
        Assert.Equal(["shortcut", "github", "kanban", "recovery"], sources.Lanes(b.Runner, new IntakeStatus(TimeProvider.System), TextWriter.Null).Select(l => l.Source.Naming.Source));

        await b.Loop(sources).PollOnceAsync(None);

        // Each lane listed nothing, without one request to its board: nothing was claimed, worked or given a key.
        Assert.Empty(b.Shortcut.Requests);
        Assert.Empty(b.Kanban.Board.Calls);
        Assert.Empty(b.GitHub.Requests);
        Assert.Empty(b.Prs.Opened);
        Assert.Equal(FakeShortcutBoard.ToDo, (long)b.Shortcut.Story(101)["workflow_state_id"]!);
        Assert.Equal(("Ready", null), (item.Status, item.Claim?.Marker));
        await using (var db = b.Kanban.Db())
        {
            Assert.Empty(await db.WorkItems.ToListAsync(None));
        }

        // Start-up checks the scope of the watched sources only: beside a watched KanbanBoard, the Shortcut token alone asks Shortcut nothing.
        var watched = Options(Core, Kanban, ShortcutToken);
        using var beside = b.Registry(watched);
        Assert.Null(await FactoryRunner.CheckWorkStartAsync(watched, beside, TextWriter.Null, None));
        Assert.Empty(b.Shortcut.Requests);
        Assert.Equal("list_projects", Assert.Single(b.Kanban.Board.Calls).Tool);
    }

    [Fact]
    public async Task A_poll_finishes_the_pending_stop_of_an_item_whose_source_is_no_longer_watched()
    {
        var b = new Boards();
        b.Shortcut.Add(101, FakeShortcutBoard.FactoryTeam);
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        using (var both = b.Registry(Options(Core, Shortcut, Kanban)))
        {
            await b.Loop(both).PollOnceAsync(None);
        }
        Assert.Equal((WorkState.Review, WorkState.Review), ((await b.Item("sc-101"))!.State, (await b.Item("kb-1"))!.State));

        // The Shortcut watch scope is emptied with sc-101 in flight (the token stays), and a Stop is pending on it.
        await b.Controls.SetAsync(ControlScope.Item("sc-101"), ControlState.Stopping, "tester", None);
        b.Shortcut.Requests.Clear();
        using (var kanbanWatched = b.Registry(Options(Core, Kanban, ShortcutToken)))
        {
            await b.Loop(kanbanWatched).PollOnceAsync(None);
        }

        // The Shortcut lane finished the stop — on the Shortcut board — and listed nothing: no team or epic was read.
        Assert.Equal(WorkState.Cancelled, (await b.Item("sc-101"))!.State);
        Assert.Equal(FakeShortcutBoard.Backlog, (long)b.Shortcut.Story(101)["workflow_state_id"]!);
        Assert.Null(await b.Controls.GetAsync(ControlScope.Item("sc-101"), None));
        Assert.NotEmpty(b.Shortcut.Requests);
        Assert.DoesNotContain(b.Shortcut.Requests, r => Regex.IsMatch(r.PathAndQuery, @"/(groups|epics)\b"));
        Assert.Equal(WorkState.Review, (await b.Item("kb-1"))!.State);

        // The same for KanbanBoard when its watched projects are cleared with work in flight.
        await b.Controls.SetAsync(ControlScope.Item("kb-1"), ControlState.Stopping, "tester", None);
        b.Kanban.Board.Calls.Clear();
        using (var shortcutWatched = b.Registry(Options(Core, Shortcut, KanbanToken)))
        {
            await b.Loop(shortcutWatched).PollOnceAsync(None);
        }

        Assert.Equal(WorkState.Cancelled, (await b.Item("kb-1"))!.State);
        Assert.Equal(("Backlog", null), (item.Status, item.Claim?.Marker));
        Assert.DoesNotContain(b.Kanban.Board.Calls, c => c.Tool is "list_projects" or "list_work_items");
    }

    [Fact]
    public async Task A_poll_renews_the_claim_of_an_in_flight_item_on_the_lane_of_a_source_that_is_no_longer_watched()
    {
        var b = new Boards();
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        using (var watched = b.Registry(Options(Core, Kanban)))
        {
            await b.Loop(watched).PollOnceAsync(None);
        }
        Assert.Equal((WorkState.Review, "InProgress"), ((await b.Item("kb-1"))!.State, item.Status));

        // The watched projects are cleared with kb-1 in flight (the token stays): its claim is still renewed (sc-25983), by the
        // lane the registry builds for a source that only has its credential — and nothing is listed.
        b.Kanban.Board.Calls.Clear();
        using (var cleared = b.Registry(Options(Core, Shortcut, KanbanToken)))
        {
            await b.Loop(cleared).PollOnceAsync(None);
        }
        Assert.Equal(["renew_work_item_claim"], b.Kanban.Board.Calls.Select(c => c.Tool));
        Assert.Equal(KanbanWorkSource.Marker(item.Id), item.Claim!.Value.Marker);

        // Without the token there is no lane, so nothing renews it (the start-up warning says so).
        b.Kanban.Board.Calls.Clear();
        using var tokenless = b.Registry(Options(Core, Shortcut));
        await b.Loop(tokenless).PollOnceAsync(None);
        Assert.Empty(b.Kanban.Board.Calls);
        Assert.Single(await tokenless.UnreachableAsync(None));
    }

    [Fact]
    public async Task Start_up_warns_once_per_source_that_has_active_items_in_the_ledger_and_no_credential()
    {
        var b = new Boards();
        await using (var db = b.Kanban.Db())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            // KanbanBoard: two active items (one being implemented, one escalated) and a cancelled one; Shortcut: one active story.
            foreach (var (source, id, states) in new (string, string, WorkState[])[]
            {
                ("kanban", "kb-1", [WorkState.Implement]),
                ("kanban", "kb-2", [WorkState.Implement, WorkState.Escalated]),
                ("kanban", "kb-3", [WorkState.Implement, WorkState.Cancelled]),
                ("shortcut", "sc-9", [WorkState.Implement]),
            })
            {
                var row = await ledger.GetOrCreateAsync(source, id, "t", "acme/widgets", null, None);
                foreach (var state in states)
                {
                    await ledger.RecordAsync(row, state, null, null, None);
                }
            }
        }

        using var noKanban = b.Registry(Options(Core, Shortcut));
        Assert.Equal(["warning: KanbanBoard: the ledger holds 2 active item(s) of it (kb-<n>) and this source has no credential, so the factory "
            + "cannot finish, stop or resume them (the dashboard still lists them). To bring them back, set a KanbanBoard write token "
            + "(env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account 'kanban-write-token')."], await noKanban.UnreachableAsync(None));
        using var noShortcut = b.Registry(Options(Core, Kanban));
        Assert.Equal(["warning: Shortcut: the ledger holds 1 active item(s) of it (sc-<n>) and this source has no credential, so the factory "
            + "cannot finish, stop or resume them (the dashboard still lists them). To bring them back, set a Shortcut API token "
            + "(env SHORTCUT_API_TOKEN, config 'Shortcut:ApiToken' or keychain account 'shortcut-api-token')."], await noShortcut.UnreachableAsync(None));
        // A credential with no watch scope is enough: its lane finishes them, so there is nothing to warn about.
        using var tokens = b.Registry(Options(Core, ShortcutToken, KanbanToken));
        Assert.Empty(await tokens.UnreachableAsync(None));
        Assert.Empty(b.Built);
    }

    [Fact]
    public async Task With_kanban_alone_the_intake_loop_claims_and_runs_a_ready_kanban_item()
    {
        var b = new Boards();
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count whitespace-only input as zero words");
        using var sources = b.Registry(Options(Core, Kanban));

        await b.Loop(sources).PollOnceAsync(None);

        // Listed (and given its key) by the loop, claimed on the board and worked: no `factory kanban key` was needed.
        Assert.Equal(("InProgress", KanbanWorkSource.Marker(item.Id)), (item.Status, item.Claim!.Value.Marker));
        var row = await b.Item("kb-1");
        Assert.Equal(("kanban", WorkState.Review), (row!.Source, row.State));
        Assert.Equal("factory/kb-1", Assert.Single(b.Prs.Opened).Head);
        Assert.Empty(b.Shortcut.Requests);
    }

    [Fact]
    public async Task One_poll_runs_every_configured_sources_ready_items_and_each_lane_lists_only_its_own_in_flight_items()
    {
        var b = new Boards();
        b.Shortcut.Add(101, FakeShortcutBoard.FactoryTeam);
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        using var sources = b.Registry(Options(Core, Shortcut, Kanban));
        var loop = b.Loop(sources);

        await loop.PollOnceAsync(None);

        Assert.Equal(FakeShortcutBoard.InProgress, (long)b.Shortcut.Story(101)["workflow_state_id"]!);
        Assert.Equal("InProgress", item.Status);
        Assert.Equal(["factory/sc-101", "factory/kb-1"], b.Prs.Opened.Select(p => p.Head));
        // In flight per source: a number is an id within its source, so each lane lists (and would re-run) only its own items.
        await using var db = b.Kanban.Db();
        var ledger = new WorkLedger(db, TimeProvider.System);
        Assert.Equal([101], await RunPipeline.InFlightAsync(ledger, ItemNaming.Shortcut, None, b.Controls));
        Assert.Equal([1], await RunPipeline.InFlightAsync(ledger, ItemNaming.Kanban, None, b.Controls));
        Assert.Empty(await RunPipeline.InFlightAsync(ledger, ItemNaming.GitHubIssue, None, b.Controls));
    }

    // ---- Controls by id ----

    [Fact]
    public async Task Stop_by_id_stops_a_kanban_item_on_its_own_board_pr_to_draft_item_back_to_backlog_with_a_comment_claim_released_cancelled()
    {
        var b = new Boards();
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        // A Shortcut story with the same number, also in flight: the Stop must not reach it.
        b.Shortcut.Add(1, FakeShortcutBoard.FactoryTeam);
        using var sources = b.Registry(Options(Core, Shortcut, Kanban));
        await b.Loop(sources).PollOnceAsync(None);
        Assert.Equal(WorkState.Review, (await b.Item("kb-1"))!.State);
        b.Kanban.Board.Calls.Clear();

        var stopped = await b.Actions(sources).StopAsync(ControlScope.Item("kb-1"), "tester", None);

        Assert.True(stopped.Ok, stopped.Message);
        Assert.Equal($"kb-1 stopped: Cancelled, PRs drafted: {PrUrl}, claim released.", stopped.Message);
        Assert.Equal(["factory/kb-1"], b.Prs.Drafted);
        Assert.Equal(("Backlog", null), (item.Status, item.Claim?.Marker));
        var comment = Assert.Single(item.Comments, c => c.Body.Contains("was stopped by tester", StringComparison.Ordinal));
        Assert.StartsWith("[author: dark-factory] kb-1 was stopped by tester: the factory cancelled its work on it (was Review) and released its claim.", comment.Body);
        Assert.Equal(["update_work_item", "release_work_item_claim", "add_work_item_comment"], b.Kanban.Board.Writes);
        Assert.Equal(WorkState.Cancelled, (await b.Item("kb-1"))!.State);
        Assert.Null(await b.Controls.GetAsync(ControlScope.Item("kb-1"), None));
        // The Shortcut story sc-1 is untouched.
        Assert.Equal(WorkState.Review, (await b.Item("sc-1"))!.State);
        Assert.Equal(FakeShortcutBoard.InProgress, (long)b.Shortcut.Story(1)["workflow_state_id"]!);
    }

    [Fact]
    public async Task The_factory_scope_stops_every_sources_items_each_on_its_own_board_found_from_its_ledger_row()
    {
        var b = new Boards();
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        b.Shortcut.Add(101, FakeShortcutBoard.FactoryTeam);
        using var sources = b.Registry(Options(Core, Shortcut, Kanban));
        await b.Loop(sources).PollOnceAsync(None);

        var stopped = await b.Actions(sources).StopAsync(ControlScope.Factory, "tester", None);

        Assert.True(stopped.Ok, stopped.Message);
        Assert.Equal("Backlog", item.Status);
        Assert.Equal(FakeShortcutBoard.Backlog, (long)b.Shortcut.Story(101)["workflow_state_id"]!);
        Assert.Equal((WorkState.Cancelled, WorkState.Cancelled), ((await b.Item("kb-1"))!.State, (await b.Item("sc-101"))!.State));

        // An item whose source is no longer configured is reported as not stopped and keeps its Stopping control.
        var other = b.Kanban.Board.AddItem(b.Kanban.Project, "Trim words");
        await b.Loop(sources).PollOnceAsync(None);
        Assert.Equal(WorkState.Review, (await b.Item("kb-2"))!.State);
        using var shortcutOnly = b.Registry(Options(Core, Shortcut));
        var unreachable = await b.Actions(shortcutOnly).StopAsync(ControlScope.Factory, "tester", None);
        Assert.False(unreachable.Ok);
        // No lane runs a source without its credential, so the message does not promise that a poll retries it.
        Assert.Equal($"kb-2: stop NOT finished ({WorkSourceRegistry.NeedsCredential(ItemNaming.Kanban)}); it stays Stopping, and nothing finishes "
            + "the stop until that credential is set (then the next poll or `factory stop` does).", unreachable.Message);
        Assert.Equal(ControlState.Stopping, (await b.Controls.GetAsync(ControlScope.Item("kb-2"), None))!.State);
        Assert.Equal("InProgress", other.Status);
    }

    [Fact]
    public async Task Stop_of_one_item_whose_source_has_no_credential_is_refused_before_anything_is_written()
    {
        var b = new Boards();
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        using (var both = b.Registry(Options(Core, Shortcut, Kanban)))
        {
            await b.Loop(both).PollOnceAsync(None);
        }
        Assert.Equal(WorkState.Review, (await b.Item("kb-1"))!.State);
        b.Kanban.Board.Calls.Clear();
        // The dashboard's Stop (ControlActions over the host's registry), with the Kanban token gone.
        using var shortcutOnly = b.Registry(Options(Core, Shortcut));

        var refused = await b.Actions(shortcutOnly).StopAsync(ControlScope.Item("kb-1"), "tester", None);

        Assert.False(refused.Ok);
        Assert.Equal("kb-1: NOT stopped, and nothing was written. The KanbanBoard work source is not configured (kb-<n> names a Kanban item): it needs "
            + "a KanbanBoard write token (env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account 'kanban-write-token').", refused.Message);
        // No Stopping control: one would stay for good (no lane to finish it, and a Stopping scope cannot be continued).
        Assert.Null(await b.Controls.GetAsync(ControlScope.Item("kb-1"), None));
        Assert.Equal(WorkState.Review, (await b.Item("kb-1"))!.State);
        Assert.Empty(b.Kanban.Board.Calls);
        Assert.Equal("InProgress", item.Status);
        // An item the ledger does not know is refused the same way.
        Assert.False((await b.Actions(shortcutOnly).StopAsync(ControlScope.Item("kb-7"), "tester", None)).Ok);
        Assert.Null(await b.Controls.GetAsync(ControlScope.Item("kb-7"), None));
    }

    [Fact]
    public async Task Pause_and_continue_by_id_hold_and_release_a_kanban_item_as_they_do_a_shortcut_story()
    {
        var b = new Boards();
        var item = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words");
        using var sources = b.Registry(Options(Core, Kanban));
        Assert.Equal(1, await b.Kanban.Key(item));
        var actions = b.Actions(sources);

        Assert.True((await actions.PauseAsync(ControlScope.Item("kb-1"), "tester", None)).Ok);
        await b.Loop(sources).PollOnceAsync(None);

        // Paused before it was picked up: nothing is claimed or worked.
        Assert.Equal(("Ready", null), (item.Status, item.Claim?.Marker));
        Assert.Empty(b.Kanban.Board.Writes);
        Assert.Empty(b.Prs.Opened);

        Assert.True((await actions.ContinueAsync(ControlScope.Item("kb-1"), "tester", None)).Ok);
        await b.Loop(sources).PollOnceAsync(None);

        Assert.Equal(("InProgress", KanbanWorkSource.Marker(item.Id)), (item.Status, item.Claim?.Marker));
        Assert.Equal(WorkState.Review, (await b.Item("kb-1"))!.State);
    }

    [Fact]
    public async Task An_epic_scope_reaches_only_its_own_sources_items_and_asks_each_its_own_board()
    {
        // Shortcut's epics are named by their number, Kanban's by a ledger key with the prefix; an issue is in no epic.
        Assert.Equal([("shortcut", EpicNaming.Number), ("github", EpicNaming.None), ("kanban", EpicNaming.Key), ("recovery", EpicNaming.None)],
            ItemNaming.All.Select(n => (n.Source, n.Epics)));
        var b = new Boards();
        await using (var db = b.Kanban.Db())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            // In flight with no epic in the ledger: a Kanban item, an issue's item and a Shortcut story.
            foreach (var naming in ItemNaming.All)
            {
                var row = await ledger.GetOrCreateAsync(naming.Source, naming.Format(5), "t", "acme/widgets", null, None);
                await ledger.RecordAsync(row, WorkState.Implement, null, null, None);
            }
        }
        var asked = new List<ItemNaming>();
        var story = new FakeWorkSource(new WorkStory(5, "s", null, "chore", "u", ItemNaming.Shortcut)) { Epic = new WorkEpic(9, "Phase", null, "u") };
        var actions = new ControlActions(b.Controls, b.Kanban.Contexts, sources: naming =>
        {
            asked.Add(naming);
            return naming == ItemNaming.Shortcut ? story : null;
        }, locks: b.Locks);

        var paused = await actions.PauseAsync(ControlScope.Epic(ItemNaming.Shortcut.Epic(9)), "tester", None);

        // Only the Shortcut story was looked up, on the Shortcut board; the others are not Shortcut's and are not "unknown".
        Assert.True(paused.Ok, paused.Message);
        // (Asked for twice: whether the epic can exist, then the story's own board.)
        Assert.Equal([ItemNaming.Shortcut, ItemNaming.Shortcut], asked);
        Assert.Equal(9, (await b.Item("sc-5"))!.EpicId);
        Assert.Null((await b.Item("kb-5"))!.EpicId);

        // A story whose board is not configured cannot be asked: reported, never silently skipped.
        await using (var db = b.Kanban.Db())
        {
            var row = await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-6", "t", "acme/widgets", null, None);
            await new WorkLedger(db, TimeProvider.System).RecordAsync(row, WorkState.Implement, null, null, None);
        }
        var unconfigured = await new ControlActions(b.Controls, b.Kanban.Contexts, sources: _ => null)
            .PauseAsync(ControlScope.Epic(ItemNaming.Shortcut.Epic(9)), "tester", None);
        Assert.False(unconfigured.Ok);
        Assert.Contains("1 item(s) with an unknown epic NOT paused: sc-6", unconfigured.Message);
        // The Kanban epic scope reaches the Kanban item alone: the story and the issue's item are not its to resolve.
        var key = await new KanbanKeys(b.Kanban.Contexts).EpicKeyAsync(Guid.NewGuid(), b.Kanban.Project.Id, None);
        var kanban = await new ControlActions(b.Controls, b.Kanban.Contexts, sources: _ => null)
            .PauseAsync(ControlScope.Epic(ItemNaming.Kanban.Epic(key)), "tester", None);
        Assert.Contains("1 item(s) with an unknown epic NOT paused: kb-5 ", kanban.Message);
    }

    // ---- Kanban epics (sc-25985) ----

    /// <summary>A Shortcut story in flight whose Shortcut epic has the number <paramref name="epic"/>: a Kanban epic's key may be the same number.</summary>
    private static async Task ShortcutStoryInEpicAsync(Boards b, string id, long epic)
    {
        await using var db = b.Kanban.Db();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var row = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, id, "A story", "acme/widgets", null, None, epic);
        await ledger.RecordAsync(row, WorkState.Implement, null, null, None);
    }

    [Fact]
    public async Task Pausing_a_kanban_epic_pauses_its_items_claims_nothing_new_in_it_and_leaves_the_shortcut_epic_of_that_number_alone()
    {
        var b = new Boards();
        var epic = b.Kanban.Board.AddEpic(b.Kanban.Project, "Word counting");
        var first = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words", epic: epic);
        using var sources = b.Registry(Options(Core, Kanban));
        await b.Loop(sources).PollOnceAsync(None);
        // The epic got its ledger key when the item's spec was read, and the item's row holds it.
        Assert.Equal((WorkState.Review, (long?)1), ((await b.Item("kb-1"))!.State, (await b.Item("kb-1"))!.EpicId));
        await using (var db = b.Kanban.Db())
        {
            var key = await db.KanbanEpics.SingleAsync();
            Assert.Equal((1, epic.Id, b.Kanban.Project.Id), (key.Id, key.EpicId, key.ProjectId));
        }
        // Shortcut epic 1 is another epic: the same number, another source.
        await ShortcutStoryInEpicAsync(b, "sc-50", 1);
        var second = b.Kanban.Board.AddItem(b.Kanban.Project, "Trim words", epic: epic);
        var outside = b.Kanban.Board.AddItem(b.Kanban.Project, "Split words");
        var actions = b.Actions(sources);

        var paused = await actions.PauseAsync("epic:kb-1", "tester", None);

        Assert.True(paused.Ok, paused.Message);
        Assert.Equal(ControlState.Paused, (await b.Controls.GetAsync("epic:kb-1", None))!.State);
        Assert.Null(await b.Controls.GetAsync("epic:1", None));
        Assert.Equal(ControlState.Paused, await b.Controls.EffectiveAsync("kb-1", 1, None));
        Assert.Equal(ControlState.Running, await b.Controls.EffectiveAsync("sc-50", 1, None));

        await b.Loop(sources).PollOnceAsync(None);

        // Nothing new is claimed in the paused epic; an item outside it is worked as usual.
        Assert.Equal(("Ready", null), (second.Status, second.Claim?.Marker));
        Assert.Null(await b.Item("kb-2"));
        Assert.Equal("InProgress", outside.Status);
        Assert.Equal(WorkState.Review, (await b.Item("kb-3"))!.State);
        Assert.Equal("InProgress", first.Status);

        Assert.True((await actions.ContinueAsync("epic:kb-1", "tester", None)).Ok);
        await b.Loop(sources).PollOnceAsync(None);

        Assert.Equal(ControlState.Running, await b.Controls.EffectiveAsync("kb-1", 1, None));
        Assert.Equal("InProgress", second.Status);
        Assert.Equal((WorkState.Review, (long?)1), ((await b.Item("kb-2"))!.State, (await b.Item("kb-2"))!.EpicId));

        // And the other way round: a row `epic:1`, as written before Kanban epics had scopes, holds the Shortcut story alone.
        Assert.True((await new ControlActions(b.Controls, b.Kanban.Contexts).PauseAsync("epic:1", "tester", None)).Ok);
        Assert.Equal(ControlState.Paused, await b.Controls.EffectiveAsync("sc-50", 1, None));
        Assert.Equal(ControlState.Running, await b.Controls.EffectiveAsync("kb-1", 1, None));
        Assert.Equal(ControlState.Running, await b.Controls.EffectiveAsync("kb-2", 1, None));
    }

    [Fact]
    public async Task Stopping_a_kanban_epic_stops_its_unfinished_items_only_and_is_not_ok_while_a_stop_is_unfinished()
    {
        var b = new Boards();
        var (epic, other) = (b.Kanban.Board.AddEpic(b.Kanban.Project, "Word counting"), b.Kanban.Board.AddEpic(b.Kanban.Project, "Trimming"));
        var first = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words", epic: epic);
        var second = b.Kanban.Board.AddItem(b.Kanban.Project, "Count lines", epic: epic);
        var elsewhere = b.Kanban.Board.AddItem(b.Kanban.Project, "Trim words", epic: other);
        using var sources = b.Registry(Options(Core, Kanban));
        await b.Loop(sources).PollOnceAsync(None);
        Assert.Equal([(long?)1, 1, 2], new[] { (await b.Item("kb-1"))!.EpicId, (await b.Item("kb-2"))!.EpicId, (await b.Item("kb-3"))!.EpicId });
        await ShortcutStoryInEpicAsync(b, "sc-50", 1);
        // The board refuses the second item's move for now: its stop cannot finish.
        b.Kanban.Board.Instead = (tool, arguments) => tool is "update_work_item" or "move_work_item" && Guid.Parse((string)arguments["itemId"]!) == second.Id
            ? FakeKanbanBoard.Refusal("forbidden", "Not now.")
            : null;

        var unfinished = await b.Actions(sources).StopAsync("epic:kb-1", "tester", None);

        // Non-zero exit while a stop is unfinished; the item keeps its Stopping control for the next poll or `factory stop`.
        Assert.False(unfinished.Ok);
        Assert.Equal(1, unfinished.ExitCode);
        Assert.Contains("kb-2: stop NOT finished", unfinished.Message);
        Assert.Equal((WorkState.Cancelled, "Backlog", null), ((await b.Item("kb-1"))!.State, first.Status, first.Claim?.Marker));
        Assert.Equal(WorkState.Review, (await b.Item("kb-2"))!.State);
        Assert.Equal(ControlState.Stopping, (await b.Controls.GetAsync(ControlScope.Item("kb-2"), None))!.State);

        b.Kanban.Board.Instead = null;
        var stopped = await b.Actions(sources).StopAsync("epic:kb-1", "tester", None);

        Assert.True(stopped.Ok, stopped.Message);
        Assert.Equal(0, stopped.ExitCode);
        Assert.Equal((WorkState.Cancelled, "Backlog"), ((await b.Item("kb-2"))!.State, second.Status));
        // Another Kanban epic's item and the story of Shortcut epic 1 were never the scope's.
        Assert.Equal((WorkState.Review, "InProgress"), ((await b.Item("kb-3"))!.State, elsewhere.Status));
        Assert.Equal(WorkState.Implement, (await b.Item("sc-50"))!.State);
        Assert.Null(await b.Controls.GetAsync(ControlScope.Item("kb-3"), None));
        Assert.Null(await b.Controls.GetAsync(ControlScope.Item("sc-50"), None));
        Assert.Equal("epic:kb-1: no active items to stop.", (await b.Actions(sources).StopAsync("epic:kb-1", "tester", None)).Message);
    }

    [Fact]
    public async Task A_kanban_epic_scope_reaches_an_item_the_ledger_has_no_epic_for_by_asking_the_kanban_board()
    {
        var b = new Boards();
        var epic = b.Kanban.Board.AddEpic(b.Kanban.Project, "Word counting");
        var early = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words", epic: epic);
        var later = b.Kanban.Board.AddItem(b.Kanban.Project, "Count lines", epic: epic);
        var loose = b.Kanban.Board.AddItem(b.Kanban.Project, "Trim words");
        using var sources = b.Registry(Options(Core, Kanban));
        await b.Loop(sources).PollOnceAsync(None);
        // As recorded before Kanban epics had keys: the first item's row names no epic.
        await using (var db = b.Kanban.Db())
        {
            (await db.WorkItems.SingleAsync(i => i.ExternalId == "kb-1")).EpicId = null;
            await db.SaveChangesAsync();
        }
        b.Kanban.Board.Calls.Clear();

        var paused = await b.Actions(sources).PauseAsync("epic:kb-1", "tester", None);

        // Looked up on the Kanban board (reads only) and recorded, so the pause holds it; the item in no epic is in none.
        Assert.True(paused.Ok, paused.Message);
        Assert.Empty(b.Kanban.Board.Writes);
        Assert.Contains(b.Kanban.Board.Calls, c => c.Tool == "get_work_item");
        Assert.Equal(1, (await b.Item("kb-1"))!.EpicId);
        Assert.Null((await b.Item("kb-3"))!.EpicId);
        Assert.Equal(ControlState.Paused, await b.Controls.EffectiveAsync("kb-1", (await b.Item("kb-1"))!.EpicId, None));
        Assert.Equal(ControlState.Running, await b.Controls.EffectiveAsync("kb-3", (await b.Item("kb-3"))!.EpicId, None));

        // Stop finds such an item too, whether or not its epic could be recorded first.
        await using (var db = b.Kanban.Db())
        {
            (await db.WorkItems.SingleAsync(i => i.ExternalId == "kb-2")).EpicId = null;
            await db.SaveChangesAsync();
        }
        var stopped = await b.Actions(sources).StopAsync("epic:kb-1", "tester", None);
        Assert.True(stopped.Ok, stopped.Message);
        Assert.Equal((WorkState.Cancelled, WorkState.Cancelled, WorkState.Review),
            ((await b.Item("kb-1"))!.State, (await b.Item("kb-2"))!.State, (await b.Item("kb-3"))!.State));
        Assert.Equal(("Backlog", "Backlog", "InProgress"), (early.Status, later.Status, loose.Status));

        // An item whose board cannot be read is reported, never silently skipped.
        Assert.True((await b.Actions(sources).ContinueAsync("epic:kb-1", "tester", None)).Ok);
        var unread = b.Kanban.Board.AddItem(b.Kanban.Project, "Join words", epic: epic);
        await b.Loop(sources).PollOnceAsync(None);
        await using (var db = b.Kanban.Db())
        {
            (await db.WorkItems.SingleAsync(i => i.ExternalId == "kb-4")).EpicId = null;
            await db.SaveChangesAsync();
        }
        b.Kanban.Board.Instead = (tool, _) => tool == "get_work_item" ? FakeKanbanBoard.Refusal("forbidden", "Not now.") : null;
        var reported = await b.Actions(sources).PauseAsync("epic:kb-1", "tester", None);
        Assert.False(reported.Ok);
        // (kb-3 is in no epic, which only its board could say.)
        Assert.Contains("2 item(s) with an unknown epic NOT paused: kb-3, kb-4 ", reported.Message);
        Assert.Equal("InProgress", unread.Status);
    }

    [Theory]
    [InlineData("pause", "paused")]
    [InlineData("stop", "stopped")]
    public async Task An_epic_control_on_a_kanban_key_the_ledger_never_gave_out_is_refused_before_anything_is_written(string action, string done)
    {
        var b = new Boards();
        using var sources = b.Registry(Options(Core, Kanban));
        var actions = b.Actions(sources);

        var refused = action == "pause" ? await actions.PauseAsync("epic:kb-7", "tester", None) : await actions.StopAsync("epic:kb-7", "tester", None);

        // A row for it would name no epic today and land on whichever epic is given key 7 later.
        Assert.False(refused.Ok);
        Assert.Equal($"epic:kb-7: NOT {done}, and nothing was written. The factory has seen no KanbanBoard epic kb-7: an epic gets its key when the "
            + "factory first reads an item of it (the dashboard's pipeline page shows it beside the item), or ahead of that from "
            + "`factory kanban key --epic <epic GUID>`.", refused.Message);
        Assert.Empty(await b.Controls.ListAsync(None));
        Assert.Empty(b.Kanban.Board.Calls);
        // A Shortcut epic is named by its own number: one the factory has not seen yet may be paused ahead (as it always could).
        using var shortcut = b.Registry(Options(Core, Shortcut));
        Assert.True((await b.Actions(shortcut).PauseAsync("epic:7", "tester", None)).Ok);
    }

    [Theory]
    [InlineData("pause", "paused")]
    [InlineData("stop", "stopped")]
    public async Task A_kanban_epic_key_the_ledger_never_gave_out_is_refused_without_the_kanban_source_too_and_a_known_one_is_written(
        string action, string done)
    {
        // Kanban not configured (no write token), or a host with no registry at all: the check needs the ledger alone.
        var b = new Boards();
        foreach (var sources in new Func<ItemNaming, IWorkSource?>?[] { _ => null, null })
        {
            var actions = new ControlActions(b.Controls, b.Kanban.Contexts, sources: sources);

            var refused = action == "pause" ? await actions.PauseAsync("epic:kb-7", "tester", None) : await actions.StopAsync("epic:kb-7", "tester", None);

            Assert.False(refused.Ok);
            Assert.StartsWith($"epic:kb-7: NOT {done}, and nothing was written. The factory has seen no KanbanBoard epic kb-7", refused.Message);
            Assert.Empty(await b.Controls.ListAsync(None));
        }
        Assert.Empty(b.Kanban.Board.Calls);

        // A known epic's control is written all the same: an unconfigured source is no reason to refuse.
        await using (var db = b.Kanban.Db())
        {
            db.KanbanEpics.Add(new KanbanEpicKey { Id = 7, EpicId = Guid.NewGuid(), ProjectId = b.Kanban.Project.Id, FirstSeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var unconfigured = new ControlActions(b.Controls, b.Kanban.Contexts, sources: _ => null);
        var known = action == "pause" ? await unconfigured.PauseAsync("epic:kb-7", "tester", None) : await unconfigured.StopAsync("epic:kb-7", "tester", None);
        Assert.True(known.Ok, known.Message);
        if (action == "pause")
        {
            Assert.Equal([("epic:kb-7", ControlState.Paused)], (await b.Controls.ListAsync(None)).Select(c => (c.Scope, c.State)));
        }
        else
        {
            // A Stop is written on the epic's items, and it has none: accepted, not refused.
            Assert.Equal("epic:kb-7: no active items to stop.", known.Message);
        }
        // The next key is still refused, and a Shortcut epic needs no ledger row with or without its source.
        Assert.False((await unconfigured.PauseAsync("epic:kb-8", "tester", None)).Ok);
        Assert.True((await unconfigured.PauseAsync("epic:8", "tester", None)).Ok);
    }

    [Fact]
    public async Task An_epic_given_its_key_ahead_can_be_paused_before_any_item_of_it_was_read_and_the_pause_holds_its_first_item()
    {
        var b = new Boards();
        var epic = b.Kanban.Board.AddEpic(b.Kanban.Project, "Word counting");
        using var sources = b.Registry(Options(Core, Kanban));
        var actions = b.Actions(sources);
        // Before the key: the epic has no name a control could use.
        Assert.False((await actions.PauseAsync("epic:kb-1", "tester", None)).Ok);

        // `factory kanban key --epic <GUID>`: read from the board, written to the ledger alone.
        var key = await ((KanbanWorkSource)sources.Of(ItemNaming.Kanban)).EpicKeyOfAsync(epic.Id, None);

        Assert.Equal(1, key);
        Assert.Empty(b.Kanban.Board.Writes);
        var paused = await actions.PauseAsync("epic:kb-1", "tester", None);
        Assert.True(paused.Ok, paused.Message);
        Assert.Equal(ControlState.Paused, (await b.Controls.GetAsync("epic:kb-1", None))!.State);

        // The epic's first items arrive only now: the one in it is not claimed, the one outside it is worked.
        var inside = b.Kanban.Board.AddItem(b.Kanban.Project, "Count words", epic: epic);
        var outside = b.Kanban.Board.AddItem(b.Kanban.Project, "Split words");
        await b.Loop(sources).PollOnceAsync(None);

        Assert.Equal(("Ready", null), (inside.Status, inside.Claim?.Marker));
        Assert.Null(await b.Item("kb-1"));
        Assert.Equal(["claim_work_item"], b.Kanban.Board.Writes.Take(1)); // one claim: the outside item's
        Assert.Single(b.Kanban.Board.Writes, w => w == "claim_work_item");
        Assert.Equal("InProgress", outside.Status);
        Assert.Equal(WorkState.Review, (await b.Item("kb-2"))!.State);

        // Continued, the item is claimed under the very key given ahead.
        Assert.True((await actions.ContinueAsync("epic:kb-1", "tester", None)).Ok);
        await b.Loop(sources).PollOnceAsync(None);
        Assert.Equal("InProgress", inside.Status);
        Assert.Equal((WorkState.Review, (long?)1), ((await b.Item("kb-1"))!.State, (await b.Item("kb-1"))!.EpicId));
        await using (var db = b.Kanban.Db())
        {
            Assert.Equal((1, epic.Id), ((await db.KanbanEpics.SingleAsync()).Id, (await db.KanbanEpics.SingleAsync()).EpicId));
        }
    }

    // ---- The work host ----

    [Fact]
    public async Task The_work_host_builds_its_lanes_and_its_stop_from_the_registry_with_kanban_alone()
    {
        await using var app = FactoryHost.BuildWork(Options(Core, Kanban, new() { ["Factory:HostPort"] = "0" }));

        // The intake loop is built without a Shortcut token, over the one registry the dashboard's Stop also uses.
        Assert.Contains(app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is IntakeLoop);
        var sources = app.Services.GetRequiredService<WorkSourceRegistry>();
        Assert.Equal(["kanban"], sources.Configured.Select(s => s.Naming.Source));
        Assert.IsType<FactoryItemStops>(app.Services.GetRequiredService<IItemStops>());
        Assert.NotNull(app.Services.GetRequiredService<ControlActions>());

        // A watched source without its credential fails the host's start rather than be left out of the loop.
        await using var tokenless = FactoryHost.BuildWork(Options(Core, Kanban, new() { ["Factory:HostPort"] = "0", ["Shortcut:Watch:Epics"] = "7" }));
        var missing = Assert.Throws<MissingCredentialException>(() => tokenless.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList());
        Assert.Contains("Shortcut:ApiToken", missing.Message);
    }

    // ---- The command line ----

    private static async Task<string> Help(params string[] args)
    {
        var root = FactoryCli.Build((_, _, _) => Task.FromResult(0), (_, _, _, _) => Task.FromResult(0), (_, _) => Task.FromResult(0),
            _ => Task.FromResult(0), _ => Task.FromResult(0), (_, _, _) => Task.FromResult(0), (_, _, _) => Task.FromResult(0));
        var output = new StringWriter();
        Assert.Equal(0, await root.Parse([.. args, "--help"]).InvokeAsync(new() { Output = output, Error = output }));
        // One line, so wrapped help text still matches.
        return Regex.Replace(output.ToString(), @"\s+", " ");
    }

    [Fact]
    public async Task Help_names_every_sources_ids_and_no_command_is_described_as_shortcut_only()
    {
        var run = await Help("run");
        Assert.Contains("<item-id>", run);
        foreach (var example in new[] { "sc-1234", "gh-12", "kb-7" })
        {
            Assert.Contains(example, run);
        }
        Assert.Contains("The prefix names its work source, which must be configured", run);
        Assert.Contains("outside its work source's watch scope", run);
        Assert.DoesNotContain("Shortcut watch scope", run);

        var work = await Help("work");
        Assert.Contains("polls every configured work source in turn (Shortcut, KanbanBoard, GitHub issues: any of them, at least one)", work);
        Assert.DoesNotContain("watched Shortcut scope", work);

        foreach (var action in new[] { "pause", "continue", "stop" })
        {
            var control = await Help(action);
            Assert.Contains("--item", control);
            Assert.Contains("a Kanban item's, e.g. kb-7", control);
        }
        Assert.Contains("move it back to its board's Backlog with a comment, releasing the claim", await Help("stop"));
        Assert.Contains("`factory work` gives every item it lists its id by itself", await Help("kanban", "key"));
        Assert.Contains("--epic", await Help("kanban", "key"));
        Assert.Contains("so that the epic can be paused or stopped (`factory pause --epic kb-<number>`) before its first item runs", await Help("kanban", "key"));
    }

    [Theory]
    [InlineData("pause", "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic <epic> (N or kb-N), --repo <owner/name> or --factory.")]
    [InlineData("stop", "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic <epic> (N or kb-N), --repo <owner/name> or --factory.")]
    [InlineData("continue", "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic <epic> (N or kb-N), --repo <owner/name>, --factory, --usage or --freeze.")]
    public void A_control_with_no_scope_says_which_ids_an_item_takes(string action, string expected)
    {
        var root = FactoryCli.Build((_, _, _) => Task.FromResult(0), (_, _, _, _) => Task.FromResult(0), (_, _) => Task.FromResult(0),
            _ => Task.FromResult(0), _ => Task.FromResult(0), (_, _, _) => Task.FromResult(0));

        Assert.Equal([expected], root.Parse([action]).Errors.Select(e => e.Message));
        Assert.Equal(["'jr-7' is not a work item id (expected sc-<number>, <number>, gh-<number> or kb-<number>)."],
            root.Parse([action, "--item", "jr-7"]).Errors.Select(e => e.Message).Take(1));
    }

    // ---- Nothing outside a source's folder builds that source (E1) ----

    [Fact]
    public void Only_the_registry_names_the_shortcut_source_type_outside_the_shortcut_folder()
    {
        var root = Path.Combine(SandboxSupport.RepoRoot, "src", "DarkFactory.Orchestrator");
        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
            .Select(f => (Path: f, Relative: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !f.Relative.StartsWith("Shortcut/", StringComparison.Ordinal) && !f.Relative.StartsWith("obj/", StringComparison.Ordinal)
                && !f.Relative.StartsWith("bin/", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 50, $"only {files.Count} source files found under {root}");
        foreach (var (path, relative) in files)
        {
            // Code only: a comment may say where a client is used.
            var code = string.Join('\n', File.ReadLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            if (relative != "WorkSources/WorkSourceRegistry.cs" && Regex.IsMatch(code, @"\b(ShortcutWorkSource|StoryId)\b"))
            {
                offenders.Add($"{relative} names the Shortcut source's types");
            }
            // The Shortcut namespace: the registry, and the options for the watch scope's setting type.
            if (relative is not ("WorkSources/WorkSourceRegistry.cs" or "FactoryOptions.cs") && Regex.IsMatch(code, @"Orchestrator\.Shortcut\b"))
            {
                offenders.Add($"{relative} uses the Shortcut namespace");
            }
        }
        Assert.Empty(offenders);
        // The check can fail: the registry itself is the one file that does name it.
        Assert.Matches(@"\bShortcutWorkSource\b", File.ReadAllText(Path.Combine(root, "WorkSources", "WorkSourceRegistry.cs")));
    }
}
