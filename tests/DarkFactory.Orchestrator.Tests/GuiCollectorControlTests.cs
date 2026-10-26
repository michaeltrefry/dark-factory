using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>In-process deployment contracts only: no gh, dotnet, host probes, root setup or live HTTP.</summary>
public sealed class GuiCollectorControlTests
{
    const string Control = "michaeltrefry/dark-factory-gui-control";
    const string Source = "michaeltrefry/LensWorks";
    const string OwnerToken = "OWNER_PAT_MUST_STAY_LOCAL";
    const string AppId = "12";
    const string Branch = "release/default";
    const string EnvironmentPath = "/repos/" + Source + "/environments/factory-gui-dispatch";
    static readonly string Hash = new('a', 64);
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
    static readonly string Pem = MakePem();
    static string MakePem() { using var rsa = RSA.Create(2048); return rsa.ExportRSAPrivateKeyPem(); }
    static HttpResponseMessage Answer(JsonNode value) => FakeApi.Json(HttpStatusCode.OK, value.ToJsonString());
    static JsonObject Repository() => new() {
        ["id"] = 456, ["full_name"] = Control, ["private"] = true, ["fork"] = false,
        ["owner"] = new JsonObject { ["login"] = "michaeltrefry", ["type"] = "User" },
        ["permissions"] = new JsonObject { ["admin"] = true }
    };
    static JsonObject Restricted() => new() {
        ["deployment_branch_policy"] = new JsonObject { ["protected_branches"] = false, ["custom_branch_policies"] = true }
    };
    static JsonObject Policy(long id, string name, string type = "branch") => new() { ["id"] = id, ["name"] = name, ["type"] = type };
    static JsonObject Policies(params JsonObject[] policies) => new() {
        ["total_count"] = policies.Length, ["branch_policies"] = new JsonArray(policies.Select(p => (JsonNode)p).ToArray())
    };
    static GuiCollectorDeployment Client(HttpClient http) => new(http,
        new GitHubApp(http, AppId, Pem, new FixedTime(Now)), OwnerToken, new FixedTime(Now));
    static FakeApi Bootstrap(JsonObject? repository = null, JsonArray? collaborators = null) => new FakeApi()
        .On("GET /user", HttpStatusCode.OK, "{\"login\":\"michaeltrefry\"}")
        .On("GET /repos/" + Control, _ => Answer(repository ?? Repository()))
        .On("GET /repos/" + Control + "/collaborators", _ => Answer(collaborators ?? new JsonArray()))
        .On("PUT /repos/" + Control + "/actions/permissions", HttpStatusCode.NoContent, "")
        .On("PUT /repos/" + Control + "/actions/permissions/selected-actions", HttpStatusCode.NoContent, "")
        .On("PUT /repos/" + Control + "/actions/permissions/workflow", HttpStatusCode.NoContent, "")
        .On("GET /repos/" + Control + "/installation", HttpStatusCode.OK,
            "{\"id\":7,\"permissions\":{\"actions\":\"write\"},\"repository_selection\":\"all\"}");

    [Fact]
    public async Task Explicit_bootstrap_creates_only_the_private_control_repository_and_reads_it_back()
    {
        var api = Bootstrap();
        var reads = 0;
        api.On("GET /repos/" + Control, _ => ++reads == 1
            ? FakeApi.Json(HttpStatusCode.NotFound, "{}") : Answer(Repository()));
        api.On("POST /user/repos", request => {
            var body = JsonNode.Parse(request.Body!)!;
            Assert.Equal("dark-factory-gui-control", body["name"]!.GetValue<string>());
            Assert.True(body["private"]!.GetValue<bool>());
            Assert.True(body["auto_init"]!.GetValue<bool>());
            Assert.False(body["allow_forking"]!.GetValue<bool>());
            return Answer(Repository());
        });
        using var http = api.Client("https://api.github.com/");
        await Client(http).ProvisionControlRepositoryAsync(CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.Single(api.Requests, r => r.Method == HttpMethod.Post);
        Assert.All(api.Requests, r => Assert.DoesNotContain(Source, r.PathAndQuery));
        AssertOwnerAndAppCredentials(api);
        var workflow = JsonNode.Parse(api.Requests.Single(r => r.PathAndQuery.EndsWith("/permissions/workflow")).Body!)!;
        Assert.Equal("read", workflow["default_workflow_permissions"]!.GetValue<string>());
        Assert.False(workflow["can_approve_pull_request_reviews"]!.GetValue<bool>());
        // All-repository installation selection is allowed here. FactoryRepositoryPolicy independently
        // prevents worker Apps from getting source-write credentials for this control repository.
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("access_tokens"));
    }

    [Fact]
    public async Task Explicit_configured_private_control_uses_its_own_full_name_and_owner_not_the_default_account()
    {
        var configured = new RepoRef("another-owner", "private-gui-control");
        var path = "/repos/" + configured.FullName;
        var repository = Repository();
        repository["full_name"] = configured.FullName;
        repository["owner"]!["login"] = configured.Owner;
        var api = new FakeApi()
            .On("GET /user", HttpStatusCode.OK, "{\"login\":\"another-owner\"}")
            .On("GET " + path, _ => Answer(repository))
            .On("GET " + path + "/collaborators", _ => Answer(new JsonArray()))
            .On("PUT " + path + "/actions/permissions", HttpStatusCode.NoContent, "")
            .On("PUT " + path + "/actions/permissions/selected-actions", HttpStatusCode.NoContent, "")
            .On("PUT " + path + "/actions/permissions/workflow", HttpStatusCode.NoContent, "")
            .On("GET " + path + "/installation", HttpStatusCode.OK, "{\"permissions\":{\"actions\":\"write\"}}");
        using var http = api.Client("https://api.github.com/");
        var client = new GuiCollectorDeployment(http, new GitHubApp(http, AppId, Pem, new FixedTime(Now)), OwnerToken, new FixedTime(Now), configured);
        await client.ProvisionControlRepositoryAsync(CancellationToken.None);
        Assert.All(api.Requests.Where(r => r.PathAndQuery.StartsWith("/repos/")), r => Assert.StartsWith(path, r.PathAndQuery));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("michaeltrefry"));
    }

    [Fact]
    public async Task Private_creation_refusal_has_no_public_fallback_and_does_not_retain_response_body()
    {
        var api = Bootstrap().On("GET /repos/" + Control, HttpStatusCode.NotFound, "{}")
            .On("POST /user/repos", HttpStatusCode.Forbidden, "PRIVATE_RESPONSE_OWNER_PAT_MUST_STAY_LOCAL");
        using var http = api.Client("https://api.github.com/");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).ProvisionControlRepositoryAsync(CancellationToken.None));
        Assert.DoesNotContain(OwnerToken, error.Message);
        Assert.DoesNotContain("PRIVATE_RESPONSE", error.Message);
        Assert.Single(api.Requests, r => r.Method != HttpMethod.Get);
        Assert.True(JsonNode.Parse(api.Requests.Last().Body!)!["private"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("private")]
    [InlineData("fork")]
    [InlineData("full_name")]
    [InlineData("owner_login")]
    [InlineData("owner_type")]
    [InlineData("admin")]
    public async Task Existing_control_must_be_the_exact_private_nonfork_owner_admin_repository(string mismatch)
    {
        var repository = Repository();
        switch (mismatch)
        {
            case "private": repository["private"] = false; break;
            case "fork": repository["fork"] = true; break;
            case "full_name": repository["full_name"] = Source; break;
            case "owner_login": repository["owner"]!["login"] = "another-owner"; break;
            case "owner_type": repository["owner"]!["type"] = "Organization"; break;
            case "admin": repository["permissions"]!["admin"] = false; break;
        }
        var api = Bootstrap(repository);
        using var http = api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).ProvisionControlRepositoryAsync(CancellationToken.None));
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData("push")]
    [InlineData("maintain")]
    [InlineData("admin")]
    public async Task Any_unexpected_writer_refuses_before_actions_or_app_provisioning(string permission)
    {
        var api = Bootstrap(collaborators: new JsonArray(new JsonObject {
            ["login"] = "unexpected-writer", ["permissions"] = new JsonObject { [permission] = true }
        }));
        using var http = api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).ProvisionControlRepositoryAsync(CancellationToken.None));
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.EndsWith("/installation"));
    }

    [Fact]
    public async Task Owner_write_and_other_read_only_collaborators_are_not_unexpected_writers()
    {
        var api = Bootstrap(collaborators: new JsonArray(
            new JsonObject { ["login"] = "michaeltrefry", ["permissions"] = new JsonObject { ["admin"] = true, ["push"] = true } },
            new JsonObject { ["login"] = "reader", ["permissions"] = new JsonObject { ["pull"] = true, ["push"] = false } }));
        using var http = api.Client("https://api.github.com/");
        await Client(http).ProvisionControlRepositoryAsync(CancellationToken.None);
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_full_collaborator_page_is_not_proof_of_owner_only_access()
    {
        var rows = new JsonArray(Enumerable.Range(0, 100).Select(i => (JsonNode)new JsonObject {
            ["login"] = "reader-" + i, ["permissions"] = new JsonObject { ["pull"] = true }
        }).ToArray());
        var api = Bootstrap(collaborators: rows);
        using var http = api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).ProvisionControlRepositoryAsync(CancellationToken.None));
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Another_authenticated_identity_cannot_even_create_the_control_repository()
    {
        var api = Bootstrap().On("GET /user", HttpStatusCode.OK, "{\"login\":\"other\"}");
        using var http = api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).ProvisionControlRepositoryAsync(CancellationToken.None));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task App_repository_selection_adds_only_the_exact_private_repository_and_requires_readback()
    {
        var api = Bootstrap();
        var installationReads = 0;
        api.On("GET /repos/" + Control + "/installation", _ => ++installationReads == 1
            ? FakeApi.Json(HttpStatusCode.NotFound, "{}")
            : FakeApi.Json(HttpStatusCode.OK, "{\"permissions\":{\"actions\":\"write\"}}"));
        api.On("GET /user/installations", HttpStatusCode.OK,
            "{\"total_count\":1,\"installations\":[{\"id\":7,\"app_id\":12,\"account\":{\"login\":\"michaeltrefry\"}}]}");
        api.On("PUT /user/installations/7/repositories/456", HttpStatusCode.NoContent, "");
        using var http = api.Client("https://api.github.com/");
        await Client(http).ProvisionControlRepositoryAsync(CancellationToken.None);
        Assert.Equal(2, installationReads);
        Assert.Single(api.Requests, r => r.PathAndQuery == "/user/installations/7/repositories/456");
        AssertOwnerAndAppCredentials(api);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("none")]
    public async Task Installation_without_actions_write_is_an_actual_approval_prerequisite(string permission)
    {
        var api = Bootstrap().On("GET /repos/" + Control + "/installation", HttpStatusCode.OK,
            new JsonObject { ["permissions"] = new JsonObject { ["actions"] = permission } }.ToJsonString());
        using var http = api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).ProvisionControlRepositoryAsync(CancellationToken.None));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("registration-token"));
    }

    sealed class EnvironmentApi
    {
        public readonly FakeApi Api = new();
        public readonly List<string> Events = [];
        public readonly List<(bool Secret, string Name, string Value)> Values = [];
        public JsonObject Before = Policies(Policy(11, "*"), Policy(22, Branch, "tag"), Policy(33, "release/*"));
        public JsonObject After = Policies(Policy(44, Branch));
        public JsonObject Restriction = Restricted();
        int policyReads;
        public EnvironmentApi()
        {
            Api.On("PUT " + EnvironmentPath, _ => { Events.Add("restrict"); return Answer(Restricted()); });
            Api.On("GET " + EnvironmentPath, _ => { Events.Add("read-restriction"); return Answer(Restriction); });
            Api.On("GET " + EnvironmentPath + "/deployment-branch-policies", _ => {
                Events.Add("read-policies"); return Answer(++policyReads == 1 ? Before : After);
            });
            foreach (var id in new[] { 11, 22, 33 })
                Api.On("DELETE " + EnvironmentPath + "/deployment-branch-policies/" + id,
                    _ => { Events.Add("delete-" + id); return new HttpResponseMessage(HttpStatusCode.NoContent); });
            Api.On("POST " + EnvironmentPath + "/deployment-branch-policies", r => {
                Events.Add("create-exact");
                Assert.Equal(Branch, JsonNode.Parse(r.Body!)!["name"]!.GetValue<string>());
                Assert.Equal("branch", JsonNode.Parse(r.Body!)!["type"]!.GetValue<string>());
                return Answer(Policy(44, Branch));
            });
            Api.On("GET " + EnvironmentPath + "/secrets/FACTORY_GUI_APP_PRIVATE_KEY", _ => {
                Assert.Contains(Values, v => v.Secret && v.Value == Pem);
                return Answer(new JsonObject { ["name"] = "FACTORY_GUI_APP_PRIVATE_KEY" });
            });
            Api.On("GET " + EnvironmentPath + "/variables/FACTORY_GUI_APP_ID", _ =>
                Answer(new JsonObject { ["value"] = AppId }));
        }
        public Task Write(bool secret, string name, string value, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested);
            // These checks occur at the seam, before either public id or signing material is written.
            Assert.Contains("read-restriction", Events);
            Assert.True(Events.Count(e => e == "read-policies") >= 2);
            Events.Add("write-" + name);
            Values.Add((secret, name, value));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Exact_default_branch_policy_is_verified_before_publishing_only_the_gate_key()
    {
        var fake = new EnvironmentApi();
        using var http = fake.Api.Client("https://api.github.com/");
        await Client(http).ProvisionDispatcherEnvironmentAsync(Branch, Pem, CancellationToken.None, fake.Write);
        Assert.Equal(new[] { "restrict", "read-policies", "delete-11", "delete-22", "delete-33", "create-exact",
            "read-restriction", "read-policies", "write-FACTORY_GUI_APP_ID", "write-FACTORY_GUI_APP_PRIVATE_KEY",
            "read-restriction", "read-policies" }, fake.Events);
        Assert.Equal(new[] { (false, "FACTORY_GUI_APP_ID", AppId), (true, "FACTORY_GUI_APP_PRIVATE_KEY", Pem) }, fake.Values);
        Assert.DoesNotContain(fake.Values, v => v.Value == OwnerToken);
        Assert.All(fake.Api.Requests, r => {
            Assert.DoesNotContain(Pem, r.Body ?? "");
            Assert.DoesNotContain(OwnerToken, r.Body ?? "");
            Assert.Equal("Bearer " + OwnerToken, r.Headers["Authorization"]);
        });
    }

    [Fact]
    public async Task Existing_exact_policy_is_preserved_by_id_while_wildcard_and_tag_policies_are_removed()
    {
        var fake = new EnvironmentApi { Before = Policies(Policy(44, Branch), Policy(11, "*"), Policy(22, Branch, "tag")) };
        using var http = fake.Api.Client("https://api.github.com/");
        await Client(http).ProvisionDispatcherEnvironmentAsync(Branch, Pem, CancellationToken.None, fake.Write);
        Assert.DoesNotContain(fake.Api.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(new[] { EnvironmentPath + "/deployment-branch-policies/11", EnvironmentPath + "/deployment-branch-policies/22" },
            fake.Api.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.PathAndQuery));
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("custom")]
    [InlineData("wildcard")]
    [InlineData("tag")]
    [InlineData("extra")]
    [InlineData("count")]
    public async Task Restriction_readback_mismatch_publishes_neither_id_nor_secret(string mismatch)
    {
        var fake = new EnvironmentApi();
        switch (mismatch)
        {
            case "protected": fake.Restriction["deployment_branch_policy"]!["protected_branches"] = true; break;
            case "custom": fake.Restriction["deployment_branch_policy"]!["custom_branch_policies"] = false; break;
            case "wildcard": fake.After = Policies(Policy(44, "*")); break;
            case "tag": fake.After = Policies(Policy(44, Branch, "tag")); break;
            case "extra": fake.After = Policies(Policy(44, Branch), Policy(45, "*")); break;
            case "count": fake.After["total_count"] = 2; break;
        }
        using var http = fake.Api.Client("https://api.github.com/");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(http).ProvisionDispatcherEnvironmentAsync(Branch, Pem, CancellationToken.None, fake.Write));
        Assert.Contains("no App key was published", error.Message);
        Assert.Empty(fake.Values);
    }

    [Fact]
    public async Task Incomplete_initial_policy_inventory_is_not_mutated_and_no_key_is_published()
    {
        var fake = new EnvironmentApi();
        fake.Before["total_count"] = 101;
        using var http = fake.Api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(http).ProvisionDispatcherEnvironmentAsync(Branch, Pem, CancellationToken.None, fake.Write));
        Assert.Empty(fake.Values);
        Assert.DoesNotContain(fake.Api.Requests, r => r.Method == HttpMethod.Delete || r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(OwnerToken)]
    public async Task Missing_key_or_owner_pat_is_never_published_as_app_signing_material(string key)
    {
        var fake = new EnvironmentApi();
        using var http = fake.Api.Client("https://api.github.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(http).ProvisionDispatcherEnvironmentAsync(Branch, key, CancellationToken.None, fake.Write));
        Assert.Empty(fake.Api.Requests);
        Assert.Empty(fake.Values);
    }

    [Theory]
    [InlineData(true, "FACTORY_GUI_APP_PRIVATE_KEY")]
    [InlineData(false, "FACTORY_GUI_APP_ID")]
    public void Gh_environment_builder_keeps_the_owner_pat_out_of_argv_and_accepts_no_secret_value(bool secret, string name)
    {
        var start = GitHubCli.GuiEnvironmentStartInfo(OwnerToken, secret, name);
        Assert.Equal("gh", start.FileName);
        Assert.Equal(new[] { secret ? "secret" : "variable", "set", name, "--repo", Source, "--env", "factory-gui-dispatch" }, start.ArgumentList);
        Assert.DoesNotContain(OwnerToken, string.Join(' ', start.ArgumentList));
        Assert.DoesNotContain(Pem, string.Join(' ', start.ArgumentList));
        Assert.Equal(OwnerToken, start.Environment["GH_TOKEN"]);
        Assert.Equal(new[] { "GH_HOST", "GH_PROMPT_DISABLED", "GH_TOKEN", "PATH" }, start.Environment.Keys.Order());
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
        Assert.Equal("/", start.WorkingDirectory);
    }

    [Fact]
    public async Task Persist_uses_normal_user_secrets_set_for_exactly_three_public_values_preserving_unrelated_keys()
    {
        var store = new Dictionary<string, string> { ["Router:Key"] = "UNCHANGED_ROUTER", ["GitHub:PrivateKeyPem"] = "UNCHANGED_APP" };
        var calls = new List<ProcessStartInfo>();
        await GuiCollectorSettings.PersistAsync(Control, [Source], Hash, CancellationToken.None, (start, _) => {
            calls.Add(start);
            Assert.Equal("dotnet", start.FileName);
            Assert.Equal(new[] { "user-secrets", "set" }, start.ArgumentList.Take(2));
            Assert.Equal(new[] { "--id", "dark-factory-orchestrator" }, start.ArgumentList.Skip(4));
            Assert.Equal(6, start.ArgumentList.Count);
            Assert.False(start.UseShellExecute);
            Assert.Equal("/", start.WorkingDirectory);
            Assert.Equal(new[] { "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "HOME", "PATH" }, start.Environment.Keys.Order());
            Assert.DoesNotContain(OwnerToken, string.Join(' ', start.ArgumentList));
            Assert.DoesNotContain(Pem, string.Join(' ', start.ArgumentList));
            store[start.ArgumentList[2]] = start.ArgumentList[3];
            return Task.CompletedTask;
        }, new Hashtable());
        Assert.Equal(3, calls.Count);
        Assert.Equal(Control, store["Gate:Gui:CollectorRepo"]);
        Assert.Equal(Source, store["Gate:Gui:TrustedRepos"]);
        Assert.Equal(Hash, store["Gate:Gui:ConfigurationSha256"]);
        Assert.Equal("UNCHANGED_ROUTER", store["Router:Key"]);
        Assert.Equal("UNCHANGED_APP", store["GitHub:PrivateKeyPem"]);
        Assert.Equal(5, store.Count);
    }

    [Theory]
    [InlineData("Gate__Gui__CollectorRepo")]
    [InlineData("gate:gui:trustedrepos")]
    [InlineData("GATE__GUI__CONFIGURATIONSHA256")]
    public async Task Conflicting_environment_override_refuses_before_any_user_secrets_process(string key)
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorSettings.PersistAsync(
            Control, [Source], Hash, CancellationToken.None, (_, _) => { calls++; return Task.CompletedTask; },
            new Hashtable { [key] = "DIFFERENT" }));
        Assert.Contains("environment override", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Matching_overrides_and_unrelated_environment_are_accepted_but_not_forwarded_to_dotnet()
    {
        var calls = 0;
        await GuiCollectorSettings.PersistAsync(Control, [Source], Hash, CancellationToken.None, (start, _) => {
            calls++;
            Assert.False(start.Environment.ContainsKey("GH_TOKEN"));
            Assert.DoesNotContain(start.Environment.Keys, k => k.Contains("Gate", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }, new Hashtable {
            ["Gate__Gui__CollectorRepo"] = Control, ["gate:gui:trustedrepos"] = Source,
            ["Gate__Gui__ConfigurationSha256"] = Hash, ["GH_TOKEN"] = OwnerToken
        });
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Prepare_environment_guard_ignores_only_the_configuration_pin_until_it_is_known()
    {
        GuiCollectorSettings.CheckEnvironment(Control, [Source], environment: new Hashtable {
            ["Gate__Gui__ConfigurationSha256"] = "old-pin"
        });
        Assert.Throws<InvalidOperationException>(() => GuiCollectorSettings.CheckEnvironment(Control, [Source],
            environment: new Hashtable { ["Gate__Gui__CollectorRepo"] = "other/repo" }));
    }

    [Fact]
    public async Task Host_defaults_are_known_pinned_metadata_and_success_runs_only_read_only_fake_probes()
    {
        var configuration = GuiCollectorHost.DefaultConfiguration();
        Assert.Equal(Control, configuration["collectorRepository"]!.GetValue<string>());
        Assert.Equal("/Applications/Xcode.app/Contents/Developer", configuration["developer"]!.GetValue<string>());
        Assert.Equal("26.5", configuration["xcode"]!.GetValue<string>());
        Assert.Equal("17F42", configuration["build"]!.GetValue<string>());
        Assert.Equal("iOS-26-5", configuration["runtime"]!.GetValue<string>());
        Assert.Equal("iPhone-17", configuration["deviceType"]!.GetValue<string>());
        Assert.Equal(new[] { Source, "michaeltrefry/dark-factory-s6-fixtures" }, configuration["trustedRepositories"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { Source, "other-owner/dark-factory-s6-fixtures" }, GuiCollectorHost.DefaultConfiguration("other-owner")["trustedRepositories"]!.AsArray().Select(n => n!.GetValue<string>()));
        var calls = new List<string>();
        await GuiCollectorHost.VerifyAsync(configuration, CancellationToken.None, Probe(calls));
        Assert.Equal(new[] { "/usr/bin/xcode-select -p", "/usr/bin/xcodebuild -version",
            "/usr/bin/xcodebuild -checkFirstLaunchStatus", "/usr/bin/xcodebuild -license check",
            "/usr/bin/xcrun simctl list runtimes -j", "/usr/bin/xcrun simctl list devicetypes -j" }, calls);
    }

    [Theory]
    [InlineData("developer", "/Applications/Other.app/Contents/Developer")]
    [InlineData("xcode", "26.4")]
    [InlineData("build", "OTHER")]
    [InlineData("runtime", "iOS-99-0")]
    [InlineData("deviceType", "iPhone-99")]
    public async Task Host_mismatches_refuse_without_root_install_license_accept_create_or_boot(string key, string value)
    {
        var configuration = GuiCollectorHost.DefaultConfiguration();
        configuration[key] = value;
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorHost.VerifyAsync(configuration, CancellationToken.None, Probe(calls)));
        Assert.All(calls, call => {
            Assert.DoesNotContain("sudo", call);
            Assert.DoesNotContain("create", call);
            Assert.DoesNotContain("boot", call);
            Assert.DoesNotContain("accept", call);
            Assert.DoesNotContain("runFirstLaunch", call);
        });
        Assert.InRange(calls.Count, 1, 6);
    }

    [Fact]
    public async Task An_installed_but_unavailable_pinned_runtime_does_not_pass_host_validation()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorHost.VerifyAsync(
            GuiCollectorHost.DefaultConfiguration(), CancellationToken.None, Probe(calls, runtimeAvailable: false)));
        Assert.DoesNotContain(calls, c => c.Contains("devicetypes"));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Root_entry_stages_compiled_owner_private_bytes_outside_a_malicious_checkout_and_passes_no_checkout_script()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "df-gui-bootstrap-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var checkout = Path.Combine(root, "malicious-checkout");
            Directory.CreateDirectory(Path.Combine(checkout, "scripts"));
            var malicious = Path.Combine(checkout, "scripts", "setup-gui-collector.py");
            await File.WriteAllTextAsync(malicious, "raise Exception('source checkout must never execute as root')");
            var closed = new List<string>();
            string stage;
            using (var bootstrap = await GuiCollectorDeployment.StageBootstrapAsync(CancellationToken.None, Path.Combine(root, "owner-stage"), (path, _) => {
                closed.Add(path);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
                return Task.CompletedTask;
            }))
            {
                stage = bootstrap.Directory;
                Assert.Single(closed);
                Assert.Equal(GuiCollectorDeployment.BootstrapBytes(), await File.ReadAllBytesAsync(bootstrap.Script));
                Assert.NotEqual(await File.ReadAllTextAsync(malicious), await File.ReadAllTextAsync(bootstrap.Script));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(bootstrap.Script));
                var installer = GuiCollectorDeployment.InstallerStartInfo(bootstrap.Script, "/owner/plan.json", "/owner/authority.json");
                Assert.Equal("/", installer.WorkingDirectory);
                Assert.Equal(new[] { "/usr/bin/python3", "-I", bootstrap.Script, "--apply", "--plan", "/owner/plan.json", "--authority", "/owner/authority.json" }, installer.ArgumentList);
                Assert.DoesNotContain(checkout, string.Join(' ', installer.ArgumentList));
                var inventory = GuiCollectorDeployment.InventoryStartInfo(bootstrap.Script, "/old/runner");
                Assert.Equal(new[] { "/usr/bin/python3", "-I", bootstrap.Script, "--inventory", "--old-root", "/old/runner" }, inventory.ArgumentList);
                Assert.DoesNotContain(checkout, string.Join(' ', inventory.ArgumentList));
                Assert.Equal("/", inventory.WorkingDirectory);
            }
            Assert.False(Directory.Exists(stage));
            Assert.True(File.Exists(malicious));
        }
        finally { Directory.Delete(root, true); }
    }

    static Func<string, string[], CancellationToken, Task<string>> Probe(List<string> calls, bool runtimeAvailable = true) => (program, arguments, _) => {
        var call = program + " " + string.Join(' ', arguments);
        calls.Add(call);
        return Task.FromResult(call switch {
            "/usr/bin/xcode-select -p" => "/Applications/Xcode.app/Contents/Developer\n",
            "/usr/bin/xcodebuild -version" => "Xcode 26.5\r\nBuild version 17F42\r\n",
            "/usr/bin/xcodebuild -checkFirstLaunchStatus" or "/usr/bin/xcodebuild -license check" => "",
            "/usr/bin/xcrun simctl list runtimes -j" => new JsonObject { ["runtimes"] = new JsonArray(new JsonObject {
                ["identifier"] = "com.apple.CoreSimulator.SimRuntime.iOS-26-5", ["isAvailable"] = runtimeAvailable
            }) }.ToJsonString(),
            "/usr/bin/xcrun simctl list devicetypes -j" => "{\"devicetypes\":[{\"identifier\":\"com.apple.CoreSimulator.SimDeviceType.iPhone-17\"}]}",
            _ => throw new InvalidOperationException("Unexpected host mutation/probe " + call)
        });
    };

    static void AssertOwnerAndAppCredentials(FakeApi api)
    {
        foreach (var request in api.Requests)
        {
            var authorization = request.Headers["Authorization"];
            if (!request.PathAndQuery.EndsWith("/installation")) { Assert.Equal("Bearer " + OwnerToken, authorization); continue; }
            Assert.StartsWith("Bearer ey", authorization);
            Assert.DoesNotContain(OwnerToken, authorization);
            var parts = authorization["Bearer ".Length..].Split('.');
            Assert.Equal(3, parts.Length);
            static byte[] Decode(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Decode(parts[1])))!;
            Assert.Equal(AppId, payload["iss"]!.GetValue<string>());
            Assert.Equal(Now.ToUnixTimeSeconds() - 60, payload["iat"]!.GetValue<long>());
            Assert.Equal(Now.ToUnixTimeSeconds() + 540, payload["exp"]!.GetValue<long>());
            using var rsa = RSA.Create();
            rsa.ImportFromPem(Pem);
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
    }
}
