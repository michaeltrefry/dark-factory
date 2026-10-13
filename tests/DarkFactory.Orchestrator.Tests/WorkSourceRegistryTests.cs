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

    /// <summary>Whether the environment supplies a source's token (a developer's shell): what "not configured" tests cannot then show.</summary>
    private static bool EnvironmentConfiguresASource =>
        Environment.GetEnvironmentVariable("SHORTCUT_API_TOKEN") is not null || Environment.GetEnvironmentVariable("FACTORY_KANBAN_WRITE_TOKEN") is not null;

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

    private static FactoryOptions Options(params Dictionary<string, string?>[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
                settings.SelectMany(s => s).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build(), new InMemorySecrets()); // a later one wins

    /// <summary>Both fake boards and one in-memory ledger, behind a registry over the given settings.</summary>
    private sealed class Boards
    {
        public KanbanWorkSourceTests.Harness Kanban { get; } = new();
        public FakeShortcutBoard Shortcut { get; } = new();
        public List<ItemNaming> Built { get; } = [];
        public InProcessRunLocks Locks { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public IControls Controls => new LedgerControls(Kanban.Contexts, TimeProvider.System);

        public WorkSourceRegistry Registry(FactoryOptions options) => new(options, naming =>
        {
            Built.Add(naming);
            return naming == ItemNaming.Kanban ? Kanban.Board.Client()
                : naming == ItemNaming.Shortcut ? Shortcut.Client()
                : new HttpClient(new FakeApi()) { BaseAddress = new Uri("https://github.test/") };
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
        // The refusal with no source names all of them.
        Assert.All(WorkSourceRegistry.Kinds, k => Assert.Contains(k.Settings, WorkSourceRegistry.NoneConfigured));
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
        var b = new Boards();
        // Shortcut's token and no scope (what `factory run sc-12 --ignore-scope` always worked with); nothing of KanbanBoard.
        using var sources = b.Registry(Options(Core, Issues, new() { ["Shortcut:ApiToken"] = "tok" }));

        Assert.IsType<ShortcutWorkSource>(sources.Of(ItemNaming.Shortcut));
        Assert.Same(sources.Of(ItemNaming.Shortcut), sources.Find(ItemNaming.Shortcut));
        Assert.Equal(["github"], sources.Configured.Select(s => s.Naming.Source));
        Assert.Null(sources.Find(ItemNaming.Kanban));
        var missing = Assert.Throws<MissingCredentialException>(() => sources.Of(ItemNaming.Kanban));
        Assert.Equal("The KanbanBoard work source is not configured (kb-<n> names a Kanban item): it needs a KanbanBoard write token "
            + "(env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account 'kanban-write-token') and the watched projects "
            + "(Kanban:Watch:Projects).", missing.Message);
        Assert.DoesNotContain(ItemNaming.Kanban, b.Built);

        // With no source configured at all, the refusal names what would configure each one.
        using var none = b.Registry(Options(Core));
        var nothing = Assert.Throws<MissingCredentialException>(() => none.Of(ItemNaming.Shortcut));
        Assert.StartsWith("The Shortcut work source is not configured (sc-<n> names a Shortcut story). No work source is configured", nothing.Message);
        Assert.EndsWith(WorkSourceRegistry.NoneConfigured, nothing.Message);
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
        var b = new Boards();
        var stderr = new StringWriter();
        // A token alone watches nothing: that is no configured source either, and each such source says so.
        var options = Options(Core, new() { ["Shortcut:ApiToken"] = "tok", ["Kanban:WriteToken"] = FakeKanbanBoard.FullToken });
        using var sources = b.Registry(options);

        var refusal = await FactoryRunner.CheckWorkStartAsync(options, sources, stderr, None);

        Assert.Equal(WorkSourceRegistry.NoneConfigured, refusal);
        Assert.StartsWith("No work source is configured", refusal);
        foreach (var setting in new[] { "Shortcut:ApiToken", "Shortcut:Watch:Teams", "Shortcut:Watch:Epics", "Kanban:WriteToken", "Kanban:Watch:Projects", "GitHub:Watch:Repos" })
        {
            Assert.Contains(setting, refusal);
        }
        Assert.Equal(
        [
            "Shortcut: the watch scope is empty (set Shortcut:Watch:Teams and/or Shortcut:Watch:Epics); no Shortcut story will be picked up.",
            "No GitHub issues are watched (set GitHub:Watch:Repos to triage a repo's issues).",
            "KanbanBoard: no project is watched (set Kanban:Watch:Projects); no Kanban item will be picked up.",
        ], stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(b.Built);
        Assert.Empty(b.Kanban.Board.Calls);
        Assert.Empty(b.Shortcut.Requests);
    }

    [Fact]
    public async Task Factory_work_reports_each_configured_sources_bad_scope_or_missing_credential_before_polling()
    {
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (Environment.GetEnvironmentVariable("FACTORY_ROUTER_KEY") is null)
        {
            using var sources = b.Registry(keyless);
            Assert.Contains("router key", await FactoryRunner.CheckWorkStartAsync(keyless, sources, TextWriter.Null, None));
        }
    }

    [Fact]
    public async Task Factory_run_of_an_item_whose_source_is_not_configured_exits_2_naming_the_settings_before_anything_is_asked()
    {
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        Assert.StartsWith("The KanbanBoard work source is not configured (kb-<n> names a Kanban item). No work source is configured", stderr.ToString());
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
    public void The_intake_loop_gets_one_lane_per_configured_source_and_only_issues_are_prepared()
    {
        if (EnvironmentConfiguresASource)
        {
            return;
        }
        var b = new Boards();
        using var sources = b.Registry(Options(Core, Kanban, Issues, Shortcut));
        var runners = new List<IWorkSource>();

        var lanes = sources.Lanes(source => { runners.Add(source); return b.Runner(source); }, new IntakeStatus(TimeProvider.System), TextWriter.Null);

        Assert.Equal(["shortcut", "github", "kanban"], lanes.Select(l => l.Source.Naming.Source));
        Assert.Equal(lanes.Select(l => l.Source), runners);
        // GitHub issues are triaged before they are listed; a board's ready items are listed as they are.
        Assert.Equal([false, true, false], lanes.Select(l => l.Prepare is not null));
        using var kanbanOnly = b.Registry(Options(Core, Kanban));
        Assert.Equal(["kanban"], kanbanOnly.Lanes(b.Runner, new IntakeStatus(TimeProvider.System), TextWriter.Null).Select(l => l.Source.Naming.Source));
        using var none = b.Registry(Options(Core));
        Assert.Empty(none.Lanes(b.Runner, new IntakeStatus(TimeProvider.System), TextWriter.Null));
    }

    [Fact]
    public async Task With_kanban_alone_the_intake_loop_claims_and_runs_a_ready_kanban_item()
    {
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
        Assert.Contains("kb-2: stop NOT finished (The KanbanBoard work source is not configured", unreachable.Message);
        Assert.Equal(ControlState.Stopping, (await b.Controls.GetAsync(ControlScope.Item("kb-2"), None))!.State);
        Assert.Equal("InProgress", other.Status);
    }

    [Fact]
    public async Task Pause_and_continue_by_id_hold_and_release_a_kanban_item_as_they_do_a_shortcut_story()
    {
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
    public async Task An_epic_scope_reaches_only_sources_whose_epics_have_numbers_and_asks_each_item_its_own_board()
    {
        Assert.Equal(["shortcut"], ItemNaming.All.Where(n => n.NumberedEpics).Select(n => n.Source));
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
        var story = new FakeWorkSource(new WorkStory(5, "s", null, "chore", "u")) { Epic = new WorkEpic(9, "Phase", null, "u") };
        var actions = new ControlActions(b.Controls, b.Kanban.Contexts, sources: naming =>
        {
            asked.Add(naming);
            return naming == ItemNaming.Shortcut ? story : null;
        }, locks: b.Locks);

        var paused = await actions.PauseAsync(ControlScope.Epic(9), "tester", None);

        // Only the Shortcut story was looked up, on the Shortcut board; the others are in no numbered epic and are not "unknown".
        Assert.True(paused.Ok, paused.Message);
        Assert.Equal([ItemNaming.Shortcut], asked);
        Assert.Equal(9, (await b.Item("sc-5"))!.EpicId);
        Assert.Null((await b.Item("kb-5"))!.EpicId);

        // A story whose board is not configured cannot be asked: reported, never silently skipped.
        await using (var db = b.Kanban.Db())
        {
            var row = await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-6", "t", "acme/widgets", null, None);
            await new WorkLedger(db, TimeProvider.System).RecordAsync(row, WorkState.Implement, null, null, None);
        }
        var unconfigured = await new ControlActions(b.Controls, b.Kanban.Contexts, sources: _ => null).PauseAsync(ControlScope.Epic(9), "tester", None);
        Assert.False(unconfigured.Ok);
        Assert.Contains("1 item(s) with an unknown epic NOT paused: sc-6", unconfigured.Message);
    }

    // ---- The work host ----

    [Fact]
    public async Task The_work_host_builds_its_lanes_and_its_stop_from_the_registry_with_kanban_alone()
    {
        if (EnvironmentConfiguresASource)
        {
            return;
        }
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
            _ => Task.FromResult(0), _ => Task.FromResult(0), (_, _, _) => Task.FromResult(0), (_, _) => Task.FromResult(0));
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
    }

    [Theory]
    [InlineData("pause", "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic N or --factory.")]
    [InlineData("stop", "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic N or --factory.")]
    [InlineData("continue", "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic N, --factory, --usage or --freeze.")]
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
