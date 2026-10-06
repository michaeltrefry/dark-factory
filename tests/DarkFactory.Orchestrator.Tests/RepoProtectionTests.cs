using System.Net;
using System.Text.Json;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public class RepoProtectionTests
{
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private const string Rulesets = "/repos/michaeltrefry/dark-factory-sandbox/rulesets";

    private static JsonElement Payload(string name) => JsonDocument.Parse(RepoProtection.Serialize(
        RepoProtection.DesiredRulesets().Single(r => r.Name == name))).RootElement;

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static RepoProtection Protection(FakeApi api) =>
        new(api.Client("https://api.github.com/"), "gho_admin", TextWriter.Null);

    [Fact]
    public void Main_requires_a_pull_request_and_blocks_force_push_and_deletion_for_everyone()
    {
        var main = Payload(RepoProtection.MainRulesetName);

        Assert.Equal("branch", main.GetProperty("target").GetString());
        Assert.Equal("active", main.GetProperty("enforcement").GetString());
        Assert.Equal(["~DEFAULT_BRANCH"], Strings(main.GetProperty("conditions").GetProperty("ref_name").GetProperty("include")));
        Assert.Empty(main.GetProperty("bypass_actors").EnumerateArray());
        var rules = main.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(["pull_request", "non_fast_forward", "deletion"], rules.Select(r => r.GetProperty("type").GetString()));
        var pr = rules[0].GetProperty("parameters");
        Assert.Equal(0, pr.GetProperty("required_approving_review_count").GetInt32());
        Assert.Equal(5, pr.EnumerateObject().Count()); // every field GitHub requires on a pull_request rule
        Assert.False(rules[1].TryGetProperty("parameters", out _));
    }

    [Theory]
    [InlineData(RepoProtection.BranchesRulesetName, "branch", "refs/heads/factory/**")]
    [InlineData(RepoProtection.TagsRulesetName, "tag", null)]
    public void Only_repository_admins_write_refs_outside_factory_branches(string name, string target, string? excluded)
    {
        var ruleset = Payload(name);

        Assert.Equal(target, ruleset.GetProperty("target").GetString());
        Assert.Equal("active", ruleset.GetProperty("enforcement").GetString());
        var refName = ruleset.GetProperty("conditions").GetProperty("ref_name");
        Assert.Equal(["~ALL"], Strings(refName.GetProperty("include")));
        Assert.Equal(excluded is null ? [] : [excluded], Strings(refName.GetProperty("exclude")));
        Assert.Equal(["creation", "update", "deletion"],
            ruleset.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("type").GetString()));
        var bypass = ruleset.GetProperty("bypass_actors").EnumerateArray().Single();
        Assert.Equal(RepoProtection.RepositoryAdminRoleId, bypass.GetProperty("actor_id").GetInt32());
        Assert.Equal("RepositoryRole", bypass.GetProperty("actor_type").GetString());
        Assert.Equal("always", bypass.GetProperty("bypass_mode").GetString());
    }

    [Fact]
    public async Task Apply_creates_missing_rulesets_and_overwrites_existing_ones_by_name()
    {
        var api = new FakeApi()
            .On($"GET {Rulesets}", HttpStatusCode.OK,
                $$"""[{"id":77,"name":"{{RepoProtection.MainRulesetName}}"},{"id":78,"name":"someone else's"}]""")
            .On($"PUT {Rulesets}/77", HttpStatusCode.OK, "{}")
            .On($"POST {Rulesets}", HttpStatusCode.Created, "{}");

        await Protection(api).ApplyAsync(Sandbox, CancellationToken.None);

        Assert.Equal(["GET", "PUT", "POST", "POST"], api.Requests.Select(r => r.Method.Method));
        Assert.Equal($"{Rulesets}/77", api.Requests[1].PathAndQuery);
        Assert.Equal([RepoProtection.MainRulesetName, RepoProtection.BranchesRulesetName, RepoProtection.TagsRulesetName],
            api.Requests.Skip(1).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("name").GetString()));
        Assert.All(api.Requests, r => Assert.Equal("Bearer gho_admin", r.Headers["Authorization"]));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.EndsWith("/78"));
    }

    [Fact]
    public async Task Plan_refusal_explains_the_limit_without_leaking_the_token()
    {
        var api = new FakeApi().On($"GET {Rulesets}", HttpStatusCode.Forbidden,
            """{"message":"Upgrade to GitHub Pro or make this repository public to enable this feature."}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Protection(api).ApplyAsync(Sandbox, CancellationToken.None));

        Assert.Contains("Upgrade to GitHub Pro", ex.Message);
        Assert.Contains("michaeltrefry/dark-factory-sandbox", ex.Message);
        Assert.DoesNotContain("gho_admin", ex.Message);
    }

    [Fact]
    public async Task Duplicate_ruleset_names_overwrite_the_first_instead_of_crashing()
    {
        var api = new FakeApi()
            .On($"GET {Rulesets}", HttpStatusCode.OK,
                $$"""[{"id":77,"name":"{{RepoProtection.MainRulesetName}}"},{"id":79,"name":"{{RepoProtection.MainRulesetName}}"}]""")
            .On($"PUT {Rulesets}/77", HttpStatusCode.OK, "{}")
            .On($"POST {Rulesets}", HttpStatusCode.Created, "{}");

        await Protection(api).ApplyAsync(Sandbox, CancellationToken.None);

        Assert.Equal(["GET", "PUT", "POST", "POST"], api.Requests.Select(r => r.Method.Method));
        Assert.Equal($"{Rulesets}/77", api.Requests[1].PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(null)] // network failure: HttpRequestException
    public async Task Command_exits_1_with_the_reason_and_never_the_token(HttpStatusCode? status)
    {
        var api = new FakeApi().On($"GET {Rulesets}", _ => status is { } code
            ? FakeApi.Json(code, """{"message":"Upgrade to GitHub Pro"}""")
            : throw new HttpRequestException("Connection refused (api.github.com:443)"));
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await RepoProtection.RunAsync(api.Client("https://api.github.com/"), "gho_admin", Sandbox, output, error, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(status is null ? "Connection refused" : "Upgrade to GitHub Pro", error.ToString());
        Assert.DoesNotContain("gho_admin", output.ToString() + error.ToString());
    }

    [Theory]
    [InlineData("gh_env", "gh_actions", "gh_cli", "gh_env")]
    [InlineData(null, "gh_actions", "gh_cli", "gh_actions")]
    [InlineData("", "gh_actions", "gh_cli", "gh_actions")]
    [InlineData("  ", "", "gh_cli", "gh_cli")]
    [InlineData("", " ", null, null)]
    public void Admin_token_falls_back_past_empty_values(string? ghToken, string? githubToken, string? ghCli, string? expected)
    {
        var env = new Dictionary<string, string?> { ["GH_TOKEN"] = ghToken, ["GITHUB_TOKEN"] = githubToken };

        Assert.Equal(expected, RepoProtection.ResolveAdminToken(env.GetValueOrDefault, () => ghCli));
    }
}
