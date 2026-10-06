using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;
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
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => App(new FakeApi()).CreateInstallationTokenAsync(Sandbox, CancellationToken.None));
        Assert.Contains("not installed on michaeltrefry/dark-factory-sandbox", ex.Message);
    }

    [Fact]
    public async Task Pull_request_is_opened_with_installation_token()
    {
        var api = GitHubWithInstallation().On("POST /repos/michaeltrefry/dark-factory-sandbox/pulls", HttpStatusCode.Created,
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
        Assert.Contains("head=michaeltrefry%3Afactory%2Fsc-7", api.Requests.Last().PathAndQuery);
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
    public void Manifest_requests_only_contents_pr_write_and_metadata_read_without_webhook()
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
