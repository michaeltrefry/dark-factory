using System.Net;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator.Tests;

public class FactoryCliTests
{
    private static (System.CommandLine.RootCommand Root, List<string> Calls) Cli()
    {
        var calls = new List<string>();
        var root = FactoryCli.Build(
            (id, ignoreScope, _) => { calls.Add(ignoreScope ? $"run {id} ignore-scope" : $"run {id}"); return Task.FromResult(0); },
            (name, port, _) => { calls.Add($"setup {name} {port}"); return Task.FromResult(0); },
            (repo, _) => { calls.Add($"protect {repo}"); return Task.FromResult(0); },
            _ => { calls.Add("work"); return Task.FromResult(0); },
            _ => { calls.Add("dashboard set-password"); return Task.FromResult(0); },
            (action, scope, _) => { calls.Add($"{action} {scope}"); return Task.FromResult(0); });
        return (root, calls);
    }

    [Theory]
    [InlineData("pause --item sc-77", "pause item:sc-77")]
    [InlineData("continue --item 77", "continue item:sc-77")]
    [InlineData("stop --item sc-77", "stop item:sc-77")]
    [InlineData("pause --epic 12", "pause epic:12")]
    [InlineData("stop --epic 12", "stop epic:12")]
    [InlineData("pause --factory", "pause factory")]
    [InlineData("continue --factory", "continue factory")]
    [InlineData("continue --usage", "continue usage")]
    public async Task Controls_pass_their_scope(string args, string expected)
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(args.Split(' ')).InvokeAsync());
        Assert.Equal([expected], calls);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("pause --factory --item sc-1")]
    [InlineData("stop --item nope")]
    [InlineData("continue --epic 0")]
    [InlineData("continue --usage --factory")]
    [InlineData("pause --usage")]
    [InlineData("stop --usage")]
    public async Task Controls_need_exactly_one_valid_scope(string args)
    {
        var (root, calls) = Cli();
        var parse = root.Parse(args.Split(' '));
        Assert.NotEmpty(parse.Errors);
        Assert.NotEqual(0, await parse.InvokeAsync(new() { Output = TextWriter.Null, Error = TextWriter.Null }));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("sc-25172", 25172)]
    [InlineData("42", 42)]
    public async Task Run_passes_parsed_story_id(string arg, int expected)
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["run", arg]).InvokeAsync());
        Assert.Equal([$"run {expected}"], calls);
    }

    [Fact]
    public async Task Run_ignores_the_scope_only_when_asked()
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["run", "sc-7", "--ignore-scope"]).InvokeAsync());
        Assert.Equal(["run 7 ignore-scope"], calls);
    }

    [Theory]
    [InlineData("run", "nope")]
    [InlineData("run")]
    [InlineData("run", "sc-1", "sc-2")]
    public async Task Invalid_run_arguments_fail_without_running(params string[] args)
    {
        var (root, calls) = Cli();
        var parse = root.Parse(args);
        Assert.NotEmpty(parse.Errors);
        Assert.NotEqual(0, await parse.InvokeAsync(new() { Output = TextWriter.Null, Error = TextWriter.Null }));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Github_app_setup_has_defaults_and_overrides()
    {
        var (root, calls) = Cli();
        await root.Parse(["github-app", "setup", "--name", "df-test", "--port", "50001"]).InvokeAsync();
        await root.Parse(["github-app", "setup"]).InvokeAsync();
        Assert.Equal("setup df-test 50001", calls[0]);
        Assert.Equal($"setup dark-factory-{Environment.UserName} {FactoryCli.DefaultSetupPort}", calls[1]);
    }

    [Fact]
    public async Task Work_runs_the_host()
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["work"]).InvokeAsync());
        Assert.Equal(["work"], calls);
    }

    [Fact]
    public async Task Dashboard_set_password_runs_its_handler()
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["dashboard", "set-password"]).InvokeAsync());
        Assert.Equal(["dashboard set-password"], calls);
    }

    [Fact]
    public async Task Github_repo_protect_passes_parsed_repo()
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["github-repo", "protect", "michaeltrefry/dark-factory-sandbox"]).InvokeAsync());
        Assert.Equal(["protect michaeltrefry/dark-factory-sandbox"], calls);
    }

    [Theory]
    [InlineData("not-a-repo")]
    [InlineData("a/b/c")]
    public async Task Github_repo_protect_rejects_malformed_repo(string arg)
    {
        var (root, calls) = Cli();
        Assert.NotEqual(0, await root.Parse(["github-repo", "protect", arg]).InvokeAsync());
        Assert.Empty(calls);
    }
}

public class FactoryOptionsTests
{
    private static FactoryOptions Options(Dictionary<string, string?> config, InMemorySecrets? secrets = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), secrets ?? new InMemorySecrets());

    [Fact]
    public void Defaults_point_at_local_router_sandbox_and_compose_postgres()
    {
        var options = Options([]);
        Assert.Equal(new Uri("http://localhost:8080/"), options.RouterBaseUrl);
        Assert.Equal("michaeltrefry/dark-factory-sandbox", options.DefaultRepo.FullName);
        Assert.Contains("Port=5434", options.LedgerConnectionString);
        Assert.Equal(Worker.WorkerAuth.ClaudeLogin, options.WorkerAuth);
    }

    [Fact]
    public void Pause_grace_outlasts_the_longest_tool_call_and_is_validated()
    {
        Assert.Equal(TimeSpan.FromSeconds(660), Options([]).PauseGrace); // a 600 s Bash call (e.g. dotnet test) is never cut off
        Assert.Equal(TimeSpan.FromSeconds(900), Options(new() { ["Worker:PauseGraceSeconds"] = "900" }).PauseGrace);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:PauseGraceSeconds"] = "300" }).PauseGrace);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:PauseGraceSeconds"] = "600" }).PauseGrace);
    }

    [Fact]
    public void Worker_auth_mode_is_configurable_and_validated()
    {
        Assert.Equal(Worker.WorkerAuth.RouterKey, Options(new() { ["Worker:Auth"] = "router-key" }).WorkerAuth);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Auth"] = "api-key" }).WorkerAuth);
    }

    [Fact]
    public void Workers_run_as_the_sandbox_user_by_default_with_a_work_root_outside_both_homes()
    {
        var options = Options([]);
        Assert.Equal(new Worker.WorkerSandbox("_factory", "/usr/local/libexec/dark-factory/factory-worker-launch"), options.WorkerSandbox);
        Assert.Equal("/opt/dark-factory/work", options.WorkRoot);

        var custom = Options(new() { ["Worker:RunAs"] = "_df2", ["Worker:LaunchHelper"] = "/opt/h", ["Factory:WorkRoot"] = "/w" });
        Assert.Equal(new Worker.WorkerSandbox("_df2", "/opt/h"), custom.WorkerSandbox);
        Assert.Equal("/w", custom.WorkRoot);
    }

    [Fact]
    public void Run_as_none_runs_workers_as_the_owner_under_the_owners_work_root()
    {
        var options = Options(new() { ["Worker:RunAs"] = "none" });
        Assert.Null(options.WorkerSandbox);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dark-factory"), options.WorkRoot);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:RunAs"] = "" }).WorkerSandbox);
    }

    [Fact]
    public void Config_wins_over_keychain_and_keychain_is_the_fallback()
    {
        var secrets = new InMemorySecrets();
        secrets.Set(SecretAccounts.RouterKey, "rk_keychain");
        secrets.Set(SecretAccounts.GitHubAppId, "99");

        Assert.Equal("rk_config", Options(new() { ["Router:Key"] = "rk_config" }, secrets).RouterKey);
        Assert.Equal("rk_keychain", Options([], secrets).RouterKey);
        Assert.Equal("99", Options([], secrets).GitHubAppId);
    }

    [Fact]
    public void Watch_scope_is_empty_by_default_and_reads_lists_or_arrays()
    {
        Assert.True(Options([]).WatchScope.IsEmpty);
        Assert.Equal(TimeSpan.FromSeconds(60), Options([]).PollInterval);

        var csv = Options(new() { ["Shortcut:Watch:Teams"] = "darkfactory, @other", ["Shortcut:Watch:Epics"] = "25171,7", ["Intake:PollSeconds"] = "15" });
        Assert.Equal(["darkfactory", "other"], csv.WatchScope.Teams);
        Assert.Equal([25171, 7], csv.WatchScope.Epics);
        Assert.Equal(TimeSpan.FromSeconds(15), csv.PollInterval);

        var array = Options(new() { ["Shortcut:Watch:Epics:0"] = "25171", ["Shortcut:Watch:Epics:1"] = "8" });
        Assert.Empty(array.WatchScope.Teams);
        Assert.Equal([25171, 8], array.WatchScope.Epics);

        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Shortcut:Watch:Epics"] = "sc-1" }).WatchScope);
    }

    [Fact]
    public void Missing_credential_names_where_to_set_it_without_leaking_values()
    {
        var options = Options([]);
        var ex = Assert.Throws<MissingCredentialException>(() => options.GitHubAppPrivateKeyPem);
        Assert.Contains("github-app-private-key", ex.Message);
        Assert.False(options.TryGet(o => o.GitHubAppId, out _));
    }
}

public class RouterClientTests
{
    [Fact]
    public async Task Session_cost_is_read_with_router_key_header()
    {
        var api = new FakeApi().On("GET /v1/sessions/sess-1/cost", HttpStatusCode.OK,
            """{"session_id":"sess-1","request_count":3,"actual_cost_usd_micros":1250,"actual_cost_usd":0.00125}""");
        var client = new RouterClient(api.Client("http://localhost:8080/"), "rk_test");

        var cost = await client.GetSessionCostAsync("sess-1", CancellationToken.None);

        Assert.Equal(new SessionCost("sess-1", 3, 1250), cost);
        Assert.Equal("rk_test", api.Requests.Single().Headers["X-Weave-Router-Key"]);
    }

    [Fact]
    public async Task Unknown_session_returns_null()
    {
        var client = new RouterClient(new FakeApi().Client("http://localhost:8080/"), "rk");
        Assert.Null(await client.GetSessionCostAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task Subscription_usage_is_read_with_router_key_header_and_the_routers_nanosecond_timestamps()
    {
        var api = new FakeApi().On("GET /v1/subscriptions/usage", HttpStatusCode.OK,
            """
            {"as_of":"2026-10-06T22:00:14.152459584Z","all_exhausted":true,"resumes_at":"2026-10-07T03:00:00.5Z",
             "known_credentials":1,"observed_credentials":1,"credentials":[]}
            """);
        var client = new RouterClient(api.Client("http://localhost:8080/"), "rk_test");

        var usage = await client.GetUsageAsync(CancellationToken.None);

        Assert.True(usage.AllExhausted);
        Assert.Equal(1, usage.KnownCredentials);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 3, 0, 0, 500, TimeSpan.Zero), usage.ResumesAt);
        Assert.Equal("rk_test", api.Requests.Single().Headers["X-Weave-Router-Key"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RouterClient(new FakeApi().Client("http://localhost:8080/"), "rk").GetUsageAsync(CancellationToken.None));
    }
}

public class MacKeychainTests
{
    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEsecretkeymaterial\n-----END RSA PRIVATE KEY-----\n";

    [Fact]
    public void Set_command_sends_the_secret_on_stdin_never_in_argv()
    {
        var (args, stdin) = MacKeychain.BuildSetCommand(SecretAccounts.GitHubAppPrivateKey, Pem);
        var encoded = "b64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Pem));

        Assert.Equal(["-i"], args); // nothing but the interactive flag: the secret cannot be in argv
        Assert.Equal($"add-generic-password -U -s dark-factory -a github-app-private-key -w {encoded}\n", stdin);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a\nfind-generic-password")]
    [InlineData("")]
    public void Set_command_rejects_account_names_that_would_break_the_stdin_line(string account) =>
        Assert.Throws<ArgumentException>(() => MacKeychain.BuildSetCommand(account, "v"));

    [Fact]
    public void Set_command_rejects_values_too_long_for_security_interactive_mode() =>
        Assert.Throws<ArgumentException>(() => MacKeychain.BuildSetCommand("router-key", new string('k', 2800)));
}
