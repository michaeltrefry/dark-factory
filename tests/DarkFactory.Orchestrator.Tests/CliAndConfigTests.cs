using System.Net;
using DarkFactory.Orchestrator.Gate;
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
            (name, port, gate, _) => { calls.Add(gate ? $"setup {name} {port} gate" : $"setup {name} {port}"); return Task.FromResult(0); },
            (repo, _) => { calls.Add($"protect {repo}"); return Task.FromResult(0); },
            _ => { calls.Add("work"); return Task.FromResult(0); },
            _ => { calls.Add("dashboard set-password"); return Task.FromResult(0); },
            (action, scope, _) => { calls.Add($"{action} {scope}"); return Task.FromResult(0); },
            (id, epic, _) => { calls.Add(epic ? $"kanban key --epic {id}" : $"kanban key {id}"); return Task.FromResult(0); });
        return (root, calls);
    }

    [Theory]
    [InlineData("pause --item sc-77", "pause item:sc-77")]
    [InlineData("continue --item 77", "continue item:sc-77")]
    [InlineData("stop --item sc-77", "stop item:sc-77")]
    [InlineData("stop --item gh-4", "stop item:gh-4")]
    [InlineData("pause --item GH-4", "pause item:gh-4")]
    [InlineData("pause --item kb-7", "pause item:kb-7")]
    [InlineData("stop --item KB-7", "stop item:kb-7")]
    [InlineData("pause --epic 12", "pause epic:12")]
    [InlineData("stop --epic 12", "stop epic:12")]
    [InlineData("pause --epic kb-3", "pause epic:kb-3")]
    [InlineData("continue --epic kb-3", "continue epic:kb-3")]
    [InlineData("stop --epic KB-3", "stop epic:kb-3")]
    [InlineData("pause --repo Owner/Alpha", "pause repo:owner/alpha")]
    [InlineData("continue --repo owner/alpha", "continue repo:owner/alpha")]
    [InlineData("stop --repo owner/alpha", "stop repo:owner/alpha")]
    [InlineData("pause --factory", "pause factory")]
    [InlineData("continue --factory", "continue factory")]
    [InlineData("continue --usage", "continue usage")]
    [InlineData("continue --freeze", "continue freeze")]
    public async Task Controls_pass_their_scope(string args, string expected)
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(args.Split(' ')).InvokeAsync());
        Assert.Equal([expected], calls);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("pause --repo owner/alpha --item sc-1")]
    [InlineData("continue --repo owner/alpha --factory")]
    [InlineData("stop --repo owner/../alpha")]
    [InlineData("pause --repo ../alpha")]
    [InlineData("pause --repo owner/alpha?")]
    [InlineData("pause --factory --item sc-1")]
    [InlineData("stop --item nope")]
    [InlineData("continue --epic 0")]
    [InlineData("pause --epic kb-0")]
    [InlineData("pause --epic gh-3")]
    [InlineData("stop --epic sc-3")]
    [InlineData("stop --epic nope")]
    [InlineData("pause --epic kb-3 --item kb-3")]
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
    [InlineData("sc-25172", "sc-25172")]
    [InlineData("42", "sc-42")]
    [InlineData("gh-12", "gh-12")]
    [InlineData("GH-3", "gh-3")]
    [InlineData("kb-7", "kb-7")]
    [InlineData("KB-7", "kb-7")]
    public async Task Run_passes_the_parsed_work_item(string arg, string expected)
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
        Assert.Equal(["run sc-7 ignore-scope"], calls);
    }

    [Theory]
    [InlineData("run", "nope")]
    [InlineData("run")]
    [InlineData("run", "sc-1", "sc-2")]
    [InlineData("run", "gh-")]
    [InlineData("run", "gh-0")]
    [InlineData("run", "gh-1x")]
    [InlineData("run", "kb-")]
    [InlineData("run", "kb-0")]
    [InlineData("run", "e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11")] // a board id is not a work item id: `factory kanban key` gives it one
    [InlineData("kanban", "key")]
    [InlineData("kanban", "key", "kb-7")]
    [InlineData("kanban", "key", "not-a-guid")]
    [InlineData("kanban", "key", "--epic")]
    [InlineData("kanban", "key", "--epic", "kb-3")]
    [InlineData("kanban", "key", "--epic", "not-a-guid")]
    [InlineData("kanban", "key", "e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11", "--epic", "a1865c06-7b9a-4f0f-9a1e-3a8f3c0f2d22")] // one or the other
    public async Task Invalid_run_arguments_fail_without_running(params string[] args)
    {
        var (root, calls) = Cli();
        var parse = root.Parse(args);
        Assert.NotEmpty(parse.Errors);
        Assert.NotEqual(0, await parse.InvokeAsync(new() { Output = TextWriter.Null, Error = TextWriter.Null }));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Kanban_key_passes_the_items_board_id()
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["kanban", "key", "E0865C06-7B9A-4F0F-9A1E-3A8F3C0F2D11"]).InvokeAsync());
        Assert.Equal(["kanban key e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11"], calls);
    }

    [Fact]
    public async Task Kanban_key_with_epic_passes_the_epics_board_id_as_an_epic()
    {
        var (root, calls) = Cli();
        Assert.Equal(0, await root.Parse(["kanban", "key", "--epic", "A1865C06-7B9A-4F0F-9A1E-3A8F3C0F2D22"]).InvokeAsync());
        Assert.Equal(["kanban key --epic a1865c06-7b9a-4f0f-9a1e-3a8f3c0f2d22"], calls);
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
    public async Task Github_app_setup_gate_registers_the_gate_app_under_its_own_default_name()
    {
        var (root, calls) = Cli();
        await root.Parse(["github-app", "setup", "--gate"]).InvokeAsync();
        await root.Parse(["github-app", "setup", "--gate", "--name", "df-gate"]).InvokeAsync();
        Assert.Equal([$"setup dark-factory-gate-{Environment.UserName} {FactoryCli.DefaultSetupPort} gate", $"setup df-gate {FactoryCli.DefaultSetupPort} gate"],
            calls);
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
        Assert.Equal(Worker.WorkerAuth.RouterKey, options.WorkerAuth); // the worker holds no provider credential by default (E5)
    }

    [Fact]
    public void Metrics_leave_out_the_sandbox_repo_and_demo_markers_by_default_and_as_configured()
    {
        Assert.Equal(["michaeltrefry/dark-factory-sandbox"], Options([]).Metrics.SandboxRepos);
        Assert.Equal(["[demo]", "[sandbox]"], Options([]).Metrics.DemoMarkers);
        var configured = Options(new() { ["Metrics:SandboxRepos"] = " acme/play , acme/demo", ["Metrics:DemoMarkers"] = "(trial)" }).Metrics;
        Assert.Equal(["acme/play", "acme/demo"], configured.SandboxRepos);
        Assert.Equal(["(trial)"], configured.DemoMarkers);
    }

    [Fact]
    public void Watched_issue_repos_default_to_none_and_are_validated()
    {
        Assert.Empty(Options([]).WatchedIssueRepos);
        Assert.Equal(["acme/widgets", "acme/gadgets"],
            Options(new() { ["GitHub:Watch:Repos"] = " acme/widgets, acme/gadgets ,ACME/widgets" }).WatchedIssueRepos.Select(r => r.FullName));
        Assert.Equal(["acme/widgets"], Options(new() { ["GitHub:Watch:Repos:0"] = "acme/widgets" }).WatchedIssueRepos.Select(r => r.FullName));
        Assert.Contains("GitHub:Watch:Repos",
            Assert.Throws<InvalidOperationException>(() => Options(new() { ["GitHub:Watch:Repos"] = "acme" }).WatchedIssueRepos).Message);
    }

    [Fact]
    public void Factory_repos_default_to_none_are_read_and_validated_and_turn_on_no_issue_polling()
    {
        Assert.Empty(Options([]).Repos);
        Assert.Equal(["michaeltrefry/dark-factory-sandbox"], Options([]).ConfiguredRepos);
        var extra = Options(new() { ["Factory:Repos"] = " acme/widgets, acme/gadgets ,ACME/widgets" });
        Assert.Equal(["acme/widgets", "acme/gadgets"], extra.Repos.Select(r => r.FullName));
        Assert.Equal(["acme/widgets"], Options(new() { ["Factory:Repos:0"] = "acme/widgets" }).Repos.Select(r => r.FullName));
        // A repository named there is one the factory plans and works in (and whose main the freeze watches) …
        Assert.Equal(["michaeltrefry/dark-factory-sandbox", "acme/widgets", "acme/gadgets"], extra.ConfiguredRepos);
        // … and its issues are not polled: that is GitHub:Watch:Repos alone, so the setting needs no sandboxed triage worker.
        Assert.Empty(extra.WatchedIssueRepos);
        Assert.Null(Issues.IssueIntake.UnsandboxedRefusal(sandbox: null, extra.WatchedIssueRepos)); // sandbox: null is Worker:RunAs=none
        extra.ValidateSettings();

        // The configured repositories are the three settings together, each repository once whatever its case.
        var all = Options(new()
        {
            ["Factory:DefaultRepo"] = "acme/main", ["Factory:Repos"] = "ACME/Main,acme/widgets,acme/both", ["GitHub:Watch:Repos"] = "acme/issues,Acme/Both",
        });
        Assert.Equal(["acme/main", "acme/widgets", "acme/both", "acme/issues"], all.ConfiguredRepos);
        Assert.Equal(["acme/issues", "Acme/Both"], all.WatchedIssueRepos.Select(r => r.FullName));

        // A malformed entry is refused at start-up, naming the key.
        foreach (var bad in new[] { "acme", "acme/widgets,acme/", "acme/../x", "a/b/c" })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => Options(new() { ["Factory:Repos"] = bad }).ValidateSettings());
            Assert.StartsWith("Factory:Repos: ", refused.Message);
        }
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
    public void Stuck_detection_and_the_quiet_threshold_are_read_from_config_and_validated()
    {
        Assert.Equal(new Sessions.StuckDetection(5, 0.96), Options([]).StuckDetection);
        Assert.Equal(new Sessions.StuckDetection(3, 0.9),
            Options(new() { ["Worker:StuckRepeats"] = "3", ["Worker:StuckSimilarity"] = "0.9" }).StuckDetection);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:StuckRepeats"] = "1" }).StuckDetection);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:StuckSimilarity"] = "0" }).StuckDetection);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:StuckSimilarity"] = "1.5" }).StuckDetection);

        Assert.Equal(TimeSpan.FromMinutes(10), Options([]).QuietThreshold); // the longest legitimate tool call streams nothing that long
        Assert.Equal(TimeSpan.FromMinutes(2.5), Options(new() { ["Worker:QuietMinutes"] = "2.5" }).QuietThreshold);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:QuietMinutes"] = "0" }).QuietThreshold);
    }

    [Theory]
    [InlineData("Review:Models", "claude-opus-5")]
    [InlineData("Review:Correctness:Models", "claude-opus-5-5")]
    [InlineData("Review:SpecConformance:Models", "gpt-6-astra")]
    [InlineData("Review:Security:Models", "claude-opus-6")]
    [InlineData("Review:Confirm:Models", "claude-sonnet-5")]
    [InlineData("Review:Models", "")] // set at all, even empty, is refused: the owner expects it to apply
    public void A_pinned_review_model_setting_is_refused_at_start_up_rather_than_ignored(string key, string value)
    {
        var ex = Assert.Throws<ReviewConfigurationException>(() => Options(new() { [key] = value }).RejectReviewModelSettings());
        Assert.Contains($"{key} is no longer a setting", ex.Message);
        Assert.Contains("high model class", ex.Message);
        // A section with children (e.g. a JSON array) is refused too.
        Assert.Throws<ReviewConfigurationException>(() => Options(new() { [$"{key}:0"] = "claude-opus-5" }).RejectReviewModelSettings());
        Options([]).RejectReviewModelSettings();
        Options(new() { ["Review:TimeoutMinutes"] = "11" }).RejectReviewModelSettings();
        Assert.Equal((TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30)), (Options([]).CiPollInterval, Options([]).CiTimeout));
    }

    [Fact]
    public async Task Factory_run_with_a_review_model_setting_exits_2_with_the_reason_before_any_router_call()
    {
        var calls = new List<string>();
        var stderr = new StringWriter();

        var exit = await FactoryRunner.RunCommandAsync(Options(new() { ["Review:Confirm:Models"] = "claude-sonnet-5" }),
            _ => { calls.Add("enrollment"); return Task.FromResult<string?>(null); },
            _ => { calls.Add("run"); return Task.FromResult(new RunOutcome(1, DarkFactory.Orchestrator.Ledger.WorkState.Watch, null, null, null)); },
            stderr, CancellationToken.None);

        Assert.Equal(2, exit); // not 1, a failed run
        Assert.Contains("Review:Confirm:Models is no longer a setting", stderr.ToString());
        Assert.DoesNotContain(" at ", stderr.ToString()); // the message, not a stack trace
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Factory_run_with_no_review_model_setting_starts()
    {
        var calls = new List<string>();
        var stderr = new StringWriter();

        var exit = await FactoryRunner.RunCommandAsync(Options(new() { ["CodeGraph:Token"] = "cg_config" }),
            _ => { calls.Add("enrollment"); return Task.FromResult<string?>(null); },
            _ => { calls.Add("run"); return Task.FromResult(new RunOutcome(1, DarkFactory.Orchestrator.Ledger.WorkState.Watch, null, null, null)); },
            stderr, CancellationToken.None);

        Assert.Equal((0, ""), (exit, stderr.ToString()));
        Assert.Equal(["enrollment", "run"], calls);
    }

    [Fact]
    public async Task Factory_run_and_work_warn_at_start_up_without_a_codegraph_token_and_still_start()
    {
        Assert.Null(FactoryRunner.CodeGraphTokenWarning(Options(new() { ["CodeGraph:Token"] = "cg_config" })));
        if (Environment.GetEnvironmentVariable("FACTORY_CODEGRAPH_TOKEN") is not null)
        {
            return; // the environment supplies a token
        }
        const string warning = "no CodeGraph token (CodeGraph:Token, env FACTORY_CODEGRAPH_TOKEN, or keychain account 'codegraph-token' "
            + "(service 'dark-factory')): the merge gate will merge no change whose tier requires risk-threshold";
        // `factory work` prints the same warning (Program.cs, through CodeGraphTokenWarning) before it starts.
        Assert.Equal(warning, FactoryRunner.CodeGraphTokenWarning(Options([])));
        var stderr = new StringWriter();
        var calls = new List<string>();

        var exit = await FactoryRunner.RunCommandAsync(Options([]),
            _ => { calls.Add("enrollment"); return Task.FromResult<string?>(null); },
            _ => { calls.Add("run"); return Task.FromResult(new RunOutcome(1, DarkFactory.Orchestrator.Ledger.WorkState.Watch, null, null, null)); },
            stderr, CancellationToken.None);

        Assert.Equal((0, warning + Environment.NewLine), (exit, stderr.ToString()));
        Assert.Equal(["enrollment", "run"], calls);
    }

    [Fact]
    public void The_gate_app_is_a_separate_credential_from_the_workers_app()
    {
        var secrets = new InMemorySecrets();
        secrets.Set(SecretAccounts.GitHubAppId, "1");
        var options = Options([], secrets);
        Assert.Throws<MissingCredentialException>(() => options.GitHubGateAppId);
        secrets.Set(SecretAccounts.GitHubGateAppId, "2");
        Assert.Equal(("1", "2"), (options.GitHubAppId, options.GitHubGateAppId));
    }

    [Fact]
    public void Worker_auth_mode_is_configurable_and_validated()
    {
        Assert.Equal(Worker.WorkerAuth.RouterKey, Options(new() { ["Worker:Auth"] = "router-key" }).WorkerAuth);
        Assert.Equal(Worker.WorkerAuth.ClaudeLogin, Options(new() { ["Worker:Auth"] = "claude-login" }).WorkerAuth);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Auth"] = "api-key" }).WorkerAuth);
    }

    [Fact]
    public void Workers_run_as_the_sandbox_user_by_default_with_a_work_root_outside_both_homes()
    {
        var options = Options([]);
        // The sandbox carries its work root's lock (sc-25988): every run of the helper happens under it.
        Assert.Equal(new Worker.WorkerSandbox("_factory", "/usr/local/libexec/dark-factory/factory-worker-launch") { Lock = Locked("/opt/dark-factory/work") }, options.WorkerSandbox);
        Assert.Equal("/opt/dark-factory/work", options.WorkRoot);

        var custom = Options(new() { ["Worker:RunAs"] = "_df2", ["Worker:LaunchHelper"] = "/opt/h", ["Factory:WorkRoot"] = "/w" });
        Assert.Equal(new Worker.WorkerSandbox("_df2", "/opt/h") { Lock = Locked("/w") }, custom.WorkerSandbox);
        Assert.Equal("/w", custom.WorkRoot);
    }

    /// <summary>The lock of a work root: its <c>.factory-run.lock</c>.</summary>
    private static WorkerLock Locked(string workRoot) => new(Path.Combine(workRoot, ".factory-run.lock"));

    [Fact]
    public void Worker_slots_default_to_the_one_sandbox_user_and_a_configured_count_adds_a_user_a_helper_and_a_lock_each()
    {
        // Unset (E9): one slot, which is exactly the sandbox before slots — one user, the same helper path, one lock file.
        var unset = Options([]);
        Assert.Equal(1, unset.WorkerSlotCount);
        Assert.Equal(unset.WorkerSandbox, Assert.Single(unset.WorkerSlots!.All).Sandbox);
        Assert.Equal("sudo scripts/setup-worker-user.sh", unset.WorkerSlots.All[0].Sandbox.Reinstall);

        // A non-default value is read: slot n is the configured user with n appended, through the configured helper path with -n
        // appended, under its own lock file.
        var three = Options(new() { ["Worker:Slots"] = "3", ["Worker:RunAs"] = "_df", ["Worker:LaunchHelper"] = "/opt/h", ["Factory:WorkRoot"] = "/w" });
        Assert.Equal(3, three.WorkerSlotCount);
        Assert.Equal([(1, "_df", "/opt/h", "/w/.factory-run.lock"), (2, "_df2", "/opt/h-2", "/w/slot-locks/.factory-run-2.lock"), (3, "_df3", "/opt/h-3", "/w/slot-locks/.factory-run-3.lock")],
            three.WorkerSlots!.All.Select(s => (s.Number, s.User, s.Sandbox.HelperPath, s.Lock!.Path)));
        Assert.All(three.WorkerSlots.All, s => Assert.Equal("sudo scripts/setup-worker-user.sh --slots 3", s.Sandbox.Reinstall));
        Assert.Equal(Worker.WorkerSlots.MaxSlots, Options(new() { ["Worker:Slots"] = "8" }).WorkerSlots!.Count);
        three.ValidateSettings();

        // Out of range is refused at start-up, naming the key.
        foreach (var refused in new[] { "0", "-1", "9" })
        {
            Assert.Contains("Worker:Slots must be a whole number from 1 to 8",
                Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Slots"] = refused }).ValidateSettings()).Message);
        }
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Slots"] = "two" }).ValidateSettings());
    }

    [Fact]
    public void Worker_slots_with_run_as_none_are_refused_rather_than_ignored()
    {
        // Unsandboxed there is no sandbox user and no lock: a slot count would have no consumer (E10).
        var ex = Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Slots"] = "2", ["Worker:RunAs"] = "none" }).ValidateSettings());
        Assert.Contains("Worker:Slots is 2, but Worker:RunAs=none", ex.Message);
        // 1 (or unset) is today's unsandboxed development mode.
        var none = Options(new() { ["Worker:Slots"] = "1", ["Worker:RunAs"] = "none" });
        none.ValidateSettings();
        Assert.Null(none.WorkerSlots);
    }

    [Fact]
    public void More_slots_than_concurrent_runs_is_allowed_and_noted_and_more_runs_than_slots_is_the_ordinary_case()
    {
        Options(new() { ["Worker:Slots"] = "3", ["Intake:MaxConcurrentRuns"] = "1" }).ValidateSettings();
        Options(new() { ["Worker:Slots"] = "1", ["Intake:MaxConcurrentRuns"] = "4" }).ValidateSettings();
        Assert.Null(FactoryRunner.SlotsNote(slots: 1, runs: 1));
        Assert.Null(FactoryRunner.SlotsNote(slots: 2, runs: 4));
        Assert.Null(FactoryRunner.SlotsNote(slots: 2, runs: 2));
        var note = FactoryRunner.SlotsNote(slots: 3, runs: 2);
        Assert.Contains("Worker:Slots is 3 but Intake:MaxConcurrentRuns is 2", note);
        Assert.Contains("raise Intake:MaxConcurrentRuns", note);
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
    public void CodeGraph_base_url_defaults_to_the_hosted_codegraph_is_read_from_config_and_an_invalid_one_is_refused_at_start_up()
    {
        // sc-25705, E7.
        Assert.Equal(Gateway.OutboundHttp.CodeGraphDefaultBase, Options([]).CodeGraphBaseUrl);
        Assert.Equal(new Uri("http://127.0.0.1:5005/cg/"), Options(new() { ["CodeGraph:BaseUrl"] = "http://127.0.0.1:5005/cg" }).CodeGraphBaseUrl);
        foreach (var bad in new[] { "codegraph.local", "ftp://codegraph.local", "https://api.anthropic.com/" })
        {
            Assert.Contains("CodeGraph:BaseUrl",
                Assert.Throws<InvalidOperationException>(() => Options(new() { ["CodeGraph:BaseUrl"] = bad }).ValidateSettings()).Message);
        }
    }

    [Fact]
    public async Task The_codegraph_token_comes_from_config_or_keychain_and_without_one_analyze_impact_answers_an_error()
    {
        var secrets = new InMemorySecrets();
        secrets.Set(SecretAccounts.CodeGraphToken, "cg_keychain");
        Assert.Equal("cg_config", Options(new() { ["CodeGraph:Token"] = "cg_config" }, secrets).CodeGraphToken);
        Assert.Equal("cg_keychain", Options([], secrets).CodeGraphToken);
        Assert.Equal("codegraph-token", SecretAccounts.CodeGraphToken);

        // Missing: start-up is unaffected (ValidateSettings reads no secret); the reviewers' tool answers an error, said once.
        var none = Options([]);
        if (Environment.GetEnvironmentVariable("FACTORY_CODEGRAPH_TOKEN") is null)
        {
            Assert.False(none.TryGet(o => o.CodeGraphToken, out _));
            none.ValidateSettings();
            var log = new StringWriter();
            using var http = new HttpClient();
            var tools = FactoryRunner.CreateReviewTools(none, new FakeReviewFiles("head", []), http, log);
            Assert.Contains("no CodeGraph token", log.ToString());
            var outcome = await tools.RunAsync(new AnswerBlock(AnswerBlock.ToolUse, null, "toolu_1", ReviewTools.AnalyzeImpact, "{\"name\":\"X\"}"),
                ReviewTools.Session(new RepoRef("o", "r"), "head", "base", 0), CancellationToken.None);
            Assert.True(outcome.IsError);
            Assert.Contains("CodeGraph is not configured", outcome.Content);
        }
        // With a token, analyze_impact asks CodeGraph with it.
        var codeGraph = new FakeApi();
        var configured = FactoryRunner.CreateReviewTools(Options(new() { ["CodeGraph:Token"] = "cg_config" }), new FakeReviewFiles("head", []),
            codeGraph.Client("https://codegraph.test/"), TextWriter.Null);
        await configured.RunAsync(new AnswerBlock(AnswerBlock.ToolUse, null, "toolu_1", ReviewTools.AnalyzeImpact, "{\"name\":\"X\"}"),
            ReviewTools.Session(new RepoRef("o", "r"), "head", "base", 0), CancellationToken.None);
        Assert.Equal("Bearer cg_config", codeGraph.Requests.First().Headers["Authorization"]);
    }

    [Fact]
    public void Kanban_mcp_url_defaults_to_the_hosted_kanbanboard_is_read_from_config_and_an_invalid_one_is_refused_at_start_up()
    {
        // sc-25707, E7.
        Assert.Equal(new Uri("https://kanban-mcp.trefry" + ".net/mcp"), Options([]).KanbanMcpUrl);
        Assert.Equal(Gateway.OutboundHttp.KanbanDefaultMcpUrl, Options([]).KanbanMcpUrl);
        Assert.Equal(new Uri("http://127.0.0.1:5006/kb/mcp"), Options(new() { ["Kanban:McpUrl"] = "http://127.0.0.1:5006/kb/mcp" }).KanbanMcpUrl);
        foreach (var bad in new[] { "kanban.local/mcp", "ftp://kanban.local/mcp", "https://api.anthropic.com/mcp" })
        {
            Assert.Contains("Kanban:McpUrl",
                Assert.Throws<InvalidOperationException>(() => Options(new() { ["Kanban:McpUrl"] = bad }).ValidateSettings()).Message);
        }
    }

    [Fact]
    public void A_kanban_write_token_equal_to_the_read_token_is_refused_at_start_up_without_printing_either()
    {
        // sc-25992 (E2): the read token reaches model sessions through the MCP proxy; the full-scope one must never be it.
        const string token = "kb_same_full_scope_token_value";
        var secrets = new InMemorySecrets();
        secrets.Set(SecretAccounts.KanbanWriteToken, token);
        var refused = Assert.Throws<InvalidOperationException>(() => Options(new() { ["Kanban:Token"] = token }, secrets).ValidateSettings());
        Assert.Contains("Kanban:WriteToken must not be the same token as Kanban:Token", refused.Message);
        Assert.DoesNotContain(token, refused.Message);

        // Apart, or only one of them set, start-up goes on.
        Options(new() { ["Kanban:Token"] = "kb_read_scope_token" }, secrets).ValidateSettings();
        Options(new() { ["Kanban:WriteToken"] = "kb_other_full_scope" }).ValidateSettings();
    }

    [Fact]
    public async Task The_kanban_token_comes_from_config_or_keychain_and_without_one_there_is_no_kanban_upstream()
    {
        var secrets = new InMemorySecrets();
        secrets.Set(SecretAccounts.KanbanToken, "kb_keychain");
        Assert.Equal("kb_config", Options(new() { ["Kanban:Token"] = "kb_config" }, secrets).KanbanToken);
        Assert.Equal("kb_keychain", Options([], secrets).KanbanToken);
        Assert.Equal("kanban-token", SecretAccounts.KanbanToken);

        using var http = new HttpClient();
        if (Environment.GetEnvironmentVariable("FACTORY_KANBAN_TOKEN") is null)
        {
            // Missing: not an error; start-up is unaffected and there is no Kanban upstream (said once).
            var none = Options([]);
            Assert.False(none.TryGet(o => o.KanbanToken, out _));
            none.ValidateSettings();
            var log = new StringWriter();
            Assert.Null(FactoryRunner.CreateUpstreams(none, http, http, log).Kanban);
            Assert.Contains("no Kanban token", log.ToString());
        }
        // With a token, the upstream asks Kanban:McpUrl with it.
        var kanban = new FakeMcpServer("/kb/mcp", ["get_board", "create_work_item"], (t, _) => FakeMcpServer.Text(t));
        var upstreams = FactoryRunner.CreateUpstreams(Options(new() { ["Kanban:Token"] = "kb_config" }), http,
            kanban.Client("https://kanban.test/kb/mcp"), TextWriter.Null);
        var upstream = Assert.IsType<Mcp.McpUpstream>(upstreams.Kanban);
        Assert.False(upstream.RepoScoped);
        Assert.Equal(["get_board"], (await upstream.ListToolsAsync(CancellationToken.None)).Select(t => t.Name));
        Assert.All(kanban.Api.Requests, r => Assert.Equal("Bearer kb_config", r.Headers["Authorization"]));
        await Assert.ThrowsAsync<Mcp.ToolNotAllowedException>(() =>
            upstream.CallAsync("create_work_item", new System.Text.Json.Nodes.JsonObject(), CancellationToken.None));
    }

    [Fact]
    public void Watched_kanban_projects_default_to_none_are_read_as_project_keys_and_a_malformed_key_is_refused_at_start_up()
    {
        // sc-25981, E10.
        Assert.True(Options([]).KanbanWatchScope.IsEmpty);
        Assert.Equal(["DF", "SCENE-WORKS"], Options(new() { ["Kanban:Watch:Projects"] = " df, Scene-Works ,DF" }).KanbanWatchScope.Projects);
        Assert.Equal(["DF"], Options(new() { ["Kanban:Watch:Projects:0"] = "df" }).KanbanWatchScope.Projects);
        Options(new() { ["Kanban:Watch:Projects"] = "df" }).ValidateSettings();
        foreach (var bad in new[] { "two words", "df,a-key-that-is-longer-than-24", "df\tx" })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => Options(new() { ["Kanban:Watch:Projects"] = bad }).ValidateSettings());
            Assert.StartsWith("Kanban:Watch:Projects: ", refused.Message);
        }
    }

    [Fact]
    public void Kanban_web_url_defaults_to_the_hosted_kanbanboard_is_read_from_config_and_an_invalid_one_is_refused_at_start_up()
    {
        // sc-25981, E10: what a Kanban item's link is built on.
        Assert.Equal(new Uri("https://kanban.trefry" + ".net/"), Options([]).KanbanWebUrl);
        Assert.Equal(Gateway.OutboundHttp.KanbanDefaultWebUrl, Options([]).KanbanWebUrl);
        var configured = Options(new() { ["Kanban:WebUrl"] = "http://127.0.0.1:5005/board/" });
        Assert.Equal(new Uri("http://127.0.0.1:5005/board/"), configured.KanbanWebUrl);
        configured.ValidateSettings();
        var item = new Kanban.KanbanWorkItem(Guid.Parse("e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11"), Guid.Parse("a0000000-0000-0000-0000-000000000001"),
            "T", "Story", "Ready", 1);
        Assert.Equal("http://127.0.0.1:5005/board/?project=a0000000-0000-0000-0000-000000000001&item=e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11",
            new Kanban.KanbanLinks(configured.KanbanWebUrl).Item(item));
        foreach (var bad in new[] { "kanban.local", "ftp://kanban.local/", "https://user:pw@kanban.local/", "https://kanban.local/?project=1",
            "https://kanban.local/#x", "https://api.anthropic.com/" })
        {
            Assert.Contains("Kanban:WebUrl",
                Assert.Throws<InvalidOperationException>(() => Options(new() { ["Kanban:WebUrl"] = bad }).ValidateSettings()).Message);
        }
    }

    [Fact]
    public void The_kanban_write_token_is_its_own_credential_from_config_or_keychain_and_never_the_read_token()
    {
        // sc-25981, E2.
        Assert.Equal("kanban-write-token", SecretAccounts.KanbanWriteToken);
        var secrets = new InMemorySecrets();
        secrets.Set(SecretAccounts.KanbanWriteToken, "kbw_keychain");
        Assert.Equal("kbw_config", Options(new() { ["Kanban:WriteToken"] = "kbw_config" }, secrets).KanbanWriteToken);
        if (Environment.GetEnvironmentVariable("FACTORY_KANBAN_WRITE_TOKEN") is not null)
        {
            return; // the environment supplies a token
        }
        Assert.Equal("kbw_keychain", Options([], secrets).KanbanWriteToken);
        // The read token is no write token: with only it set, the write token is missing (and start-up is unaffected: it is
        // needed only to work a Kanban item).
        var readOnly = Options(new() { ["Kanban:Token"] = "kb_read" });
        readOnly.ValidateSettings();
        var missing = Assert.Throws<MissingCredentialException>(() => readOnly.KanbanWriteToken);
        Assert.Contains("env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken', or keychain account 'kanban-write-token'", missing.Message);
        if (Environment.GetEnvironmentVariable("FACTORY_KANBAN_TOKEN") is null)
        {
            Assert.False(Options(new() { ["Kanban:WriteToken"] = "kbw_config" }).TryGet(o => o.KanbanToken, out _));
        }
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "acme/widgets")]
    [InlineData("https://github.com/acme/widgets", "acme/widgets")]
    [InlineData("https://github.com/acme/widgets/", "acme/widgets")]
    [InlineData("http://GitHub.com/acme/widgets.git", "acme/widgets")]
    [InlineData("ssh://git@github.com/acme/widgets.git", "acme/widgets")]
    [InlineData("ssh://git@github.com:22/acme/widgets", "acme/widgets")]
    [InlineData("git@github.com:acme/widgets.git", "acme/widgets")]
    [InlineData("git@github.com:acme/widgets", "acme/widgets")]
    [InlineData(" git@github.com:Acme/Wid.gets.git ", "Acme/Wid.gets")]
    [InlineData("https://gitlab.com/acme/widgets.git", null)]
    [InlineData("https://github.com.evil.example/acme/widgets.git", null)]
    [InlineData("https://evil.example/github.com/acme/widgets", null)]
    [InlineData("https://user:pw@github.com/acme/widgets.git", null)]
    [InlineData("https://github.com/acme", null)]
    [InlineData("https://github.com/acme/widgets/pull/1", null)]
    [InlineData("git@github.com:acme/.git", null)]
    [InlineData("git@github.com:../widgets.git", null)]
    [InlineData("acme/widgets", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_repository_url_names_a_github_repository_in_its_https_and_ssh_forms_or_none(string? url, string? repo) =>
        Assert.Equal(repo, Gateway.GitRemoteReads.RepoOf(url)?.FullName);

    [Fact]
    public void A_repo_line_is_found_in_an_items_text_or_not_at_all()
    {
        Assert.Equal(new RepoRef("acme", "widgets"), RepoResolver.Named("Do it.\n\nRepo: acme/widgets\nRepo: acme/other"));
        Assert.Null(RepoResolver.Named("Do it in the repo: somewhere."));
        Assert.Null(RepoResolver.Named(null));
        Assert.Equal(new RepoRef("o", "d"), RepoResolver.Resolve(null, new RepoRef("o", "d")));
    }

    [Fact]
    public void The_router_cost_settle_delay_defaults_to_five_seconds_and_refuses_a_negative_value()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), Options([]).CostSettleDelay);
        Assert.Equal(TimeSpan.FromSeconds(2.5), Options(new() { ["Router:CostSettleSeconds"] = "2.5" }).CostSettleDelay);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Router:CostSettleSeconds"] = "-1" }).CostSettleDelay);
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

    // E6: each duration key is read (a non-default value comes through) and validated (0 or less refused, naming the key: 0 would
    // poll GitHub or the board in a tight loop, or time a call out at once).
    public static TheoryData<string, Func<FactoryOptions, TimeSpan>, TimeSpan> Durations => new()
    {
        { "Review:TimeoutMinutes", o => o.ReviewTimeout, TimeSpan.FromMinutes(7) },
        { "Gate:CiPollSeconds", o => o.CiPollInterval, TimeSpan.FromSeconds(7) },
        { "Gate:CiTimeoutMinutes", o => o.CiTimeout, TimeSpan.FromMinutes(7) },
        { "Gate:TestTimeoutMinutes", o => o.TestTimeout, TimeSpan.FromMinutes(7) },
        { "Intake:PollSeconds", o => o.PollInterval, TimeSpan.FromSeconds(7) },
        { "Usage:PollSeconds", o => o.UsagePollInterval, TimeSpan.FromSeconds(7) },
        { "Worker:TimeoutMinutes", o => o.WorkerTimeout, TimeSpan.FromMinutes(7) },
        { "Freeze:CheckFailedMinutes", o => o.Freeze.CheckFailedWindow, TimeSpan.FromMinutes(7) },
        { "CodeGraph:OverlayTimeoutMinutes", o => o.CodeGraphOverlayTimeout, TimeSpan.FromMinutes(7) },
    };

    [Theory]
    [MemberData(nameof(Durations))]
    public void Each_duration_comes_from_config_and_a_non_positive_one_is_refused_at_start_up(string key, Func<FactoryOptions, TimeSpan> read, TimeSpan seven)
    {
        Assert.NotEqual(seven, read(Options([])));
        Assert.Equal(seven, read(Options(new() { [key] = "7" })));
        foreach (var bad in new[] { "0", "-1" })
        {
            Assert.Contains($"{key} must be more than 0", Assert.Throws<InvalidOperationException>(() => read(Options(new() { [key] = bad }))).Message);
            Assert.Contains(key, Assert.Throws<InvalidOperationException>(() => Options(new() { [key] = bad }).ValidateSettings()).Message);
        }
    }

    [Fact]
    public void The_gate_and_the_reviewers_client_are_built_from_their_config_keys()
    {
        var options = Options(new()
        {
            ["Gate:CiPollSeconds"] = "7", ["Gate:CiTimeoutMinutes"] = "8", ["Gate:TestTimeoutMinutes"] = "9", ["Review:TimeoutMinutes"] = "11",
            ["CodeGraph:OverlayTimeoutMinutes"] = "12",
        });
        var workspaces = new Git.GitWorkspace(Path.GetTempPath(), r => $"https://example.invalid/{r.FullName}", (_, _) => Task.FromResult<string?>(null));

        var gate = FactoryRunner.CreateGate(options, new GatePipelineTests.FakeGateGitHub(), new GatePipelineTests.FakeReviewer(),
            workspaces, slots: null);
        using var reviewer = FactoryRunner.ReviewerHttp(options);

        Assert.Equal((TimeSpan.FromSeconds(7), TimeSpan.FromMinutes(8)), (gate.CiPollInterval, gate.CiTimeout));
        Assert.Equal(TimeSpan.FromMinutes(9), Assert.IsType<SandboxTestRunner>(gate.Tests).Timeout);
        Assert.Equal((TimeSpan.FromMinutes(12), ReviewOverlays.DefaultPollInterval), (gate.Overlays!.Timeout, gate.Overlays.PollInterval));
        Assert.Equal(ReviewOverlays.DefaultTimeout, FactoryRunner.CreateGate(Options([]), new GatePipelineTests.FakeGateGitHub(),
            new GatePipelineTests.FakeReviewer(), workspaces, slots: null).Overlays!.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(11), reviewer.Timeout);
        // sc-25709: risk-threshold reads CodeGraph through the same client, its overlay wait on the same timeout; no client, no reader.
        Assert.Null(gate.Risk);
        using var codeGraphHttp = new HttpClient();
        var withCodeGraph = FactoryRunner.CreateGate(options, new GatePipelineTests.FakeGateGitHub(), new GatePipelineTests.FakeReviewer(),
            workspaces, slots: null, new CodeGraph.CodeGraphMcpClient(codeGraphHttp, "cg_test_token"));
        Assert.Equal(TimeSpan.FromMinutes(12), withCodeGraph.Risk!.Overlays.Timeout);
    }

    [Fact]
    public void The_other_settings_come_from_config_and_out_of_range_values_are_refused()
    {
        Assert.Equal(5000, Options(new() { ["Factory:HostPort"] = "5000" }).HostPort);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Factory:HostPort"] = "-1" }).HostPort);
        Assert.Contains("Factory:HostPort", Assert.Throws<InvalidOperationException>(() => Options(new() { ["Factory:HostPort"] = "70000" }).ValidateSettings()).Message);

        Assert.Equal(5, Options(new() { ["Intake:MaxItemFailures"] = "5" }).MaxItemFailures);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Intake:MaxItemFailures"] = "0" }).MaxItemFailures);

        // sc-25988: one run at a time unless configured; a value outside 1..8 is refused at start-up, naming the key.
        Assert.Equal(1, Options([]).MaxConcurrentRuns);
        Assert.Equal(3, Options(new() { ["Intake:MaxConcurrentRuns"] = "3" }).MaxConcurrentRuns);
        Assert.Equal(WorkSources.IntakeOptions.MaxConcurrentRunsLimit, Options(new() { ["Intake:MaxConcurrentRuns"] = "8" }).MaxConcurrentRuns);
        foreach (var refused in new[] { "0", "-1", "9" })
        {
            Assert.Contains("Intake:MaxConcurrentRuns must be a whole number from 1 to 8",
                Assert.Throws<InvalidOperationException>(() => Options(new() { ["Intake:MaxConcurrentRuns"] = refused }).ValidateSettings()).Message);
        }
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Intake:MaxConcurrentRuns"] = "two" }).ValidateSettings());

        Assert.Equal(DarkFactory.Orchestrator.RunPipeline.DefaultMaxControlReadFailures, Options([]).MaxControlReadFailures);
        Assert.Equal(4, Options(new() { ["Controls:MaxReadFailures"] = "4" }).MaxControlReadFailures);
        Assert.Contains("Controls:MaxReadFailures must be at least 1",
            Assert.Throws<InvalidOperationException>(() => Options(new() { ["Controls:MaxReadFailures"] = "0" }).MaxControlReadFailures).Message);

        Assert.Equal("acme/widgets", Options(new() { ["Factory:DefaultRepo"] = "acme/widgets" }).DefaultRepo.FullName);
        Assert.Contains("Factory:DefaultRepo", Assert.Throws<InvalidOperationException>(() => Options(new() { ["Factory:DefaultRepo"] = "nope" }).ValidateSettings()).Message);

        Assert.Equal("/opt/claude/bin/claude", Options(new() { ["Worker:ClaudePath"] = "/opt/claude/bin/claude" }).ClaudePath);
        Assert.Equal("Host=db;Database=f", Options(new() { ["ConnectionStrings:Ledger"] = "Host=db;Database=f" }).LedgerConnectionString);

        Assert.Equal(new Uri("http://router.local:9000/"), Options(new() { ["Router:BaseUrl"] = "http://router.local:9000" }).RouterBaseUrl);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Router:BaseUrl"] = "router.local" }).RouterBaseUrl);
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["Router:BaseUrl"] = "ftp://router.local" }).RouterBaseUrl);
        Options([]).ValidateSettings(); // the defaults pass
    }

    // P1-E1: the lint sees only the code; a model provider's host given through config is refused when the option is read.
    public static TheoryData<string> Providers => [.. Analyzers.GatewayAnalyzer.ProviderMarkers];

    [Theory]
    [MemberData(nameof(Providers))]
    public void A_router_base_url_naming_a_model_provider_is_refused(string marker)
    {
        var url = marker.Contains('.') ? $"https://{marker.ToUpperInvariant()}/v1" : $"http://router.local/?k={marker}";

        var refused = Assert.Throws<InvalidOperationException>(() => Options(new() { ["Router:BaseUrl"] = url }).RouterBaseUrl);

        Assert.Contains("Router:BaseUrl names a model provider", refused.Message);
        Assert.Contains("Router:BaseUrl", Assert.Throws<InvalidOperationException>(() => Options(new() { ["Router:BaseUrl"] = url }).ValidateSettings()).Message);
    }

    [Fact]
    public void The_orchestrator_reads_the_same_provider_list_as_the_gateway_lint()
    {
        Assert.Equal(Analyzers.GatewayAnalyzer.ProviderMarkers, Gateway.ProviderMarkers.All);
        Assert.True(Gateway.ProviderMarkers.All.Count >= 10);
    }

    [Fact]
    public async Task Factory_run_with_a_setting_out_of_range_exits_2_naming_the_key_before_anything_runs()
    {
        var calls = new List<string>();
        var stderr = new StringWriter();

        var exit = await FactoryRunner.RunCommandAsync(Options(new() { ["Gate:CiPollSeconds"] = "0", ["Review:Models"] = "claude-opus-5-5" }),
            _ => { calls.Add("enrollment"); return Task.FromResult<string?>(null); },
            _ => { calls.Add("run"); return Task.FromResult(new RunOutcome(1, DarkFactory.Orchestrator.Ledger.WorkState.Watch, null, null, null)); },
            stderr, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("Gate:CiPollSeconds must be more than 0", stderr.ToString());
        Assert.Empty(calls);
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

/// <summary>The router-key start-up check of <c>factory run</c>/<c>factory work</c> (<see cref="FactoryRunner.CheckRouterEnrollmentAsync"/>).</summary>
public class RouterEnrollmentCheckTests
{
    private static FactoryOptions Options(string? auth) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(auth is null ? [] : new Dictionary<string, string?> { ["Worker:Auth"] = auth }).Build(),
            new InMemorySecrets());

    /// <summary>The router's usage report (<c>GET /v1/subscriptions/usage</c>) with the given credential entries.</summary>
    private static FakeApi Router(string credentials) => new FakeApi().On("GET /v1/subscriptions/usage", HttpStatusCode.OK,
        $$"""{"as_of":"2026-10-07T12:00:00Z","all_exhausted":false,"known_credentials":0,"observed_credentials":0,"credentials":[{{credentials}}]}""");

    private static Task<string?> CheckAsync(FactoryOptions options, FakeApi api) =>
        FactoryRunner.CheckRouterEnrollmentAsync(options, new RouterClient(api.Client("http://localhost:8080/"), "rk_test"), CancellationToken.None);

    [Theory]
    [InlineData("""{"provider":"anthropic","source":"managed","credential_key":"a","routable":true,"enabled":true}""")]
    [InlineData("""{"provider":"openai","source":"managed","credential_key":"c","routable":true,"enabled":true}""")]
    [InlineData("""{"provider":"anthropic","source":"shared","credential_key":"s","routable":true,"enabled":true}""")]
    public async Task Router_key_mode_passes_when_the_router_lists_an_enrolled_plan_for_the_key(string credential)
    {
        var api = Router(credential);

        Assert.Null(await CheckAsync(Options(null), api)); // router-key is the default
        Assert.Equal("rk_test", api.Requests.Single().Headers[Worker.ClaudeWorker.RouterKeyHeader]);
    }

    [Theory]
    [InlineData("")] // nothing enrolled
    [InlineData("""{"provider":"anthropic","source":"presented","credential_key":"p","routable":true}""")] // a client's own login, not enrolled
    [InlineData("""{"provider":"anthropic","source":"observed","credential_key":"o","routable":true}""")]
    [InlineData("""{"provider":"anthropic","source":"managed","credential_key":"a","routable":false,"enabled":false}""")] // enrolled but disabled
    public async Task Router_key_mode_fails_fast_naming_the_enrollment_commands_when_no_plan_is_enrolled(string credential)
    {
        var error = await CheckAsync(Options("router-key"), Router(credential));

        Assert.NotNull(error);
        Assert.Contains("no enrolled", error);
        Assert.Contains("router login claude", error);
        Assert.Contains("router login codex", error);
    }

    [Fact]
    public async Task An_enabled_plan_the_router_cannot_route_onto_is_not_counted_and_fails_naming_the_reconnect()
    {
        const string credential = """{"provider":"claude","source":"managed","credential_key":"a","routable":false,"enabled":true,"state":"reconnect_required"}""";

        var error = await CheckAsync(Options("router-key"), Router(credential));

        Assert.NotNull(error);
        Assert.Contains("not routable (claude reconnect_required)", error);
        Assert.Contains("Reconnect the plan", error);
        Assert.Contains("router login claude", error);
        Assert.Contains("router login codex", error);
    }

    [Fact]
    public async Task Router_key_mode_fails_when_the_usage_report_cannot_be_read()
    {
        var error = await CheckAsync(Options("router-key"), new FakeApi()); // 404: no usage route

        Assert.NotNull(error);
        Assert.Contains("could not be read", error);
    }

    [Fact]
    public async Task Claude_login_mode_skips_the_check_without_calling_the_router()
    {
        var api = new FakeApi();

        Assert.Null(await CheckAsync(Options("claude-login"), api));
        Assert.Empty(api.Requests);
    }
}
