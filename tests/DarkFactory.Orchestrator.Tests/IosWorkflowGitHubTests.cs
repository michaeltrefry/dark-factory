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
            .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip($$$"""{"version":1,"nonce":"{{{nonce}}}","base":"{{{Base}}}","head":"{{{Head}}}","side":"base","tests":[{"test":"TallyTests/TallyTests/testNew","base":"failed"}]}"""))
            .On("GET " + Root + "/actions/artifacts/9/zip", _ => Zip($$$"""{"version":1,"nonce":"{{{nonce}}}","base":"{{{Base}}}","head":"{{{(wrongSideCommit ? Base : Head)}}}","side":"head","tests":[{"test":"TallyTests/TallyTests/testNew","head":"passed"}]}"""));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(api).DispatchAsync(Repo,
            new("main", Commit, RemoteIosWorkflow.Text), new(Base, Head, ["TallyTests/TallyTests/testNew"]), new string('a', 32), CancellationToken.None));
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
