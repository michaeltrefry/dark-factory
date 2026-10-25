using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorHostedTests
{
    const string Sha = "1111111111111111111111111111111111111111";
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    [Fact]
    public async Task Dedicated_fixture_repository_is_created_public_with_no_projects_and_both_actual_App_installations_verified()
    {
        var fixture = new Fixture { Missing = true };
        var result = await fixture.Gateway.PrepareHostedRepositoryAsync(fixture.Gateway.FixtureRepository, fixture.Worker, CancellationToken.None);
        Assert.Equal("michaeltrefry/dark-factory-s6-fixtures", result.Repository);
        Assert.Equal("main", result.Branch);
        Assert.Equal(Sha, result.Commit);
        Assert.Equal(RemoteIosWorkflow.Path, result.Before.Path);
        Assert.Equal("", result.Before.Sha256);
        var create = Assert.Single(fixture.Api.Calls.Where(c => c.Method == "POST"));
        var body = JsonNode.Parse(create.Body!)!;
        Assert.Equal("dark-factory-s6-fixtures", body["name"]!.GetValue<string>());
        Assert.False(body["private"]!.GetValue<bool>());
        Assert.True(body["auto_init"]!.GetValue<bool>());
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Path.Contains("/git/refs") || c.Path.Contains("/pulls") || c.Path.Contains("/access_tokens"));
        Assert.Equal(2, fixture.Api.Calls.Count(c => c.Path.EndsWith("/installation")));
    }

    [Theory]
    [InlineData("Sources/App/main.swift", false, false)]
    [InlineData("README.md", true, false)]
    [InlineData("README.md", false, true)]
    [InlineData(".github/link", false, false)]
    public async Task Operational_private_truncated_or_linked_fixture_defaults_are_never_overwritten(string file, bool privateRepo, bool truncated)
    {
        var fixture = new Fixture { Entry = file, Private = privateRepo, Truncated = truncated, Link = file == ".github/link" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Gateway.PrepareHostedRepositoryAsync(fixture.Gateway.FixtureRepository, fixture.Worker, CancellationToken.None));
        Assert.All(fixture.Api.Calls, c => Assert.Equal("GET", c.Method));
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Path.EndsWith("/installation"));
    }

    [Fact]
    public async Task Missing_nonfixture_trusted_repository_is_not_created_as_an_operational_source()
    {
        var fixture = new Fixture { Missing = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Gateway.PrepareHostedRepositoryAsync(new("michaeltrefry", "operational"), fixture.Worker, CancellationToken.None));
        Assert.All(fixture.Api.Calls, c => Assert.Equal("GET", c.Method));
    }

    [Fact]
    public void Default_trust_follows_configured_collector_owner_and_does_not_reuse_operational_sandbox()
    {
        var config = GuiCollectorHost.DefaultConfiguration("other-owner");
        var trusted = config["trustedRepositories"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();
        Assert.Equal(["michaeltrefry/LensWorks", "other-owner/dark-factory-s6-fixtures"], trusted);
        Assert.DoesNotContain("michaeltrefry/dark-factory-sandbox", trusted);
        var fixture = new Fixture(new("other-owner", "private-gui"));
        Assert.Equal("other-owner/dark-factory-s6-fixtures", fixture.Gateway.FixtureRepository.FullName);
    }

    sealed class Fixture
    {
        public bool Missing, Private, Truncated, Link;
        public string Entry = "README.md";
        public readonly Api Api;
        public readonly GuiCollectorDeployment Gateway;
        public readonly GitHubApp Worker;
        public Fixture(RepoRef? collector = null)
        {
            Api = new Api(Answer);
            var http = new HttpClient(Api) { BaseAddress = new Uri("https://api.github.com/") };
            Worker = new(http, "1", Pem, TimeProvider.System);
            Gateway = new(http, new GitHubApp(http, "2", Pem, TimeProvider.System), "owner-only", TimeProvider.System, controlRepository: collector);
        }
        HttpResponseMessage Answer(HttpRequestMessage request, string path, string? body)
        {
            var repo = Gateway.FixtureRepository;
            var prefix = "/repos/" + repo.FullName;
            if (path == "/user") return Json(new { login = repo.Owner, type = "User" });
            if (path == "/user/repos") { Missing = false; return Metadata(); }
            if (path == prefix) return Missing ? new(HttpStatusCode.NotFound) : Metadata();
            if (path.StartsWith("/repos/michaeltrefry/operational", StringComparison.Ordinal)) return new(HttpStatusCode.NotFound);
            if (path == prefix + "/commits/main") return Json(new { sha = Sha });
            if (path.Contains("/git/trees/")) return Json(new { truncated = Truncated, tree = new[] { new { path = Entry, type = "blob", mode = Link ? "120000" : "100644" } } });
            if (path == "/app" || path.EndsWith("/installation")) return Json(new { id = 5, permissions = new { contents = "write", pull_requests = "write", actions = "write" } });
            if (path.Contains("/contents/" + RemoteIosWorkflow.Path)) return new(HttpStatusCode.NotFound);
            throw new InvalidOperationException("Unexpected hosted fake route " + path);
        }
        HttpResponseMessage Metadata() => Json(new { id = 88, full_name = Gateway.FixtureRepository.FullName, @private = Private, fork = false,
            default_branch = "main", permissions = new { admin = true } });
    }
    static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    sealed class Api(Func<HttpRequestMessage, string, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public sealed record Call(string Method, string Path, string? Body);
        public readonly List<Call> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return answer(request, request.RequestUri!.PathAndQuery, body);
        }
    }
}
