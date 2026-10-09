using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // Every parameter GitHub's pull_request rule takes, so no GitHub default decides one.
        Assert.Equal(
            ["allowed_merge_methods", "dismiss_stale_reviews_on_push", "require_code_owner_review",
             "require_extra_approval_for_unattributed_changes", "require_last_push_approval", "required_approving_review_count",
             "required_review_thread_resolution", "required_reviewers"],
            pr.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(new[] { "dismiss_stale_reviews_on_push", "require_code_owner_review", "require_extra_approval_for_unattributed_changes",
            "require_last_push_approval", "required_review_thread_resolution" }, name => Assert.False(pr.GetProperty(name).GetBoolean()));
        Assert.Empty(pr.GetProperty("required_reviewers").EnumerateArray());
        Assert.Equal(["merge", "squash", "rebase"], Strings(pr.GetProperty("allowed_merge_methods")));
        Assert.False(rules[1].TryGetProperty("parameters", out _));
    }

    [Fact]
    public void Main_ruleset_body_sends_no_extra_approval_for_unattributed_commits()
    {
        var body = RepoProtection.Serialize(RepoProtection.DesiredRulesets().Single(r => r.Name == RepoProtection.MainRulesetName));

        // Left out, GitHub stored true: unsigned (worker) commits then need an approval nobody can give.
        Assert.Contains("\"require_extra_approval_for_unattributed_changes\":false", body);
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
    public void The_gate_app_may_bypass_the_factory_branch_rule_only_by_merging_a_pull_request()
    {
        var rulesets = RepoProtection.DesiredRulesets(gateAppId: 4242);
        var branches = JsonDocument.Parse(RepoProtection.Serialize(rulesets.Single(r => r.Name == RepoProtection.BranchesRulesetName))).RootElement;

        var actors = branches.GetProperty("bypass_actors").EnumerateArray()
            .Select(a => (a.GetProperty("actor_id").GetInt64(), a.GetProperty("actor_type").GetString(), a.GetProperty("bypass_mode").GetString()));
        Assert.Equal([((long)RepoProtection.RepositoryAdminRoleId, "RepositoryRole", "always"), (4242L, "Integration", "pull_request")], actors);
        // Nothing else changes: main still needs a PR with no bypass, tags stay admin-only.
        Assert.Empty(rulesets.Single(r => r.Name == RepoProtection.MainRulesetName).BypassActors);
        Assert.Single(rulesets.Single(r => r.Name == RepoProtection.TagsRulesetName).BypassActors);
    }

    [Fact]
    public async Task Apply_creates_missing_rulesets_and_overwrites_existing_ones_by_name()
    {
        var github = new GitHubRulesets((77, Named(RepoProtection.MainRulesetName)), (78, Named("someone else's")));

        await Protection(github.Api).ApplyAsync(Sandbox, CancellationToken.None);

        var api = github.Api;
        Assert.Equal(["GET", "PUT", "GET", "POST", "GET", "POST", "GET"], api.Requests.Select(r => r.Method.Method));
        Assert.Equal($"{Rulesets}/77", api.Requests[1].PathAndQuery);
        Assert.Equal($"{Rulesets}/77", api.Requests[2].PathAndQuery); // each write is read back
        Assert.Equal($"{Rulesets}/100", api.Requests[4].PathAndQuery);
        Assert.Equal([RepoProtection.MainRulesetName, RepoProtection.BranchesRulesetName, RepoProtection.TagsRulesetName],
            api.Requests.Where(r => r.Body is not null).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("name").GetString()));
        Assert.All(api.Requests, r => Assert.Equal("Bearer gho_admin", r.Headers["Authorization"]));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.EndsWith("/78"));
    }

    [Fact]
    public async Task Rerun_replaces_an_existing_main_ruleset_that_demands_approval_for_unattributed_changes()
    {
        // What the sandbox held (rulesets/24601473) after a protect that did not send the parameter.
        var held = Named(RepoProtection.MainRulesetName);
        held["rules"] = JsonNode.Parse("""
            [{"type":"pull_request","parameters":{"required_approving_review_count":0,"dismiss_stale_reviews_on_push":false,
              "required_reviewers":[],"require_code_owner_review":false,"require_last_push_approval":false,
              "required_review_thread_resolution":false,"require_extra_approval_for_unattributed_changes":true,
              "allowed_merge_methods":["merge","squash","rebase"]}},{"type":"non_fast_forward"},{"type":"deletion"}]
            """);
        var github = new GitHubRulesets((24601473, held));

        await Protection(github.Api).ApplyAsync(Sandbox, CancellationToken.None);
        await Protection(github.Api).ApplyAsync(Sandbox, CancellationToken.None); // and again: nothing new is created

        Assert.False(github.Stored[24601473]["rules"]![0]!["parameters"]!["require_extra_approval_for_unattributed_changes"]!.GetValue<bool>());
        Assert.Equal([100L, 101, 24601473], github.Stored.Keys.Order());
        Assert.Equal(2, github.Api.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Protect_fails_when_github_holds_an_unattributed_changes_approval_after_the_write()
    {
        var github = new GitHubRulesets
        {
            Store = ruleset => ForcePullRequestParameter(ruleset, "require_extra_approval_for_unattributed_changes", true),
        };
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await RepoProtection.RunAsync(github.Api.Client("https://api.github.com/"), "gho_admin", Sandbox, output, error, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(RepoProtection.MainRulesetName, error.ToString());
        Assert.Contains("pull_request.require_extra_approval_for_unattributed_changes: sent false, GitHub holds true", error.ToString());
        Assert.DoesNotContain("verified", output.ToString());
    }

    [Fact]
    public async Task Protect_fails_on_a_pull_request_parameter_github_defaults_that_was_not_sent()
    {
        var github = new GitHubRulesets
        {
            Store = ruleset => ForcePullRequestParameter(ruleset, "some_new_github_parameter", true),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Protection(github.Api).ApplyAsync(Sandbox, CancellationToken.None));

        Assert.Contains("pull_request.some_new_github_parameter: sent (absent), GitHub holds true", ex.Message);
    }

    [Fact]
    public async Task Protect_verifies_every_ruleset_github_holds_as_sent()
    {
        var github = new GitHubRulesets();
        var output = new StringWriter();

        var exit = await RepoProtection.RunAsync(github.Api.Client("https://api.github.com/"), "gho_admin", Sandbox, output, TextWriter.Null,
            CancellationToken.None, gateAppId: 4242);

        Assert.Equal(0, exit);
        Assert.Equal(3, output.ToString().Split('\n').Count(l => l.StartsWith("verified ruleset")));
    }

    [Theory]
    [InlineData("enforcement")]
    [InlineData("bypass_actors")]
    [InlineData("conditions")]
    [InlineData("rules")]
    public void Differences_name_any_field_github_holds_otherwise(string field)
    {
        var sent = RepoProtection.DesiredRulesets(gateAppId: 4242).Single(r => r.Name == RepoProtection.BranchesRulesetName);
        var stored = JsonNode.Parse(RepoProtection.Serialize(sent))!.AsObject();
        Assert.Empty(RepoProtection.Differences(sent, JsonDocument.Parse(stored.ToJsonString()).RootElement));

        stored[field] = field == "enforcement" ? "evaluate" : new JsonArray();

        Assert.NotEmpty(RepoProtection.Differences(sent, JsonDocument.Parse(stored.ToJsonString()).RootElement));
    }

    private static JsonObject Named(string name) => new() { ["name"] = name };

    private static void ForcePullRequestParameter(JsonObject ruleset, string name, bool value)
    {
        foreach (var rule in ruleset["rules"]!.AsArray())
        {
            if (rule!["type"]!.GetValue<string>() == "pull_request")
            {
                rule["parameters"]![name] = value;
            }
        }
    }

    /// <summary>
    /// GitHub's rulesets API in memory: writes are stored (through <see cref="Store"/>, which models what GitHub adds or
    /// changes; by default a left-out <c>require_extra_approval_for_unattributed_changes</c> is stored as true, as GitHub
    /// did), creates get ids from 100, and reads return the stored ruleset with GitHub's extra fields.
    /// </summary>
    private sealed class GitHubRulesets
    {
        private long _next = 100;

        public Dictionary<long, JsonObject> Stored { get; } = new();
        public FakeApi Api { get; } = new();
        public Action<JsonObject> Store { get; init; } = ruleset =>
        {
            foreach (var rule in ruleset["rules"]!.AsArray())
            {
                if (rule!["type"]!.GetValue<string>() == "pull_request" && rule["parameters"] is JsonObject p
                    && !p.ContainsKey("require_extra_approval_for_unattributed_changes"))
                {
                    p["require_extra_approval_for_unattributed_changes"] = true;
                }
            }
        };

        public GitHubRulesets(params (long Id, JsonObject Ruleset)[] existing)
        {
            Api.On($"GET {Rulesets}", _ => FakeApi.Json(HttpStatusCode.OK, new JsonArray(Stored
                .Select(s => (JsonNode)new JsonObject { ["id"] = s.Key, ["name"] = s.Value["name"]!.GetValue<string>() })
                .ToArray()).ToJsonString()));
            Api.On($"POST {Rulesets}", request =>
            {
                var id = _next++;
                Route(id);
                return Write(id, request, HttpStatusCode.Created);
            });
            foreach (var (id, ruleset) in existing)
            {
                Stored[id] = ruleset;
                Route(id);
            }
        }

        private void Route(long id)
        {
            Api.On($"PUT {Rulesets}/{id}", request => Write(id, request, HttpStatusCode.OK));
            Api.On($"GET {Rulesets}/{id}", _ => FakeApi.Json(HttpStatusCode.OK, Read(id)));
        }

        private HttpResponseMessage Write(long id, RecordedRequest request, HttpStatusCode status)
        {
            var ruleset = JsonNode.Parse(request.Body!)!.AsObject();
            Store(ruleset);
            Stored[id] = ruleset;
            return FakeApi.Json(status, Read(id));
        }

        private string Read(long id)
        {
            var ruleset = Stored[id].DeepClone().AsObject();
            ruleset["id"] = id;
            ruleset["source_type"] = "Repository";
            ruleset["current_user_can_bypass"] = "never";
            return ruleset.ToJsonString();
        }
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
        var github = new GitHubRulesets((77, Named(RepoProtection.MainRulesetName)), (79, Named(RepoProtection.MainRulesetName)),
            (81, Named(RepoProtection.MainRulesetName)), (83, Named("someone else's")), (84, Named("someone else's")));
        var log = new StringWriter();

        await new RepoProtection(github.Api.Client("https://api.github.com/"), "gho_admin", log).ApplyAsync(Sandbox, CancellationToken.None);

        var api = github.Api;
        Assert.Equal(["GET", "PUT", "GET", "POST", "GET", "POST", "GET"], api.Requests.Select(r => r.Method.Method));
        Assert.Equal($"{Rulesets}/77", api.Requests[1].PathAndQuery);
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.EndsWith("/79") || r.PathAndQuery.EndsWith("/81"));
        // The duplicates are left alone but named, since they still apply; an unmanaged name's duplicates are not ours to report.
        var warning = Assert.Single(log.ToString().Split(Environment.NewLine), l => l.StartsWith("warning:", StringComparison.Ordinal));
        Assert.Equal($"warning: {Sandbox} also has ruleset id(s) 79, 81 named '{RepoProtection.MainRulesetName}', left unchanged and "
            + "unverified; they still apply. Delete them in the repository's rulesets settings if stale.", warning);
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
