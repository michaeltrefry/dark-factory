using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public class GitHubAppTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly string Pem = Key.ExportRSAPrivateKeyPem(); // GitHub issues PKCS#1 PEMs
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");

    private static GitHubApp App(FakeApi api) =>
        new(api.Client("https://api.github.com/"), "123456", Pem, new FixedTime(Now));

    private static FakeApi GitHubWithInstallation() => new FakeApi()
        .On("GET /repos/michaeltrefry/dark-factory-sandbox/installation", HttpStatusCode.OK, """{"id":987}""")
        .On("POST /app/installations/987/access_tokens", HttpStatusCode.Created,
            """{"token":"ghs_test","expires_at":"2027-01-15T09:00:00Z","permissions":{"contents":"write","pull_requests":"write"}}""");

    [Fact]
    public void Jwt_is_rs256_signed_by_app_key_with_short_lifetime()
    {
        var jwt = App(new FakeApi()).CreateJwt();
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
        Assert.Equal("RS256", header.GetProperty("alg").GetString());

        var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
        Assert.Equal("123456", payload.GetProperty("iss").GetString());
        var iat = payload.GetProperty("iat").GetInt64();
        var exp = payload.GetProperty("exp").GetInt64();
        Assert.True(iat < Now.ToUnixTimeSeconds());
        Assert.True(exp > Now.ToUnixTimeSeconds());
        Assert.True(exp - iat <= 600, "GitHub rejects app JWTs that live longer than 10 minutes");

        var valid = Key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(valid);
    }

    [Fact]
    public async Task Installation_token_is_restricted_to_the_one_repo_with_contents_and_pr_write()
    {
        var api = GitHubWithInstallation();

        var token = await App(api).CreateInstallationTokenAsync(Sandbox, CancellationToken.None);

        Assert.Equal("ghs_test", token.Token);
        Assert.Equal(DateTimeOffset.Parse("2027-01-15T09:00:00Z"), token.ExpiresAt);
        Assert.All(api.Requests, r => Assert.StartsWith("Bearer ey", r.Headers["Authorization"]));
        Assert.All(api.Requests, r => Assert.Contains("dark-factory", r.Headers["User-Agent"]));

        var body = JsonDocument.Parse(api.Requests[1].Body!).RootElement;
        Assert.Equal(["dark-factory-sandbox"], body.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
        var permissions = body.GetProperty("permissions");
        Assert.Equal("write", permissions.GetProperty("contents").GetString());
        Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
        Assert.Equal(2, permissions.EnumerateObject().Count());
    }

    [Fact]
    public async Task Every_run_mints_a_fresh_token_never_reusing_an_earlier_one()
    {
        var minted = 0;
        var api = new FakeApi()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/installation", HttpStatusCode.OK, """{"id":987}""")
            .On("POST /app/installations/987/access_tokens", _ => FakeApi.Json(HttpStatusCode.Created,
                $$"""{"token":"ghs_run{{++minted}}","expires_at":"2027-01-15T09:00:00Z"}"""));
        var app = App(api);

        var first = await app.CreateInstallationTokenAsync(Sandbox, CancellationToken.None);
        var second = await app.CreateInstallationTokenAsync(Sandbox, CancellationToken.None);

        Assert.Equal(("ghs_run1", "ghs_run2"), (first.Token, second.Token));
        var mints = api.Requests.Where(r => r.PathAndQuery == "/app/installations/987/access_tokens").ToList();
        Assert.Equal(2, mints.Count);
        Assert.All(mints, m => Assert.Equal(["dark-factory-sandbox"],
            JsonDocument.Parse(m.Body!).RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString())));
    }

    [Fact]
    public async Task Token_for_another_repo_names_only_that_repo()
    {
        var api = new FakeApi()
            .On("GET /repos/acme/widgets/installation", HttpStatusCode.OK, """{"id":5}""")
            .On("POST /app/installations/5/access_tokens", HttpStatusCode.Created, """{"token":"ghs_w","expires_at":"2027-01-15T08:30:00Z"}""");

        await App(api).CreateInstallationTokenAsync(new RepoRef("acme", "widgets"), CancellationToken.None);

        var body = JsonDocument.Parse(api.Requests[1].Body!).RootElement;
        Assert.Equal(["widgets"], body.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Token_living_longer_than_an_hour_is_refused()
    {
        var api = new FakeApi()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/installation", HttpStatusCode.OK, """{"id":987}""")
            .On("POST /app/installations/987/access_tokens", HttpStatusCode.Created,
                """{"token":"ghs_long","expires_at":"2027-01-15T10:00:00Z"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => App(api).CreateInstallationTokenAsync(Sandbox, CancellationToken.None));
        Assert.Contains("60-minute limit", ex.Message);
        Assert.DoesNotContain("ghs_long", ex.Message);
    }

    [Theory]
    [InlineData("2027-01-15T09:04:00Z", true)]  // 1h + 4min: within the 5-minute clock-skew allowance
    [InlineData("2027-01-15T09:06:00Z", false)] // 1h + 6min: beyond it
    public async Task Token_lifetime_limit_is_one_hour_plus_five_minutes_of_skew(string expiresAt, bool accepted)
    {
        Assert.Equal(DateTimeOffset.Parse("2027-01-15T08:00:00Z"), Now);
        var api = new FakeApi()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/installation", HttpStatusCode.OK, """{"id":987}""")
            .On("POST /app/installations/987/access_tokens", HttpStatusCode.Created,
                $$"""{"token":"ghs_edge","expires_at":"{{expiresAt}}"}""");

        var mint = App(api).CreateInstallationTokenAsync(Sandbox, CancellationToken.None);

        if (accepted)
        {
            Assert.Equal(DateTimeOffset.Parse(expiresAt), (await mint).ExpiresAt);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => mint);
        }
    }

    [Fact]
    public async Task Missing_installation_says_to_install_the_app()
    {
        // Not a GitHubNotFoundException ("no such ref"): main-red must not read a repo the App cannot see as having no tip.
        var ex = await Assert.ThrowsAsync<GitHubAppNotInstalledException>(
            () => App(new FakeApi()).CreateInstallationTokenAsync(Sandbox, CancellationToken.None));
        Assert.Contains("not installed on michaeltrefry/dark-factory-sandbox", ex.Message);
    }

    [Fact]
    public async Task A_token_mint_404_is_an_app_that_cannot_see_the_repo()
    {
        var api = new FakeApi().On("GET /repos/michaeltrefry/dark-factory-sandbox/installation", HttpStatusCode.OK, """{"id":987}""");

        var ex = await Assert.ThrowsAsync<GitHubAppNotInstalledException>(
            () => App(api).CreateInstallationTokenAsync(Sandbox, CancellationToken.None));
        Assert.Contains("create installation token", ex.Message);
    }

    [Fact]
    public async Task A_compare_404_under_a_working_installation_is_not_found_and_a_missing_installation_is_not()
    {
        const string compare = "GET /repos/michaeltrefry/dark-factory-sandbox/compare/main...mergeAaaaaaaaaaa";
        var installed = GitHubWithInstallation().On(compare, HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var gate = new GitHubGate(installed.Client("https://api.github.com/"), App(installed));

        await Assert.ThrowsAsync<GitHubNotFoundException>(() => gate.CompareAsync(Sandbox, "main", "mergeAaaaaaaaaaa", CancellationToken.None));
        Assert.Contains(installed.Requests, r => r.PathAndQuery.StartsWith("/repos/michaeltrefry/dark-factory-sandbox/compare/", StringComparison.Ordinal));

        var uninstalled = new FakeApi().On(compare, HttpStatusCode.OK, """{"base_commit":{"sha":"tip"},"behind_by":0}""");
        var blind = new GitHubGate(uninstalled.Client("https://api.github.com/"), App(uninstalled));

        await Assert.ThrowsAsync<GitHubAppNotInstalledException>(() => blind.CompareAsync(Sandbox, "main", "mergeAaaaaaaaaaa", CancellationToken.None));
        Assert.DoesNotContain(uninstalled.Requests, r => r.PathAndQuery.Contains("/compare/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_request_is_opened_with_installation_token()
    {
        var api = GitHubWithInstallation()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.OK, "[]")
            .On("POST /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.Created,
            """{"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/1"}""");
        var prs = new GitHubPullRequests(api.Client("https://api.github.com/"), App(api));

        var url = await prs.OpenAsync(Sandbox, "factory/sc-7", "main", "sc-7: Fix", "body links story", CancellationToken.None);

        Assert.Equal("https://github.com/michaeltrefry/dark-factory-sandbox/pull/1", url);
        var create = api.Requests.Last();
        Assert.Equal("Bearer ghs_test", create.Headers["Authorization"]);
        var body = JsonDocument.Parse(create.Body!).RootElement;
        Assert.Equal("factory/sc-7", body.GetProperty("head").GetString());
        Assert.Equal("main", body.GetProperty("base").GetString());
        Assert.Equal("body links story", body.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Permanent_PR_creation_refusals_are_not_uncertain_gateway_writes(int status)
    {
        var api = GitHubWithInstallation()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.OK, "[]")
            .On("POST /repos/michaeltrefry/dark-factory-sandbox/pulls", (HttpStatusCode)status, "{}");
        var prs = new GitHubPullRequests(api.Client("https://api.github.com/"), App(api));
        var error = await Record.ExceptionAsync(() => prs.OpenAsync(Sandbox, "factory/rc-1", "main", "repair", "body", default));
        var refusal = Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.IsNotType<GitHubGatewayException>(error);
        Assert.Contains(status.ToString(), refusal.Message);
        Assert.Single(api.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery.EndsWith("/pulls"));
    }

    [Fact]
    public async Task Existing_open_pull_request_is_reused_on_rerun()
    {
        var api = GitHubWithInstallation()
            .On("POST /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.UnprocessableEntity,
                """{"message":"Validation Failed","errors":[{"message":"A pull request already exists"}]}""")
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.OK,
                """[{"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/3"}]""");
        var prs = new GitHubPullRequests(api.Client("https://api.github.com/"), App(api));

        var url = await prs.OpenAsync(Sandbox, "factory/sc-7", "main", "t", "b", CancellationToken.None);

        Assert.Equal("https://github.com/michaeltrefry/dark-factory-sandbox/pull/3", url);
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.EndsWith("/pulls") && r.Method == HttpMethod.Post);
        Assert.Contains("head=michaeltrefry%3Afactory%2Fsc-7", api.Requests.Last().PathAndQuery);
    }

    [Fact]
    public async Task Open_pull_requests_of_a_head_are_turned_back_into_drafts_through_graphql()
    {
        var api = GitHubWithInstallation()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.OK,
                """[{"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/3","node_id":"PR_3","draft":false},{"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/4","node_id":"PR_4","draft":true}]""")
            .On("POST /graphql", HttpStatusCode.OK, """{"data":{"convertPullRequestToDraft":{"pullRequest":{"isDraft":true}}}}""");
        var prs = new GitHubPullRequests(api.Client("https://api.github.com/"), App(api));

        var urls = await prs.ConvertOpenToDraftAsync(Sandbox, "factory/sc-7", CancellationToken.None);

        Assert.Equal(["https://github.com/michaeltrefry/dark-factory-sandbox/pull/3", "https://github.com/michaeltrefry/dark-factory-sandbox/pull/4"], urls);
        Assert.Contains("state=open&head=michaeltrefry%3Afactory%2Fsc-7", api.Requests.Single(r => r.Method == HttpMethod.Get && r.PathAndQuery.Contains("/pulls")).PathAndQuery);
        var mutation = Assert.Single(api.Requests, r => r.PathAndQuery == "/graphql"); // the draft one is left alone
        Assert.Equal("Bearer ghs_test", mutation.Headers["Authorization"]);
        var body = JsonDocument.Parse(mutation.Body!).RootElement;
        Assert.Contains("convertPullRequestToDraft", body.GetProperty("query").GetString());
        Assert.Equal("PR_3", body.GetProperty("variables").GetProperty("id").GetString());
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete || r.Method == HttpMethod.Patch || r.PathAndQuery.Contains("/merge"));
    }

    [Fact]
    public async Task A_graphql_error_turning_a_pull_request_into_a_draft_fails_the_call()
    {
        var api = GitHubWithInstallation()
            .On("GET /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.OK,
                """[{"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/3","node_id":"PR_3","draft":false}]""")
            .On("POST /graphql", HttpStatusCode.OK, """{"errors":[{"message":"Resource not accessible by integration"}]}""");
        var prs = new GitHubPullRequests(api.Client("https://api.github.com/"), App(api));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => prs.ConvertOpenToDraftAsync(Sandbox, "factory/sc-7", CancellationToken.None));
        Assert.Contains("Resource not accessible by integration", ex.Message);
    }

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

public class GitHubAppSetupTests
{
    [Fact]
    public void Manifest_requests_only_contents_pr_and_issues_write_and_metadata_read_without_webhook()
    {
        var manifest = JsonDocument.Parse(GitHubAppSetup.BuildManifest("dark-factory-test", 50123)).RootElement;

        Assert.Equal("dark-factory-test", manifest.GetProperty("name").GetString());
        Assert.Equal("http://localhost:50123/callback", manifest.GetProperty("redirect_url").GetString());
        Assert.False(manifest.GetProperty("public").GetBoolean());
        Assert.False(manifest.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
        Assert.Empty(manifest.GetProperty("default_events").EnumerateArray());
        var permissions = manifest.GetProperty("default_permissions").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(new Dictionary<string, string?>
        {
            ["contents"] = "write",
            ["pull_requests"] = "write",
            ["issues"] = "write", // sc-25385: the orchestrator reads, comments on and labels watched repos' issues
            ["metadata"] = "read",
        }, permissions);
    }

    [Fact]
    public void Start_page_posts_the_manifest_to_github_with_state()
    {
        var html = GitHubAppSetup.RenderStartPage("""{"name":"x"}""", "ABC123");
        Assert.Contains("action=\"https://github.com/settings/apps/new?state=ABC123\"", html);
        Assert.Contains("name=\"manifest\" value=\"{&quot;name&quot;:&quot;x&quot;}\"", html);
    }

    [Fact]
    public async Task Code_exchange_stores_app_id_and_private_key_in_secret_store()
    {
        var api = new FakeApi().On("POST /app-manifests/code-1/conversions", HttpStatusCode.Created,
            """{"id":4242,"slug":"dark-factory-test","html_url":"https://github.com/apps/dark-factory-test","pem":"-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----\n","webhook_secret":null,"client_id":"Iv1"}""");
        var secrets = new InMemorySecrets();
        var setup = new GitHubAppSetup(api.Client("https://api.github.com/"), secrets, TextWriter.Null);

        var app = await setup.ExchangeCodeAsync("code-1", CancellationToken.None);

        Assert.Equal(new CreatedApp(4242, "dark-factory-test", "https://github.com/apps/dark-factory-test"), app);
        Assert.Equal("4242", secrets.Values[SecretAccounts.GitHubAppId]);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", secrets.Values[SecretAccounts.GitHubAppPrivateKey]);
        Assert.False(api.Requests.Single().Headers.ContainsKey("Authorization"));
    }
}

public class ReservedControllerTests
{
    private static readonly RepoRef Controller = RepoRef.Parse(FactoryRepositoryPolicy.ReservedController);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2027-01-15T08:00:00Z");
    private static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    [Theory]
    [InlineData("default")]
    [InlineData("contents")]
    [InlineData("pull_requests")]
    [InlineData("issues")]
    [InlineData("actions-extra")]
    [InlineData("actions-contents-write")]
    [InlineData("actions-admin")]
    [InlineData("unknown")]
    [InlineData("empty")]
    public async Task Controller_worker_write_scopes_are_refused_before_jwt_http_or_token_mint(string scope)
    {
        Dictionary<string, string>? permissions = scope switch
        {
            "default" => null,
            "empty" => [],
            "actions-extra" => new() { ["actions"] = "write", ["pull_requests"] = "read" },
            "actions-contents-write" => new() { ["actions"] = "write", ["contents"] = "write" },
            "actions-admin" => new() { ["actions"] = "admin" },
            _ => new() { [scope] = "write" },
        };
        var api = new FakeApi();
        // An invalid PEM ensures that a guard moved after CreateJwt fails this assertion instead of silently minting.
        var app = new GitHubApp(api.Client("https://api.github.com/"), "123", "not-a-signing-key", new FixedTime(Now));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateInstallationTokenAsync(
            RepoRef.Parse(FactoryRepositoryPolicy.ReservedController.ToUpperInvariant()), CancellationToken.None, permissions));
        Assert.Contains("reserved", error.Message);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("actions-read")]
    [InlineData("actions-write")]
    [InlineData("actions-contents-read")]
    public async Task Controller_explicit_reads_and_narrow_trusted_dispatch_remain_single_repo_scoped(string scope)
    {
        var permissions = scope switch
        {
            "read" => new Dictionary<string, string> { ["contents"] = "read", ["pull_requests"] = "read" },
            "actions-read" => new() { ["actions"] = "read" },
            "actions-write" => new() { ["actions"] = "write" },
            _ => new() { ["actions"] = "write", ["contents"] = "read" },
        };
        var api = Installed(Controller);
        InstallationToken? token = null;
        var error = await Record.ExceptionAsync(async () => token = await new GitHubApp(api.Client("https://api.github.com/"), "123", Pem, new FixedTime(Now))
            .CreateInstallationTokenAsync(Controller, CancellationToken.None, permissions));
        Assert.Null(error);
        Assert.Equal("ghs_scoped", token!.Token);
        var body = JsonDocument.Parse(api.Requests[1].Body!).RootElement;
        Assert.Equal([Controller.Name], body.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(permissions.OrderBy(p => p.Key), body.GetProperty("permissions").EnumerateObject()
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)).OrderBy(p => p.Key));
        Assert.Equal(2, api.Requests.Count);
    }

    private static FakeApi Installed(RepoRef repo) => new FakeApi()
        .On($"GET /repos/{repo.FullName}/installation", HttpStatusCode.OK, "{\"id\":987}")
        .On("POST /app/installations/987/access_tokens", HttpStatusCode.Created, "{\"token\":\"ghs_scoped\",\"expires_at\":\"2027-01-15T09:00:00Z\"}");

    [Fact]
    public async Task Mutable_permission_input_cannot_widen_after_the_controller_scope_check()
    {
        var permissions = new Dictionary<string, string> { ["contents"] = "read" };
        var api = Installed(Controller).On($"GET /repos/{Controller.FullName}/installation", _ =>
        {
            permissions["contents"] = "write";
            return FakeApi.Json(HttpStatusCode.OK, "{\"id\":987}");
        });
        await new GitHubApp(api.Client("https://api.github.com/"), "123", Pem, new FixedTime(Now))
            .CreateInstallationTokenAsync(Controller, CancellationToken.None, permissions);
        var body = JsonDocument.Parse(api.Requests[1].Body!).RootElement;
        Assert.Equal("read", body.GetProperty("permissions").GetProperty("contents").GetString());
    }

    [Fact]
    public async Task Normal_dark_factory_repository_keeps_ordinary_worker_write_authority()
    {
        var normal = new RepoRef("michaeltrefry", "dark-factory");
        var api = Installed(normal);
        var error = await Record.ExceptionAsync(() => new GitHubApp(api.Client("https://api.github.com/"), "123", Pem, new FixedTime(Now))
            .CreateInstallationTokenAsync(normal, CancellationToken.None));
        Assert.Null(error);
        var permissions = JsonDocument.Parse(api.Requests[1].Body!).RootElement.GetProperty("permissions");
        Assert.Equal("write", permissions.GetProperty("contents").GetString());
        Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
        Assert.False(FactoryRepositoryPolicy.IsReservedController(normal));
        Assert.False(FactoryRepositoryPolicy.IsReservedController(new RepoRef("someone-else", Controller.Name)));
        Assert.Null(FactoryRepositoryPolicy.WorkRefusal(new RepoRef(Controller.Owner, Controller.Name + "-sandbox")));
    }

    [Fact]
    public async Task Direct_controller_merge_is_refused_before_signing_or_any_github_request()
    {
        var api = new FakeApi();
        var app = new GitHubApp(api.Client("https://api.github.com/"), "123", "not-a-signing-key", new FixedTime(Now));
        var gate = new GitHubGate(api.Client("https://api.github.com/"), app);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.MergeAsync(Controller, 1, "head", CancellationToken.None));
        Assert.Contains("reserved", error.Message);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("Factory:DefaultRepo", "Factory:DefaultRepo")]
    [InlineData("Factory:Repos", "Factory:Repos")]
    [InlineData("Factory:Repos:0", "Factory:Repos")]
    [InlineData("GitHub:Watch:Repos", "GitHub:Watch:Repos")]
    [InlineData("GitHub:Watch:Repos:0", "GitHub:Watch:Repos")]
    public void Controller_is_not_a_default_configured_or_watched_work_repository(string key, string namedKey)
    {
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [key] = FactoryRepositoryPolicy.ReservedController.ToUpperInvariant() }).Build(), new InMemorySecrets(), _ => null);
        var error = Assert.Throws<InvalidOperationException>(options.ValidateSettings);
        Assert.Contains(namedKey, error.Message);
        Assert.Contains("reserved", error.Message);
    }

    [Fact]
    public void Collector_setting_can_name_the_controller_without_enabling_factory_work_there()
    {
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Gate:Gui:CollectorRepo"] = FactoryRepositoryPolicy.ReservedController,
              ["Gate:Gui:TrustedRepos"] = "michaeltrefry/dark-factory-sandbox",
              ["Gate:Gui:ConfigurationSha256"] = new string('a', 64) }).Build(), new InMemorySecrets(), _ => null);
        Assert.Null(Record.Exception(options.ValidateSettings));
        Assert.Equal(["michaeltrefry/dark-factory-sandbox"], options.ConfiguredRepos);
        Assert.Equal(RemoteGuiSourceWorkflow.Repository, FactoryRepositoryPolicy.ReservedController);
        Assert.Equal(Controller, options.GuiWorkflow!.Collector);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_requested_controller_work_is_refused_before_claim_worker_push_review_or_ci(bool plan)
    {
        using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var description = plan ? $"Repo: {RunPipelineTests.Sandbox}\nRepo: {FactoryRepositoryPolicy.ReservedController.ToUpperInvariant()}" : $"Repo: {FactoryRepositoryPolicy.ReservedController.ToUpperInvariant()}";
        var story = new WorkStory(77, "Model-authored change", description, "bug", "https://example.test/item", ItemNaming.Shortcut)
            { Labels = plan ? ["plan"] : [] };
        var source = new FakeWorkSource(story) { PlansItems = true };
        var worker = new RunPipelineTests.FakeWorker();
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        var prs = new RunPipelineTests.FakePullRequests();
        var github = new GatePipelineTests.FakeGateGitHub();
        var reviewer = new GatePipelineTests.FakeReviewer();
        var pipeline = new RunPipeline(source, ledger, new InProcessRunLocks(), workspaces, worker, prs,
            RunPipelineTests.Sandbox, TextWriter.Null, gate: new GateStage(github, reviewer, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)),
            configuredRepos: [RunPipelineTests.Sandbox, Controller]);
        var outcome = await pipeline.RunAsync(77, CancellationToken.None);
        Assert.Contains("reserved", outcome.Error!);
        Assert.Empty(source.Writes);
        Assert.Empty(source.Comments);
        Assert.Empty(source.Children);
        Assert.Empty(worker.Calls);
        Assert.Empty(workspaces.Calls);
        Assert.Empty(prs.Opened);
        Assert.Empty(github.Calls);
        Assert.Empty(reviewer.Requests);
        Assert.Empty(await db.WorkItems.ToListAsync());
    }

    [Theory]
    [InlineData("own")]
    [InlineData("unpushed")]
    [InlineData("planned")]
    [InlineData("pass")]
    public async Task Recorded_controller_repository_changeset_or_delivery_pass_cannot_resume_into_authority(string recorded)
    {
        using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var story = new WorkStory(77, "Previously recorded plan", "Ordinary spec", "chore", "https://example.test/item", ItemNaming.Shortcut);
        var source = new FakeWorkSource(story) { PlansItems = true };
        var item = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, "sc-77", story.Name,
            recorded == "own" ? FactoryRepositoryPolicy.ReservedController : RunPipelineTests.Sandbox.FullName, null, CancellationToken.None);
        if (recorded != "own")
        {
            await ledger.RecordAsync(item, WorkState.Plan, null, "plan", CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, null, "sc-88", CancellationToken.None);
            var changeset = new Changeset(FactoryRepositoryPolicy.ReservedController, "factory/changeset-sc-77", "main", "base");
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Changeset, null, changeset.ToDetail(), CancellationToken.None);
            if (recorded != "unpushed")
            {
                await ledger.CheckpointAsync(item, RunPipeline.Steps.ChangesetPushed, null, Changeset.PushedDetail(changeset.Branch, "base", false), CancellationToken.None);
                await ledger.RecordAsync(item, WorkState.Planned, null, "waiting", CancellationToken.None);
            }
            if (recorded == "pass")
            {
                await ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, null, $"https://github.com/{Controller.FullName}/pull/1", CancellationToken.None);
                await ledger.RecordAsync(item, WorkState.Review, null, "delivery", CancellationToken.None);
            }
        }
        var before = await db.LedgerEntries.CountAsync();
        var worker = new RunPipelineTests.FakeWorker();
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        var github = new GatePipelineTests.FakeGateGitHub();
        var prs = new RunPipelineTests.FakePullRequests();
        var pipeline = new RunPipeline(source, ledger, new InProcessRunLocks(), workspaces, worker, prs, RunPipelineTests.Sandbox,
            TextWriter.Null, gate: new GateStage(github, new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)));
        var outcome = await pipeline.RunAsync(77, CancellationToken.None);
        Assert.Contains("reserved", outcome.Error!);
        Assert.Empty(source.Writes);
        Assert.Empty(source.Comments);
        Assert.Empty(worker.Calls);
        Assert.Empty(workspaces.Calls);
        Assert.Empty(github.Calls);
        Assert.Empty(prs.Opened);
        Assert.Equal(before, await db.LedgerEntries.CountAsync());
    }

    private static readonly RepoRef CustomController = new("private-org", "trusted-gui-control");
    private static Dictionary<string, string?> CustomSettings() => new()
    {
        ["Gate:Gui:CollectorRepo"] = CustomController.FullName,
        ["Gate:Gui:TrustedRepos"] = "private-org/application",
        ["Gate:Gui:ConfigurationSha256"] = new string('b', 64),
    };

    [Theory]
    [InlineData("default")]
    [InlineData("contents")]
    [InlineData("pull_requests")]
    [InlineData("actions-extra")]
    public async Task Configured_private_controller_is_write_protected_before_jwt_http_and_mint(string scope)
    {
        Dictionary<string, string>? permissions = scope switch
        {
            "default" => null,
            "actions-extra" => new() { ["actions"] = "write", ["issues"] = "write" },
            _ => new() { [scope] = "write" },
        };
        var api = new FakeApi();
        var app = new GitHubApp(api.Client("https://api.github.com/"), "123", "invalid-key", new FixedTime(Now), CustomController);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateInstallationTokenAsync(
            RepoRef.Parse(CustomController.FullName.ToUpperInvariant()), CancellationToken.None, permissions));
        Assert.Contains(CustomController.FullName, error.Message);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("dispatch")]
    public async Task Configured_private_controller_keeps_explicit_read_and_trusted_dispatch_scoped(string purpose)
    {
        var permissions = new Dictionary<string, string> { [purpose == "read" ? "contents" : "actions"] = purpose == "read" ? "read" : "write" };
        var api = Installed(CustomController);
        var app = new GitHubApp(api.Client("https://api.github.com/"), "123", Pem, new FixedTime(Now), CustomController);
        Assert.Null(await Record.ExceptionAsync(() => app.CreateInstallationTokenAsync(CustomController, CancellationToken.None, permissions)));
        var body = JsonDocument.Parse(api.Requests[1].Body!).RootElement;
        Assert.Equal([CustomController.Name], body.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(permissions.Single().Value, body.GetProperty("permissions").GetProperty(permissions.Single().Key).GetString());
    }

    [Fact]
    public async Task Controller_policy_is_per_instance_and_does_not_leak_between_differently_configured_factories()
    {
        var other = new RepoRef("other-private-org", "other-control");
        var api = Installed(CustomController);
        var blocked = new GitHubApp(api.Client("https://api.github.com/"), "123", "invalid-key", new FixedTime(Now), CustomController);
        var independent = new GitHubApp(api.Client("https://api.github.com/"), "123", Pem, new FixedTime(Now), other);
        Assert.Null(await Record.ExceptionAsync(() => independent.CreateInstallationTokenAsync(CustomController, CancellationToken.None)));
        var before = api.Requests.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.CreateInstallationTokenAsync(CustomController, CancellationToken.None));
        Assert.Contains(CustomController.FullName, error.Message);
        Assert.Equal(before, api.Requests.Count);
        Assert.True(FactoryRepositoryPolicy.IsReservedController(other, other));
        Assert.False(FactoryRepositoryPolicy.IsReservedController(CustomController, other));
    }

    [Theory]
    [InlineData("Factory:DefaultRepo")]
    [InlineData("Factory:Repos")]
    [InlineData("GitHub:Watch:Repos")]
    public void Configured_private_controller_is_not_an_ordinary_configured_work_repository(string key)
    {
        var settings = CustomSettings();
        settings[key] = CustomController.FullName.ToUpperInvariant();
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new InMemorySecrets(), _ => null);
        var error = Assert.Throws<InvalidOperationException>(options.ValidateSettings);
        Assert.Contains(key, error.Message);
        Assert.Contains(CustomController.FullName, error.Message);
    }

    [Fact]
    public void Explicit_private_controller_configuration_does_not_block_a_normal_repository_of_the_same_owner()
    {
        var settings = CustomSettings();
        settings["Factory:DefaultRepo"] = "private-org/application";
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new InMemorySecrets(), _ => null);
        Assert.Null(Record.Exception(options.ValidateSettings));
        Assert.Equal(["private-org/application"], options.ConfiguredRepos);
        Assert.Equal(CustomController, options.GuiWorkflow!.Collector);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_private_controller_cannot_be_selected_by_model_spec_or_plan_before_claim(bool plan)
    {
        using var db = TestDb.Create();
        var description = (plan ? $"Repo: {RunPipelineTests.Sandbox}\n" : "") + $"Repo: {CustomController.FullName.ToUpperInvariant()}";
        var source = new FakeWorkSource(new WorkStory(77, "Model request", description, "chore", "https://example.test/item", ItemNaming.Shortcut)
            { Labels = plan ? ["plan"] : [] }) { PlansItems = true };
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        var worker = new RunPipelineTests.FakeWorker();
        var github = new GatePipelineTests.FakeGateGitHub();
        var pipeline = new RunPipeline(source, new WorkLedger(db, TimeProvider.System), new InProcessRunLocks(), workspaces, worker,
            new RunPipelineTests.FakePullRequests(), RunPipelineTests.Sandbox, TextWriter.Null,
            gate: new GateStage(github, new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)),
            configuredRepos: [RunPipelineTests.Sandbox, CustomController], reservedController: CustomController);
        var result = await pipeline.RunAsync(77, CancellationToken.None);
        Assert.Contains(CustomController.FullName, result.Error!);
        Assert.Empty(source.Writes);
        Assert.Empty(source.Children);
        Assert.Empty(worker.Calls);
        Assert.Empty(workspaces.Calls);
        Assert.Empty(github.Calls);
        Assert.Empty(await db.WorkItems.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_private_controller_recorded_work_is_refused_by_run_and_explicit_recovery(bool recover)
    {
        using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var source = new FakeWorkSource(new WorkStory(77, "Old work", "Ordinary spec", "chore", "https://example.test/item", ItemNaming.Shortcut));
        var item = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, "sc-77", "Old work", CustomController.FullName, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Escalated, null, "old failure", CancellationToken.None);
        var count = await db.LedgerEntries.CountAsync();
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        var worker = new RunPipelineTests.FakeWorker();
        var github = new GatePipelineTests.FakeGateGitHub();
        var pipeline = new RunPipeline(source, ledger, new InProcessRunLocks(), workspaces, worker, new RunPipelineTests.FakePullRequests(),
            RunPipelineTests.Sandbox, TextWriter.Null, reservedController: CustomController,
            gate: new GateStage(github, new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)));
        var outcome = recover ? await pipeline.RecoverAsync(77, new string('c', 40), "owner", CancellationToken.None)
            : await pipeline.RunAsync(77, CancellationToken.None);
        Assert.Contains(CustomController.FullName, outcome.Error!);
        Assert.Empty(source.Writes);
        Assert.Empty(worker.Calls);
        Assert.Empty(workspaces.Calls);
        Assert.Empty(github.Calls);
        Assert.Equal(count, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Explicit_recovery_cannot_adopt_a_spec_redirected_into_the_configured_controller()
    {
        using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var source = new FakeWorkSource(new WorkStory(77, "Edited spec", $"Repo: {CustomController}", "chore", "https://example.test/item", ItemNaming.Shortcut));
        var item = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, "sc-77", "Old work", RunPipelineTests.Sandbox.FullName, null, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Claimed, null, "claimed earlier", CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Escalated, null, "old failure", CancellationToken.None);
        var count = await db.LedgerEntries.CountAsync();
        var github = new GatePipelineTests.FakeGateGitHub();
        var pipeline = new RunPipeline(source, ledger, new InProcessRunLocks(), new RunPipelineTests.FakeWorkspaces(), new RunPipelineTests.FakeWorker(),
            new RunPipelineTests.FakePullRequests(), RunPipelineTests.Sandbox, TextWriter.Null, reservedController: CustomController,
            gate: new GateStage(github, new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)));
        var outcome = await pipeline.RecoverAsync(77, new string('c', 40), "owner", CancellationToken.None);
        Assert.Contains(CustomController.FullName, outcome.Error!);
        Assert.Empty(source.Writes);
        Assert.Empty(github.Calls);
        Assert.Equal(count, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Default_controller_write_authority_stays_reserved_when_a_new_collector_is_configured()
    {
        var api = new FakeApi();
        var app = new GitHubApp(api.Client("https://api.github.com/"), "123", "invalid-key", new FixedTime(Now), CustomController);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateInstallationTokenAsync(
            RepoRef.Parse(Controller.FullName.ToUpperInvariant()), CancellationToken.None));
        Assert.Contains(Controller.FullName, error.Message);
        Assert.Empty(api.Requests);
        Assert.True(FactoryRepositoryPolicy.IsReservedController(Controller, CustomController));
        Assert.True(FactoryRepositoryPolicy.IsReservedController(CustomController, CustomController));
    }

    [Fact]
    public void Default_controller_is_not_a_work_repository_during_custom_collector_settings_migration()
    {
        var settings = CustomSettings();
        settings["Factory:DefaultRepo"] = Controller.FullName.ToUpperInvariant();
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new InMemorySecrets(), _ => null);
        var error = Assert.Throws<InvalidOperationException>(options.ValidateSettings);
        Assert.Contains("Factory:DefaultRepo", error.Message);
        Assert.Contains(Controller.FullName, error.Message);
    }

    [Fact]
    public async Task Model_spec_cannot_target_the_old_default_controller_after_configuring_a_new_collector()
    {
        using var db = TestDb.Create();
        var source = new FakeWorkSource(new WorkStory(77, "Old controller request", $"Repo: {Controller}", "chore", "https://example.test/item", ItemNaming.Shortcut));
        var worker = new RunPipelineTests.FakeWorker();
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        var github = new GatePipelineTests.FakeGateGitHub();
        var pipeline = new RunPipeline(source, new WorkLedger(db, TimeProvider.System), new InProcessRunLocks(), workspaces, worker,
            new RunPipelineTests.FakePullRequests(), RunPipelineTests.Sandbox, TextWriter.Null, reservedController: CustomController,
            gate: new GateStage(github, new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)));
        var outcome = await pipeline.RunAsync(77, CancellationToken.None);
        Assert.Contains(Controller.FullName, outcome.Error!);
        Assert.Empty(source.Writes);
        Assert.Empty(worker.Calls);
        Assert.Empty(workspaces.Calls);
        Assert.Empty(github.Calls);
        Assert.Empty(await db.WorkItems.ToListAsync());
    }

    [Fact]
    public async Task Actual_gui_transport_can_read_and_dispatch_the_same_controller_that_denies_worker_write_authority()
    {
        const string branch = "reviewed-control";
        var controllerCommit = new string('d', 40);
        var root = $"/repos/{CustomController.FullName}";
        var api = new FakeApi()
            .On($"GET {root}/installation", HttpStatusCode.OK, "{\"id\":987}")
            .On("POST /app/installations/987/access_tokens", r =>
            {
                var permissions = JsonDocument.Parse(r.Body!).RootElement.GetProperty("permissions");
                var token = permissions.GetProperty("actions").GetString() == "write" ? "ghs_dispatch" : "ghs_read";
                return FakeApi.Json(HttpStatusCode.Created, $"{{\"token\":\"{token}\",\"expires_at\":\"2027-01-15T09:00:00Z\"}}");
            })
            .On($"GET {root}", HttpStatusCode.OK, $"{{\"default_branch\":\"{branch}\"}}")
            .On($"GET {root}/commits/{branch}", HttpStatusCode.OK, $"{{\"sha\":\"{controllerCommit}\"}}")
            .On($"GET {root}/contents/{RemoteGuiWorkflow.Path}", _ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(RemoteGuiWorkflow.Text, Encoding.UTF8, "text/plain") })
            .On($"POST {root}/actions/workflows/{Path.GetFileName(RemoteGuiWorkflow.Path)}/dispatches", HttpStatusCode.NoContent, "");
        using var http = api.Client("https://api.github.com/");
        var workerApp = new GitHubApp(http, "worker", "invalid-key", new FixedTime(Now), CustomController);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => workerApp.CreateInstallationTokenAsync(CustomController, CancellationToken.None));
        Assert.Contains(CustomController.FullName, refusal.Message);
        Assert.Empty(api.Requests);

        var gateApp = new GitHubApp(http, "gate", Pem, new FixedTime(Now), CustomController);
        var transport = new DarkFactory.Orchestrator.Gateway.IosWorkflowGitHub(http, gateApp, gui: true);
        IosSealedWorkflow? workflow = null;
        Assert.Null(await Record.ExceptionAsync(async () => workflow = await transport.ReadDefaultWorkflowAsync(CustomController, CancellationToken.None)));
        Assert.Equal(new IosSealedWorkflow(branch, controllerCommit, RemoteGuiWorkflow.Text), workflow);
        var source = new RepoRef("michaeltrefry", "dark-factory");
        var request = new IosNewTestsRequest(new string('a', 40), new string('b', 40), ["ProbeTests/Probe/testNew()"])
            { SourceRepository = source.FullName };
        var nonce = new string('e', 32);
        Assert.Null(await Record.ExceptionAsync(() => transport.DispatchAsync(CustomController, workflow!, request, nonce, CancellationToken.None)));

        var mints = api.Requests.Where(r => r.PathAndQuery == "/app/installations/987/access_tokens").ToList();
        Assert.Equal(2, mints.Count);
        foreach (var mint in mints)
            Assert.Equal([CustomController.Name], JsonDocument.Parse(mint.Body!).RootElement.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()));
        var read = JsonDocument.Parse(mints[0].Body!).RootElement.GetProperty("permissions");
        Assert.Equal(2, read.EnumerateObject().Count());
        Assert.Equal("read", read.GetProperty("contents").GetString());
        Assert.Equal("read", read.GetProperty("actions").GetString());
        var dispatch = JsonDocument.Parse(mints[1].Body!).RootElement.GetProperty("permissions");
        Assert.Single(dispatch.EnumerateObject());
        Assert.Equal("write", dispatch.GetProperty("actions").GetString());
        var workflowRead = Assert.Single(api.Requests, r => r.PathAndQuery.Contains("/contents/", StringComparison.Ordinal));
        Assert.EndsWith("?ref=" + controllerCommit, workflowRead.PathAndQuery);
        Assert.Equal("Bearer ghs_read", workflowRead.Headers["Authorization"]);
        var posted = Assert.Single(api.Requests, r => r.PathAndQuery.EndsWith("/dispatches", StringComparison.Ordinal));
        Assert.Equal("Bearer ghs_dispatch", posted.Headers["Authorization"]);
        var payload = JsonDocument.Parse(posted.Body!).RootElement;
        Assert.Equal(branch, payload.GetProperty("ref").GetString());
        var inputs = payload.GetProperty("inputs");
        Assert.Equal(source.FullName, inputs.GetProperty("source_repository").GetString());
        Assert.Equal(request.Base, inputs.GetProperty("base").GetString());
        Assert.Equal(request.Head, inputs.GetProperty("head").GetString());
        Assert.Equal(nonce, inputs.GetProperty("nonce").GetString());
        Assert.Equal(request.Tests, JsonSerializer.Deserialize<string[]>(inputs.GetProperty("tests").GetString()!));

        // Normal source work keeps its worker token scope even in the same configured-controller deployment.
        var normalApi = Installed(source);
        var normalWorker = new GitHubApp(normalApi.Client("https://api.github.com/"), "worker", Pem, new FixedTime(Now), CustomController);
        Assert.Null(await Record.ExceptionAsync(() => normalWorker.CreateInstallationTokenAsync(source, CancellationToken.None)));
        var normalPermissions = JsonDocument.Parse(normalApi.Requests[1].Body!).RootElement.GetProperty("permissions");
        Assert.Equal("write", normalPermissions.GetProperty("contents").GetString());
        Assert.Equal("write", normalPermissions.GetProperty("pull_requests").GetString());
    }

    [Fact]
    public void Every_production_app_and_pipeline_construction_carries_its_configured_controller_identity()
    {
        var root = Path.Combine(SandboxSupport.RepoRoot, "src", "DarkFactory.Orchestrator");
        foreach (var name in new[] { "FactoryRunner.cs", "WorkSources/WorkSourceRegistry.cs", "IosNewTestsCli.cs", "GuiCollectorCli.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, name));
            var calls = text.Split('\n').Where(line => line.Contains("new GitHubApp(", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(calls);
            Assert.All(calls, line => Assert.Contains("reservedController:", line));
            if (name != "GuiCollectorCli.cs") Assert.All(calls, line => Assert.Contains("options.GuiWorkflow?.Collector", line));
        }
        var factory = File.ReadAllText(Path.Combine(root, "FactoryRunner.cs"));
        var start = factory.IndexOf("var pipeline = new RunPipeline(", StringComparison.Ordinal);
        Assert.Contains("reservedController: options.GuiWorkflow?.Collector", factory[start..factory.IndexOf("return recoveryHead", start, StringComparison.Ordinal)]);
    }

}
