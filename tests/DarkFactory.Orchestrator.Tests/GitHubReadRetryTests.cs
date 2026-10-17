using System.Net;
using System.Security.Cryptography;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// GitHub reads retried on a transient answer (sc-25992: a single 503 on a pull request read escalated an item in the live
/// P4b-AT5 run), through the production client (<see cref="OutboundHttp.GitHubApi()"/>'s <see cref="GitHubReadRetry"/>) over a fake API.
/// </summary>
public class GitHubReadRetryTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private const string Repo = "/repos/michaeltrefry/dark-factory-sandbox";
    private const string Pull = """
        {"number":7,"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/7","state":"open","merged":false,"draft":false,
         "merge_commit_sha":null,"changed_files":3,"head":{"ref":"factory/sc-1","sha":"aa"},"base":{"repo":{"default_branch":"main"},"ref":"main","sha":"b0"}}
        """;

    /// <summary>
    /// Records the backoff delays and lets them elapse at once; an attempt's own timeout (<see cref="GitHubReadRetry.AttemptTimeout"/>)
    /// fires at once only when <see cref="FireTimeouts"/>, else never.
    /// </summary>
    private sealed class TestTime : TimeProvider
    {
        public bool FireTimeouts { get; init; }
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == GitHubReadRetry.AttemptTimeout)
            {
                return System.CreateTimer(callback, state, FireTimeouts ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, period);
            }
            lock (Delays)
            {
                Delays.Add(dueTime);
            }
            return System.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    private static (GitHubGate Gate, FakeApi Api, TestTime Time) Gate(Action<FakeApi> routes)
    {
        var api = new FakeApi()
            .On($"GET {Repo}/installation", HttpStatusCode.OK, """{"id":555}""")
            .On("POST /app/installations/555/access_tokens", _ => FakeApi.Json(HttpStatusCode.Created,
                $$"""{"token":"ghs_gate","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(59):O}}"}"""));
        routes(api);
        var time = new TestTime();
        var client = OutboundHttp.GitHubApi(api, time);
        return (new GitHubGate(client, new GitHubApp(client, "4242", Key.ExportRSAPrivateKeyPem(), TimeProvider.System)), api, time);
    }

    private static int Count(FakeApi api, string methodAndPath) =>
        api.Requests.Count(r => $"{r.Method} {r.PathAndQuery}" == methodAndPath);

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("unavailable") };

    [Fact]
    public void The_production_github_client_retries_its_reads()
    {
        using var http = OutboundHttp.GitHubApi();

        var handler = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(http);

        Assert.IsType<GitHubReadRetry>(handler);
        Assert.Equal(OutboundHttp.GitHubApiBase, http.BaseAddress);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_pull_request_read_answered_a_transient_error_is_read_again(HttpStatusCode status)
    {
        var calls = 0;
        var (gate, api, time) = Gate(a => a.On($"GET {Repo}/pulls/7", _ => ++calls == 1 ? Status(status) : FakeApi.Json(HttpStatusCode.OK, Pull)));

        var pull = await gate.GetPullAsync(Sandbox, 7, CancellationToken.None);

        Assert.Equal(7, pull.Number);
        Assert.Equal(2, Count(api, $"GET {Repo}/pulls/7"));
        Assert.Equal([TimeSpan.FromSeconds(1)], time.Delays);
    }

    [Fact]
    public async Task A_read_that_stays_unavailable_fails_as_before_after_the_bound()
    {
        var (gate, api, time) = Gate(a => a.On($"GET {Repo}/pulls/7", _ => Status(HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<GitHubGatewayException>(() => gate.GetPullAsync(Sandbox, 7, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal("GitHub read pull request #7 failed: 503 unavailable", ex.Message);
        Assert.Equal(GitHubReadRetry.MaxAttempts, Count(api, $"GET {Repo}/pulls/7"));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], time.Delays);
    }

    [Fact]
    public async Task A_read_answered_not_found_is_not_read_again()
    {
        var (gate, api, time) = Gate(a => a.On($"GET {Repo}/pulls/7", _ => Status(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<GitHubNotFoundException>(() => gate.GetPullAsync(Sandbox, 7, CancellationToken.None));

        Assert.Equal(1, Count(api, $"GET {Repo}/pulls/7"));
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Writes_answered_unavailable_are_not_sent_again()
    {
        // The installation token is minted by a POST and the merge is a PUT: either may have been processed.
        var (gate, api, time) = Gate(a => a
            .On("POST /app/installations/555/access_tokens", _ => Status(HttpStatusCode.ServiceUnavailable)));

        await Assert.ThrowsAnyAsync<Exception>(() => gate.GetPullAsync(Sandbox, 7, CancellationToken.None));

        Assert.Equal(1, Count(api, "POST /app/installations/555/access_tokens"));
        Assert.Equal(0, Count(api, $"GET {Repo}/pulls/7"));
        Assert.Empty(time.Delays);

        var (merging, mergeApi, mergeTime) = Gate(a => a.On($"PUT {Repo}/pulls/7/merge", _ => Status(HttpStatusCode.ServiceUnavailable)));
        try
        {
            await merging.MergeAsync(Sandbox, 7, "aa", CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // How the merge reports a 503 is its own business; that it is sent once is this test's.
        }
        Assert.Equal(1, Count(mergeApi, $"PUT {Repo}/pulls/7/merge"));
        Assert.Empty(mergeTime.Delays);
    }

    /// <summary>A handler whose first <paramref name="hangs"/> requests wait until cancelled, or fail in the network; then 200.</summary>
    private sealed class Flaky(int hangs, bool network) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (++Requests > hangs)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            if (network)
            {
                throw new HttpRequestException("connection reset");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_read_with_no_answer_within_its_attempt_timeout_is_sent_again()
    {
        var flaky = new Flaky(hangs: 1, network: false);
        using var http = OutboundHttp.GitHubApi(flaky, new TestTime { FireTimeouts = true });

        using var response = await http.GetAsync("repos/o/r/pulls/7", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, flaky.Requests);
    }

    [Fact]
    public async Task A_read_that_failed_in_the_network_is_sent_again()
    {
        var flaky = new Flaky(hangs: 1, network: true);
        using var http = OutboundHttp.GitHubApi(flaky, new TestTime());

        using var response = await http.GetAsync("repos/o/r/pulls/7", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, flaky.Requests);
    }

    [Fact]
    public async Task A_read_the_caller_cancels_is_not_sent_again()
    {
        var flaky = new Flaky(hangs: 5, network: false);
        using var http = OutboundHttp.GitHubApi(flaky, new TestTime());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("repos/o/r/pulls/7", cts.Token));

        Assert.Equal(1, flaky.Requests);
    }
}
