using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public sealed class IosWorkflowGitHubTests
{
    private static readonly RepoRef Repo = new("acme", "ios");
    private const string Commit = "3333333333333333333333333333333333333333";
    private const string Base = "1111111111111111111111111111111111111111";
    private const string Head = "2222222222222222222222222222222222222222";
    private const string Root = "/repos/acme/ios";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
    private static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_transport_reads_only_default_commit_dispatches_with_repo_gate_token_and_reads_the_zip_result(bool wrongSideCommit)
    {
        string nonce = "";
        var api = Api()
            .On("GET " + Root, HttpStatusCode.OK, """{"default_branch":"trunk"}""")
            .On("GET " + Root + "/commits/trunk", HttpStatusCode.OK, $$$"""{"sha":"{{{Commit}}}"}""")
            .On("GET " + Root + "/contents/" + RemoteIosWorkflow.Path,
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RemoteIosWorkflow.Text) })
            .On("POST " + Root + "/actions/workflows/factory-ios-new-tests.yml/dispatches", request => {
                var body = JsonDocument.Parse(request.Body!).RootElement;
                Assert.Equal("trunk", body.GetProperty("ref").GetString());
                Assert.Equal(Base, body.GetProperty("inputs").GetProperty("base").GetString());
                nonce = body.GetProperty("inputs").GetProperty("nonce").GetString()!;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            })
            .On("GET " + Root + "/actions/workflows/factory-ios-new-tests.yml/runs", _ =>
                FakeApi.Json(HttpStatusCode.OK, $$$"""{"workflow_runs":[{"id":12,"display_title":"factory-ios-{{{nonce}}}","head_sha":"{{{Commit}}}","event":"workflow_dispatch","path":"{{{RemoteIosWorkflow.Path}}}","run_attempt":1,"status":"completed","conclusion":"success"}]}"""))
            .On("GET " + Root + "/actions/runs/12/artifacts", _ => FakeApi.Json(HttpStatusCode.OK,
                $$$"""{"total_count":2,"artifacts":[{"id":8,"name":"factory-ios-result-{{{nonce}}}-base","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}},{"id":9,"name":"factory-ios-result-{{{nonce}}}-head","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}}]}"""))
            .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip(RemoteIosNewTestsTests.WithEnvironment($$$"""{"version":1,"nonce":"{{{nonce}}}","base":"{{{Base}}}","head":"{{{Head}}}","side":"base","tests":[{"test":"TallyTests/TallyTests/testNew","base":"failed"}]}""")))
            .On("GET " + Root + "/actions/artifacts/9/zip", _ => Zip(RemoteIosNewTestsTests.WithEnvironment($$$"""{"version":1,"nonce":"{{{nonce}}}","base":"{{{Base}}}","head":"{{{(wrongSideCommit ? Base : Head)}}}","side":"head","tests":[{"test":"TallyTests/TallyTests/testNew","head":"passed"}]}""")));
        var client = Client(api);
        var result = await new RemoteIosNewTests(client) { PollInterval = TimeSpan.Zero }.RunAsync(Repo,
            new(Base, Head, ["TallyTests/TallyTests/testNew"]), CancellationToken.None);
        Assert.Equal(wrongSideCommit ? "error" : "pass", result.Outcome);
        Assert.All(api.Requests.Where(r => r.PathAndQuery.Contains("/contents/")), r => Assert.Contains("ref=" + Commit, r.PathAndQuery));
        var mints = api.Requests.Where(r => r.PathAndQuery == "/app/installations/5/access_tokens").ToArray();
        Assert.Single(mints, r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("permissions").GetProperty("actions").GetString() == "write");
        Assert.All(mints, r => Assert.Equal("ios", JsonDocument.Parse(r.Body!).RootElement.GetProperty("repositories")[0].GetString()));
        Assert.DoesNotContain("gate-token", api.Requests.Single(r => r.PathAndQuery.EndsWith("/dispatches")).Body!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_changed_workflow_at_the_real_default_read_sends_no_dispatch(bool missing)
    {
        var api = Api()
            .On("GET " + Root, HttpStatusCode.OK, """{"default_branch":"trunk"}""")
            .On("GET " + Root + "/commits/trunk", HttpStatusCode.OK, $$$"""{"sha":"{{{Commit}}}"}""")
            .On("GET " + Root + "/contents/" + RemoteIosWorkflow.Path, _ => new HttpResponseMessage(missing ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent(RemoteIosWorkflow.Text + "# modified") });
        var result = await new RemoteIosNewTests(Client(api)).RunAsync(Repo, new(Base, Head, ["TallyTests/TallyTests/testNew"]), CancellationToken.None);
        Assert.Equal("error", result.Outcome);
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("/dispatches"));
    }

    [Fact]
    public async Task A_refused_gate_write_token_sends_no_dispatch_or_other_write()
    {
        var api = new FakeApi()
            .On("GET " + Root + "/installation", HttpStatusCode.OK, """{"id":5}""")
            .On("POST /app/installations/5/access_tokens", HttpStatusCode.Forbidden, """{"message":"Actions write not granted"}""");
        var error = await Assert.ThrowsAsync<IosWorkflowNotSentException>(() => Client(api).DispatchAsync(Repo,
            new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["TallyTests/TallyTests/testNew"]), new string('a', 32), CancellationToken.None));
        Assert.Equal("remote-dispatch-not-sent: The gate App could not obtain repository Actions write authorization. Check its installation and permissions; no workflow was sent.", error.Message);
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("/dispatches"));
        Assert.All(api.Requests.Where(r => r.Method != HttpMethod.Get), r => Assert.Equal("/app/installations/5/access_tokens", r.PathAndQuery));
    }

    [Theory]
    [InlineData("../result.json", 1)]
    [InlineData("result.json", 2)]
    [InlineData("result.json", 1, true)]
    public async Task Unsafe_or_oversized_artifact_is_refused_without_extracting_files(string name, int count, bool oversized = false)
    {
        var api = Api()
            .On("GET " + Root + "/actions/runs/12/artifacts", HttpStatusCode.OK,
                """{"total_count":1,"artifacts":[{"id":8,"name":"factory-ios-result-abc-base","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}}]}""")
            .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip(oversized ? new string('x', RemoteIosNewTests.MaxResultBytes + 1) : "{}", name, count));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(api).ResultAsync(Repo, new(12, "", Commit, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "completed", "success"), "abc", CancellationToken.None));
    }

    [Theory]
    [InlineData("base", "missing")]
    [InlineData("head", "missing")]
    [InlineData("base", "runtime")]
    [InlineData("head", "runtime")]
    [InlineData("base", "deviceType")]
    [InlineData("head", "deviceType")]
    [InlineData("base", "xcode")]
    [InlineData("head", "xcode")]
    [InlineData("base", "build")]
    [InlineData("head", "build")]
    [InlineData("head", "different-xcode")]
    [InlineData("head", "different-build")]
    public async Task Artifacts_require_validated_identical_explicit_environments(string side, string corrupt)
    {
        var api = Api()
            .On("GET " + Root + "/actions/runs/12/artifacts", HttpStatusCode.OK,
                """{"total_count":2,"artifacts":[{"id":8,"name":"factory-ios-result-abc-base","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}},{"id":9,"name":"factory-ios-result-abc-head","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}}]}""")
            .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip(Side("base")))
            .On("GET " + Root + "/actions/artifacts/9/zip", _ => Zip(Side("head")));
        await Assert.ThrowsAnyAsync<Exception>(() => Client(api).ResultAsync(Repo,
            new(12, "", Commit, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "completed", "success"), "abc", CancellationToken.None));

        string Side(string value) => RemoteIosNewTestsTests.WithEnvironment(JsonSerializer.Serialize(new {
            version = 1, nonce = "abc", @base = Base, head = Head, side = value,
            tests = new[] { new { test = "TallyTests/TallyTests/testNew", @base = "failed", head = "passed" } }
        }), side == value ? corrupt : null);
    }

    [Theory]
    [InlineData("base", false)]
    [InlineData("head", false)]
    [InlineData("base", true)]
    [InlineData("head", true)]
    public async Task Execution_transport_binds_both_independent_vm_artifacts_to_one_plan(string active, bool wrongHash)
    {
        var api = Api()
            .On("GET " + Root + "/actions/runs/12/artifacts", HttpStatusCode.OK,
                """{"total_count":2,"artifacts":[{"id":8,"name":"factory-ios-result-abc-base","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}},{"id":9,"name":"factory-ios-result-abc-head","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}}]}""")
            .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip(Side("base")))
            .On("GET " + Root + "/actions/artifacts/9/zip", _ => Zip(Side("head")));
        var read = Client(api).ResultAsync(Repo, new(12, "", Commit, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "completed", "success"), "abc", CancellationToken.None);
        if (wrongHash) await Assert.ThrowsAsync<InvalidDataException>(() => read);
        else {
            using var result = JsonDocument.Parse(await read);
            Assert.Equal(active, result.RootElement.GetProperty("side").GetString());
            Assert.Equal("ran", result.RootElement.GetProperty("execution").GetProperty("status").GetString());
        }
        string Side(string side) => RemoteIosNewTestsTests.WithEnvironment(JsonSerializer.Serialize(new {
            version = 1, nonce = "abc", @base = Base, head = Head, side,
            executionHash = wrongHash && side == "head" ? "different" : new string('a', 64),
            execution = new { status = side == active ? "ran" : "unused", commit = active == "base" ? Base : Head,
                root = side == active ? "/tmp/factory-ios-test/" + side : null, log = "", files = Array.Empty<object>() }
        }));
    }

    [Fact]
    public async Task Execution_dispatch_sends_the_exact_bounded_plan_and_no_owner_credentials()
    {
        var execution = "{\"side\":\"base\",\"prepare\":null}";
        var api = Api().On("POST " + Root + "/actions/workflows/factory-ios-new-tests.yml/dispatches", request => {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(execution, body.RootElement.GetProperty("inputs").GetProperty("execution").GetString());
            Assert.DoesNotContain("gate-token", request.Body!);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await Client(api).DispatchAsync(Repo, new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["OtherTests/newTest()"])
            { Execution = execution }, "abc", CancellationToken.None);
        await Assert.ThrowsAsync<IosWorkflowNotSentException>(() => Client(api).DispatchAsync(Repo,
            new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["OtherTests/newTest()"])
                { Execution = new string('x', RemoteIosNewTests.MaxExecutionBytes + 1) }, "abc", CancellationToken.None));
        // The descriptor fits its own bound, but the repeated ids and JSON escaping make the total payload too big.
        await Assert.ThrowsAsync<IosWorkflowNotSentException>(() => Client(api).DispatchAsync(Repo,
            new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, Enumerable.Repeat(new string('界', 200), 100).ToArray())
                { Execution = new string('x', 1000) }, "abc", CancellationToken.None));
        Assert.Single(api.Requests, r => r.PathAndQuery.EndsWith("/dispatches", StringComparison.Ordinal));
        Assert.Single(api.Requests, r => r.PathAndQuery == "/app/installations/5/access_tokens");
    }

    [Theory]
    [InlineData("base")]
    [InlineData("head")]
    public async Task Single_side_execution_needs_only_the_VM_that_was_requested(string side)
    {
        var api = SingleSide(side, true);
        var error = await Record.ExceptionAsync(() => Client(api).ResultAsync(Repo,
            new(12, "", Commit, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "completed", "success"), "abc", CancellationToken.None));
        Assert.Null(error);
    }

    [Fact]
    public async Task A_single_fixture_artifact_is_not_a_complete_two_side_test_result()
    {
        var error = await Record.ExceptionAsync(() => Client(SingleSide("base", false)).ResultAsync(Repo,
            new(12, "", Commit, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "completed", "success"), "abc", CancellationToken.None));
        Assert.IsType<InvalidDataException>(error);
    }

    private static FakeApi SingleSide(string side, bool execution) => Api()
        .On("GET " + Root + "/actions/runs/12/artifacts", HttpStatusCode.OK,
            $$$"""{"total_count":1,"artifacts":[{"id":8,"name":"factory-ios-result-abc-{{{side}}}","size_in_bytes":300,"expired":false,"workflow_run":{"id":12}}]}""")
        .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip(RemoteIosNewTestsTests.WithEnvironment(
            JsonSerializer.Serialize(new { version = 1, nonce = "abc", @base = Base, head = Head, side,
                execution = execution ? new { status = "ran", files = Array.Empty<object>() } : null }))));

    [Fact]
    public async Task Dispatch_transports_the_absolute_deadline_so_queueing_spends_the_budget()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double deadline = 0;
        var api = Api().On("POST " + Root + "/actions/workflows/factory-ios-new-tests.yml/dispatches", request => {
            var inputs = JsonDocument.Parse(request.Body!).RootElement.GetProperty("inputs");
            if (inputs.TryGetProperty("execution_deadline_unix_seconds", out var value))
                deadline = double.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await Client(api).DispatchAsync(Repo, new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["TallyTests/TallyTests/testNew"])
            { ExecutionBudgetSeconds = 90 }, new string('a', 32), CancellationToken.None);
        Assert.InRange(deadline, before + 90, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 90);
    }

    [Fact]
    public async Task A_POST_transport_failure_is_not_marked_definitely_unsent()
    {
        var api = Api().On("POST " + Root + "/actions/workflows/factory-ios-new-tests.yml/dispatches", _ => throw new HttpRequestException("lost answer"));
        var error = await Record.ExceptionAsync(() => Client(api).DispatchAsync(Repo,
            new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["TallyTests/TallyTests/testNew"]), new string('a', 32), CancellationToken.None));
        Assert.IsType<HttpRequestException>(error);
    }

    [Fact]
    public async Task Cancellation_after_token_mint_but_before_POST_is_definitely_unsent()
    {
        using var stop = new CancellationTokenSource();
        var api = new FakeApi()
            .On("GET " + Root + "/installation", HttpStatusCode.OK, """{"id":5}""")
            .On("POST /app/installations/5/access_tokens", _ => {
                stop.Cancel();
                return FakeApi.Json(HttpStatusCode.Created, $$$"""{"token":"gate-token","expires_at":"{{{Now.AddMinutes(30):O}}}"}""");
            });
        var error = await Record.ExceptionAsync(() => Client(api).DispatchAsync(Repo,
            new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["TallyTests/TallyTests/testNew"]), new string('a', 32), stop.Token));
        Assert.True(error is IosWorkflowNotSentException { Cancelled: true });
    }

    [Fact]
    public async Task Cancellation_targets_the_correlated_run_with_actions_write_only()
    {
        var api = Api().On("POST " + Root + "/actions/runs/42/cancel", HttpStatusCode.Accepted, "");
        await Client(api).CancelAsync(Repo, new(42, "factory-ios-" + new string('a', 32), Commit,
            "workflow_dispatch", RemoteIosWorkflow.Path, 1, "in_progress", null), CancellationToken.None);
        Assert.Equal("{\"actions\":\"write\"}", JsonDocument.Parse(api.Requests.Single(r => r.PathAndQuery.EndsWith("/access_tokens")).Body!)
            .RootElement.GetProperty("permissions").GetRawText());
    }

    [Fact]
    public async Task Dispatch_transports_a_positive_remaining_execution_budget()
    {
        double budget = 0;
        var api = Api().On("POST " + Root + "/actions/workflows/factory-ios-new-tests.yml/dispatches", request => {
            var inputs = JsonDocument.Parse(request.Body!).RootElement.GetProperty("inputs");
            if (inputs.TryGetProperty("execution_budget_seconds", out var value))
                budget = double.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await Client(api).DispatchAsync(Repo, new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["TallyTests/TallyTests/testNew"])
            { ExecutionBudgetSeconds = 90 }, new string('a', 32), CancellationToken.None);
        Assert.InRange(budget, 1, 90);
    }

    [Fact]
    public async Task Transport_failure_diagnostics_never_include_the_raw_response_body()
    {
        var api = Api().On("POST " + Root + "/actions/runs/42/cancel", HttpStatusCode.Forbidden, "SECRET arbitrary transport body");
        var error = await Record.ExceptionAsync(() => Client(api).CancelAsync(Repo,
            new(42, "factory-ios-" + new string('a', 32), Commit, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "in_progress", null), CancellationToken.None));
        Assert.Equal("remote-github-http: GitHub rejected the iOS workflow request (HTTP 403). Check the gate App installation and its repository Actions permissions; no usable executed evidence.", error?.Message);
    }

    private static FakeApi Api() => new FakeApi()
        .On("GET " + Root + "/installation", HttpStatusCode.OK, """{"id":5}""")
        .On("POST /app/installations/5/access_tokens", HttpStatusCode.Created, $$$"""{"token":"gate-token","expires_at":"{{{Now.AddMinutes(30):O}}}"}""");
    private static IosWorkflowGitHub Client(FakeApi api)
    {
        var http = api.Client("https://api.github.com/");
        return new(http, new GitHubApp(http, "123", Pem, new FixedTime(Now)));
    }
    private static HttpResponseMessage Zip(string json, string name = "result.json", int count = 1)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            for (var i = 0; i < count; i++)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(json);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
    }
}
