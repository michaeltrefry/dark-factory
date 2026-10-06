using Bunit;
using Bunit.TestDoubles;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Dashboard.Components;
using DarkFactory.Orchestrator.Dashboard.Components.Pages;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.AspNetCore.Builder;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DarkFactory.Orchestrator.Tests;

internal static class TranscriptLines
{
    public const string Sid = "sess-transcript";

    public static readonly string BigOutput = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));

    public static readonly string[] All =
    [
        $$$"""{"type":"system","subtype":"init","model":"claude-opus-5","cwd":"/work/wt","session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"I'll run the tests."}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test","description":"Run tests"}}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":{{{System.Text.Json.JsonSerializer.Serialize(BigOutput)}}}}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"README.md"}}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"no such file"}],"is_error":true}]},"session_id":"{{{Sid}}}"}""",
        "Warning: not json at all",
        $$$"""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"result","subtype":"success","is_error":false,"num_turns":3,"duration_ms":4569,"result":"All green.","session_id":"{{{Sid}}}"}""",
    ];

    public static SessionEventMessage Event(int i) =>
        new(i + 1, "x", null, All[i], DateTimeOffset.UnixEpoch);
}

public class TranscriptFormatterTests
{
    [Fact]
    public void Messages_tool_calls_commands_outputs_results_and_raw_lines_are_readable()
    {
        var formatter = new TranscriptFormatter();
        var entries = Enumerable.Range(0, TranscriptLines.All.Length).SelectMany(i => formatter.Format(TranscriptLines.Event(i))).ToList();

        Assert.Equal(
            [TranscriptKind.System, TranscriptKind.Text, TranscriptKind.Command, TranscriptKind.ToolResult, TranscriptKind.ToolCall,
             TranscriptKind.ToolResult, TranscriptKind.Raw, TranscriptKind.Raw, TranscriptKind.Result],
            entries.Select(e => e.Kind));
        Assert.Contains("model: claude-opus-5", entries[0].Body);
        Assert.Equal("I'll run the tests.", entries[1].Body);
        Assert.Equal(("Bash — Run tests", "dotnet test"), (entries[2].Title, entries[2].Body));
        Assert.Equal(("output", TranscriptLines.BigOutput), (entries[3].Title, entries[3].Body));
        Assert.Equal("Read", entries[4].Title);
        Assert.Contains("\"file_path\": \"README.md\"", entries[4].Body);
        Assert.Equal(("Read result", "no such file", true), (entries[5].Title, entries[5].Body, entries[5].IsError));
        Assert.Equal("Warning: not json at all", entries[6].Body);           // raw lines as-is
        Assert.Equal(TranscriptLines.All[7], entries[7].Body);                // unknown events as-is
        Assert.Equal("result: success", entries[8].Title);
        Assert.StartsWith("All green.", entries[8].Body);
    }

    [Fact]
    public void The_recorded_fixture_formats_without_losing_its_tool_call_or_result()
    {
        var formatter = new TranscriptFormatter();
        var entries = StreamJsonFixture.Fresh.Select((line, i) => new SessionEventMessage(i + 1, "x", null, line, DateTimeOffset.UnixEpoch))
            .SelectMany(formatter.Format).ToList();

        Assert.Contains(entries, e => e is { Kind: TranscriptKind.ToolCall, Title: "Glob" } && e.Body.Contains("\"pattern\": \"*\""));
        Assert.Contains(entries, e => e is { Kind: TranscriptKind.ToolResult, Title: "Glob result", Body: "No files found" });
        Assert.Contains(entries, e => e.Kind == TranscriptKind.Result && e.Body.StartsWith("The directory is empty"));
        Assert.DoesNotContain(entries, e => e.Kind == TranscriptKind.Thinking); // the fixture's thinking is redacted
    }
}

/// <summary>bUnit tests of the pipeline and session pages against fakes.</summary>
public class DashboardComponentTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeData _data = new();
    private readonly PipelineChanges _changes = new();
    private readonly FakeViewers _viewers = new();

    public DashboardComponentTests()
    {
        Services.AddSingleton<IDashboardData>(_data);
        Services.AddSingleton(_changes);
        Services.AddSingleton<ISessionViewers>(_viewers);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
    }

    private static PipelineRow Row(long id, WorkState state, string? pr = null, decimal? cost = null, params SessionLink[] sessions) =>
        new(id, $"sc-{id}", $"Story {id}", "acme/widgets", state, Now.AddMinutes(-65), Now, pr, cost, sessions);

    [Fact]
    public void Pipeline_lists_each_active_item_with_state_repo_pr_elapsed_cost_and_session_links()
    {
        _data.Rows = [Row(1, WorkState.Review, "https://github.com/acme/widgets/pull/9", 1.5m,
            new SessionLink("s-1", 1, Now, Now, "succeeded", 1.5m), new SessionLink("s-2", 2, Now, null, null, null))];

        var cut = Render<Pipeline>();

        var cells = cut.FindAll("tr[data-item='1'] td").Select(td => td.TextContent.Trim()).ToList();
        Assert.Equal(["sc-1 Story 1", "Review", "acme/widgets", "PR", "1h 05m", "$1.50"], cells.Take(6));
        Assert.Equal("https://github.com/acme/widgets/pull/9", cut.Find("td.pr a").GetAttribute("href"));
        Assert.Equal(["sessions/s-1", "sessions/s-2"], cut.FindAll("td.sessions a").Select(a => a.GetAttribute("href")));
        Assert.Equal("#2 (live)", cut.FindAll("td.sessions a")[1].TextContent);
    }

    [Fact]
    public void A_pipeline_row_changes_state_when_its_ledger_changes_without_a_reload()
    {
        _data.Rows = [Row(1, WorkState.Intake), Row(2, WorkState.Implement)];
        var cut = Render<Pipeline>();
        Assert.Equal("Intake", cut.Find("tr[data-item='1'] td.state").TextContent);

        _data.Rows = [Row(1, WorkState.Implement), Row(2, WorkState.Implement)];
        _changes.Notify(1);

        cut.WaitForAssertion(() => Assert.Equal("Implement", cut.Find("tr[data-item='1'] td.state").TextContent));

        _data.Rows = [Row(2, WorkState.Implement)]; // item 1 is Done: it leaves the view
        _changes.Notify(null);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody tr")));
    }

    [Fact]
    public async Task Pipeline_unsubscribes_when_closed()
    {
        _data.Rows = [];
        var cut = Render<Pipeline>();
        Assert.Contains("No active items.", cut.Markup);

        await DisposeComponentsAsync();
        var reads = _data.Reads;
        _changes.Notify(null);

        Assert.Equal(reads, _data.Reads);
    }

    [Fact]
    public void Session_view_renders_the_transcript_readably_with_large_outputs_collapsed()
    {
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets", new SessionLink(TranscriptLines.Sid, 1, Now, Now, "succeeded", 0.25m));
        _viewers.Backlog = Enumerable.Range(0, TranscriptLines.All.Length).Select(TranscriptLines.Event).ToList();

        var cut = Render<Session>(p => p.Add(x => x.SessionId, TranscriptLines.Sid));

        cut.WaitForAssertion(() => Assert.Equal(9, cut.FindAll("li.entry").Count));
        Assert.Contains("finished: succeeded", cut.Find(".status").TextContent);
        Assert.Contains("$0.25", cut.Find(".meta").TextContent);
        var command = cut.Find("li.entry.Command");
        Assert.Equal("Bash — Run tests", command.QuerySelector(".title")!.TextContent);
        Assert.Equal("dotnet test", command.QuerySelector("pre")!.TextContent);
        var output = cut.FindAll("li.entry.ToolResult")[0];
        Assert.NotNull(output.QuerySelector("details"));          // 40 lines: collapsed
        Assert.Contains("line 1", output.QuerySelector("summary")!.TextContent);
        Assert.Equal(TranscriptLines.BigOutput, output.QuerySelector("details pre")!.TextContent);
        Assert.Contains("is-error", cut.FindAll("li.entry.ToolResult")[1].ClassName);
        Assert.Null(cut.FindAll("li.entry.ToolResult")[1].QuerySelector("details")); // short: open
        Assert.Contains(cut.FindAll("li.entry.Raw"), li => li.QuerySelector("pre")!.TextContent == "Warning: not json at all");
    }

    [Fact]
    public async Task Session_view_appends_live_events_and_leaves_the_feed_when_closed()
    {
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets", new SessionLink(TranscriptLines.Sid, 1, Now, null, null, null));
        _viewers.Backlog = [TranscriptLines.Event(0), TranscriptLines.Event(1)];

        var cut = Render<Session>(p => p.Add(x => x.SessionId, TranscriptLines.Sid));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.entry").Count));
        Assert.Contains("running (live)", cut.Find(".status").TextContent);

        _viewers.Send([TranscriptLines.Event(2)]);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.entry").Count));
        Assert.Equal("dotnet test", cut.Find("li.entry.Command pre").TextContent);

        // The session ends: the header follows the item's change notification.
        _data.Header = _data.Header with { Session = _data.Header.Session with { EndedAt = Now, ExitStatus = "succeeded" } };
        _changes.Notify(1);
        cut.WaitForAssertion(() => Assert.Contains("finished: succeeded", cut.Find(".status").TextContent));

        await DisposeComponentsAsync();
        Assert.Equal([TranscriptLines.Sid], _viewers.Left);
    }

    [Fact]
    public void An_unknown_session_says_so_and_joins_nothing()
    {
        var cut = Render<Session>(p => p.Add(x => x.SessionId, "nope"));

        Assert.Contains("Unknown session.", cut.Markup);
        Assert.Empty(_viewers.Joined);
    }

    [Fact]
    public void An_open_page_whose_login_ends_is_torn_down_and_sent_to_the_login_page()
    {
        // The real circuit provider, rechecking a real login every 50 ms against a swappable stored hash.
        var secrets = DashboardLogin.Secrets();
        var logins = new DashboardLogins(new PasswordHashSource(() => secrets.Get(SecretAccounts.DashboardPasswordHash)!, TimeProvider.System, TimeSpan.Zero), TimeProvider.System);
        var provider = new DashboardAuthStateProvider(NullLoggerFactory.Instance, logins, new DashboardAuthOptions { RevalidationInterval = TimeSpan.FromMilliseconds(50) });
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "dashboard"), .. DashboardLogins.Issue(secrets.Values[SecretAccounts.DashboardPasswordHash])], "Cookies")))));
        Services.AddSingleton<AuthenticationStateProvider>(provider);
        Services.AddAuthorizationCore();
        Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationService, Microsoft.AspNetCore.Authorization.DefaultAuthorizationService>(); // over bUnit's placeholder
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton<AntiforgeryStateProvider>(new NoAntiforgery());
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("sessions/" + TranscriptLines.Sid);
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets", new SessionLink(TranscriptLines.Sid, 1, Now, null, null, null));

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => Assert.Equal([TranscriptLines.Sid], _viewers.Joined));
        Thread.Sleep(300); // several revalidations while the login holds
        Assert.Empty(_viewers.Left);
        Assert.Contains("Story 1", cut.Markup);

        secrets.Values[SecretAccounts.DashboardPasswordHash] = DashboardAuth.HashPassword("a brand new password");

        cut.WaitForAssertion(() => Assert.Equal([TranscriptLines.Sid], _viewers.Left), TimeSpan.FromSeconds(10)); // the page left its feed
        // The page leaves its feed as it is torn down; the re-render without it can land a moment later.
        cut.WaitForAssertion(() => Assert.DoesNotContain("Story 1", cut.Markup), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.NotEmpty(navigation.History), TimeSpan.FromSeconds(10));
        var redirect = navigation.History.First();
        Assert.Equal($"/login?returnUrl={Uri.EscapeDataString("/sessions/" + TranscriptLines.Sid)}", redirect.Uri);
        Assert.True(redirect.Options.ForceLoad);
    }

    private sealed class NoAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class FakeData : IDashboardData
    {
        public IReadOnlyList<PipelineRow> Rows { get; set; } = [];
        public SessionHeader? Header { get; set; }
        public int Reads;

        public Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Rows);
        }

        public Task<SessionHeader?> SessionAsync(string claudeSessionId, CancellationToken ct) =>
            Task.FromResult(Header?.Session.ClaudeSessionId == claudeSessionId ? Header : null);
    }

    private sealed class FakeViewers : ISessionViewers
    {
        private SessionEventSink? _sink;
        public List<SessionEventMessage> Backlog { get; set; } = [];
        public List<string> Joined { get; } = [];
        public List<string> Left { get; } = [];
        private readonly Dictionary<string, string> _sessions = [];

        public async Task JoinAsync(string viewerId, string claudeSessionId, SessionEventSink send, Action abort, CancellationToken ct)
        {
            Joined.Add(claudeSessionId);
            _sessions[viewerId] = claudeSessionId;
            await send(Backlog, ct);
            _sink = send;
        }

        public void Send(IReadOnlyList<SessionEventMessage> page) => _sink!(page, CancellationToken.None).GetAwaiter().GetResult();

        public Task LeaveAsync(string viewerId)
        {
            Left.Add(_sessions[viewerId]);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// The pipeline and session pages on the real host's services against the compose Postgres: changes
/// written by another process (a separate ledger context and recorder) reach the rendered page.
/// </summary>
public sealed class DashboardLiveTests : BunitContext, IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;
    private WebApplication? _app;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_dashlive");
        _cs = _db.ConnectionString;
        await LedgerMigrations.MigrateAsync(_cs, CancellationToken.None);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = _cs,
            ["Factory:HostPort"] = "0",
        }).Build();
        _app = FactoryHost.Build(new FactoryOptions(config, DashboardLogin.Secrets()), s => s.AddSingleton<ISessionCostSource>(new FixedCost()));
        await _app.StartAsync();
        Services.AddSingleton(_app.Services.GetRequiredService<IDashboardData>());
        Services.AddSingleton(_app.Services.GetRequiredService<PipelineChanges>());
        Services.AddSingleton(_app.Services.GetRequiredService<ISessionViewers>());
        Services.AddSingleton(TimeProvider.System);
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private LedgerDbContext Context() => new(LedgerDbContext.PostgresOptions(_cs));

    private SessionRecorder Recorder() =>
        new(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), new FixedCost(), TimeProvider.System, TextWriter.Null, costRetryDelays: [TimeSpan.Zero]);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_pipeline_row_follows_ledger_writes_from_another_process_without_a_reload()
    {
        await using var db = Context();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("shortcut", "sc-41", "Live row", "acme/widgets", null, CancellationToken.None);

        var cut = Render<Pipeline>();
        cut.WaitForAssertion(() => Assert.Equal("Intake", cut.Find($"tr[data-item='{item.Id}'] td.state").TextContent), Wait);

        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Equal("Implement", cut.Find($"tr[data-item='{item.Id}'] td.state").TextContent), Wait);

        await ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, null,
            "https://github.com/acme/widgets/pull/3 https://github.com/acme/widgets/tree/factory/sc-41", CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Equal("https://github.com/acme/widgets/pull/3", cut.Find($"tr[data-item='{item.Id}'] td.pr a").GetAttribute("href")), Wait);

        // A session's cost lands on the session row (no ledger entry): the row's cost follows too.
        await using (var capture = await Recorder().StartAsync(item.Id, null, CancellationToken.None))
        {
            await capture.SetClaudeSessionIdAsync("sess-live-cost", CancellationToken.None);
            await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);
        }
        cut.WaitForAssertion(() => Assert.Equal("$0.4296", cut.Find($"tr[data-item='{item.Id}'] td.cost").TextContent), Wait);

        await ledger.RecordAsync(item, WorkState.Cancelled, null, null, CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"tr[data-item='{item.Id}']")), Wait);
    }

    [Fact]
    public async Task Opening_a_finished_session_replays_its_full_transcript_from_the_ledger()
    {
        var item = await ItemAsync();
        await using (var capture = await Recorder().StartAsync(item.Id, null, CancellationToken.None))
        {
            foreach (var line in StreamJsonFixture.Fresh)
            {
                await capture.OnLineAsync(line, CancellationToken.None);
            }
            await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);
        }

        var cut = Render<Session>(p => p.Add(x => x.SessionId, StreamJsonFixture.SessionId));

        var expected = ExpectedEntries(StreamJsonFixture.Fresh);
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.FindAll("li.entry").Select(li => int.Parse(li.GetAttribute("data-seq")!))), Wait);
        Assert.Contains("finished: succeeded", cut.Find(".status").TextContent);
        Assert.Contains(cut.FindAll("li.entry.ToolCall"), li => li.TextContent.Contains("Glob"));
    }

    [Fact]
    public async Task Opening_a_running_session_shows_its_backlog_then_streams_new_events_once_each()
    {
        var item = await ItemAsync();
        await using var capture = await Recorder().StartAsync(item.Id, null, CancellationToken.None);
        foreach (var line in StreamJsonFixture.Fresh.Take(4))
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await WaitStoredAsync(4);

        var cut = Render<Session>(p => p.Add(x => x.SessionId, StreamJsonFixture.SessionId));
        cut.WaitForAssertion(() => Assert.Equal(ExpectedEntries(StreamJsonFixture.Fresh.Take(4)), Sequences(cut)), Wait);
        Assert.Contains("running (live)", cut.Find(".status").TextContent);

        foreach (var line in StreamJsonFixture.Fresh.Skip(4))
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);

        cut.WaitForAssertion(() => Assert.Equal(ExpectedEntries(StreamJsonFixture.Fresh), Sequences(cut)), Wait);
        cut.WaitForAssertion(() => Assert.Contains("finished: succeeded", cut.Find(".status").TextContent), Wait);
    }

    private static List<int> Sequences(IRenderedComponent<Session> cut) =>
        cut.FindAll("li.entry").Select(li => int.Parse(li.GetAttribute("data-seq")!)).ToList();

    /// <summary>The entry sequence numbers the transcript should show for these lines, in order (once each).</summary>
    private static List<int> ExpectedEntries(IEnumerable<string> lines)
    {
        var formatter = new TranscriptFormatter();
        return lines.Select((line, i) => new SessionEventMessage(i + 1, "x", null, line, DateTimeOffset.UnixEpoch))
            .SelectMany(formatter.Format).Select(e => (int)e.Sequence).ToList();
    }

    private async Task<WorkItem> ItemAsync()
    {
        await using var db = Context();
        return await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-42", "t", "acme/widgets", null, CancellationToken.None);
    }

    private async Task WaitStoredAsync(int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            await using var db = Context();
            if (db.SessionEvents.Count() >= count)
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "events were not stored");
            await Task.Delay(20);
        }
    }

    private sealed class FixedCost : ISessionCostSource
    {
        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<SessionCost?>(new SessionCost(sessionId, 3, 429606));
    }
}
