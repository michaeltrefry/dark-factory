using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Adapter behaviour the recorded fixtures cannot show: retries, scope validation, idempotent child creation.</summary>
public class ShortcutWorkSourceTests
{
    private static readonly WatchScope Team = new(["darkfactory"], []);

    /// <summary>Records every delay asked for and lets it elapse at once.</summary>
    private sealed class InstantTime : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }
            return System.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    private const string StoryJson = """
        {"id":1,"name":"n","description":"d","story_type":"bug","app_url":"https://app.shortcut.com/t/story/1","archived":false,
         "workflow_id":1,"workflow_state_id":2,"group_id":null,"epic_id":null,"owner_ids":[],"labels":[],"external_links":[]}
        """;

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("busy") };
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }
        return response;
    }

    private static (FakeApi Api, InstantTime Time, ShortcutWorkSource Source) Api()
    {
        var api = new FakeApi();
        var time = new InstantTime();
        return (api, time, new ShortcutWorkSource(api.Client(ShortcutWorkSource.DefaultBaseAddress.ToString()), "tok", Team, time));
    }

    [Fact]
    public async Task Rate_limited_call_is_retried_after_its_retry_after()
    {
        var (api, time, source) = Api();
        var calls = 0;
        api.On("GET /api/v3/stories/1", _ => ++calls == 1
            ? Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7))
            : FakeApi.Json(HttpStatusCode.OK, StoryJson));

        var spec = await source.ReadSpecAsync(1, CancellationToken.None);

        Assert.Equal(1, spec.Story.Id);
        Assert.Equal(2, calls);
        Assert.Equal([TimeSpan.FromSeconds(7)], time.Delays);
    }

    [Fact]
    public async Task Rate_limited_post_is_retried_too_because_it_was_not_processed()
    {
        var (api, _, source) = Api();
        var calls = 0;
        api.On("POST /api/v3/stories/1/comments", _ => ++calls == 1
            ? Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2))
            : FakeApi.Json(HttpStatusCode.Created, """{"id":5}"""));

        await source.CommentAsync(1, "hi", CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unavailable_get_is_retried_with_backoff_until_it_gives_up()
    {
        var (api, time, source) = Api();
        api.On("GET /api/v3/stories/1", _ => Status(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadSpecAsync(1, CancellationToken.None));

        Assert.StartsWith("Shortcut GET stories/1 failed: 503", ex.Message);
        Assert.Equal(ShortcutWorkSource.MaxAttempts, api.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], time.Delays);
    }

    [Fact]
    public async Task Bad_gateway_get_then_success_returns_the_result()
    {
        var (api, _, source) = Api();
        var calls = 0;
        api.On("GET /api/v3/stories/1", _ => ++calls == 1 ? Status(HttpStatusCode.BadGateway) : FakeApi.Json(HttpStatusCode.OK, StoryJson));

        Assert.Equal(1, (await source.ReadSpecAsync(1, CancellationToken.None)).Story.Id);
    }

    [Fact]
    public async Task Unavailable_post_is_not_repeated_because_it_may_have_been_processed()
    {
        var (api, time, source) = Api();
        api.On("POST /api/v3/stories/1/comments", _ => Status(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CommentAsync(1, "hi", CancellationToken.None));

        Assert.Single(api.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Scope_validation_names_an_unknown_team_or_epic_and_passes_a_real_scope()
    {
        var board = new FakeShortcutBoard();
        await new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [77])).ValidateScopeAsync(CancellationToken.None);

        var team = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["nope"], [])).ValidateScopeAsync(CancellationToken.None));
        Assert.Contains("no Shortcut team 'nope'", team.Message);
        var epic = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShortcutWorkSource(board.Client(), "tok", new WatchScope([], [99999])).ValidateScopeAsync(CancellationToken.None));
        Assert.Contains("Shortcut epic 99999", epic.Message);
    }

    private static FactoryOptions Options(string teams) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Shortcut:ApiToken"] = "tok",
            ["Shortcut:Watch:Teams"] = teams,
        }).Build(), new InMemorySecrets());

    [Fact]
    public async Task Work_start_up_check_reports_a_bad_scope_before_polling()
    {
        var board = new FakeShortcutBoard();
        Assert.Null(await FactoryRunner.CheckWatchScopeAsync(Options("darkfactory"), board.Client(), CancellationToken.None));
        Assert.Contains("no Shortcut team 'nope'", await FactoryRunner.CheckWatchScopeAsync(Options("darkfactory,nope"), board.Client(), CancellationToken.None));
    }

    private static readonly ChildItem[] Plan =
    [
        new("child 1", "First.", "chore", []),
        new("child 2", "Second; needs the first.", "chore", [0]),
    ];

    private static List<JsonObject> Named(FakeShortcutBoard board, string name) =>
        board.Stories.Where(s => (string?)s["name"] == name && !(bool)s["archived"]!).ToList();

    [Fact]
    public async Task Retried_child_creation_after_a_partial_failure_creates_no_duplicates()
    {
        var board = new FakeShortcutBoard { FailStoryCreate = 2 };
        board.Add(500, FakeShortcutBoard.FactoryTeam);
        board.Add(501, FakeShortcutBoard.FactoryTeam, name: "child 2");
        board.Story(501)["archived"] = true; // an archived namesake is not reused
        var source = new ShortcutWorkSource(board.Client(), "tok", Team);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CreateChildrenAsync(500, Plan, CancellationToken.None));
        Assert.Single(Named(board, "child 1"));
        Assert.Empty(Named(board, "child 2"));

        var ids = await source.CreateChildrenAsync(500, Plan, CancellationToken.None);

        Assert.Equal([(int)Named(board, "child 1").Single()["id"]!, (int)Named(board, "child 2").Single()["id"]!], ids);
        Assert.DoesNotContain(501, ids);
        var link = Assert.Single(board.Links);
        Assert.Equal((ids[0], "blocks", ids[1]), ((int)link["subject_id"]!, (string)link["verb"]!, (int)link["object_id"]!));
    }

    [Fact]
    public async Task Repeating_a_completed_child_creation_writes_nothing()
    {
        var board = new FakeShortcutBoard();
        board.Add(500, FakeShortcutBoard.FactoryTeam, epic: 77);
        var source = new ShortcutWorkSource(board.Client(), "tok", Team);
        var first = await source.CreateChildrenAsync(500, Plan, CancellationToken.None);
        var writes = board.Requests.Count(r => r.Method == HttpMethod.Post);

        var second = await source.CreateChildrenAsync(500, Plan, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(writes, board.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.Single(board.Links);
        Assert.Contains(board.Requests, r => r.PathAndQuery == "/api/v3/epics/77/stories");
    }
}
