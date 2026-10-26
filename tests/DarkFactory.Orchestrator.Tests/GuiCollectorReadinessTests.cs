using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorReadinessTests
{
    const string Base = "1111111111111111111111111111111111111111";
    const string Head = "2222222222222222222222222222222222222222";
    const string Migration = "3333333333333333333333333333333333333333";
    const string Configuration = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const long Pull = 4, Runner = 77;
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
    static readonly string[] Jobs = ["inference", "live-raw", "cargo-test", "deterministic-fixtures-and-tour"];

    [Theory]
    [InlineData("failure")]
    [InlineData("success")]
    public async Task Post_promotion_readiness_reruns_all_four_initial_push_attempts_even_if_the_stale_pin_attempt_passed(string initialConclusion)
    {
        var fixture = new Fixture { InitialConclusion = initialConclusion };
        Assert.Null(fixture.Original.SourcePublisher); // The old default did not yet have the workflow_run publisher.
        var ready = await fixture.Run();
        Assert.True(ready.Complete);
        Assert.Equal(Migration, ready.MigrationSha);
        Assert.Equal(Pull, ready.MigrationPull);
        Assert.Equal(fixture.Original.PlanSha256, ready.PlanSha256);
        Assert.Equal(4, ready.Runs.Length);
        Assert.All(ready.Runs, r => { Assert.Equal(1, r.InitialAttempt); Assert.Equal(2, r.RerunAttempt); });
        Assert.Equal(4, fixture.Writes("rerun").Count());
        Assert.Empty(fixture.Writes("cancel"));
        Assert.All(fixture.Writes("rerun"), c => Assert.Contains(ready.Runs, r => c.Path == Fixture.Source + $"/actions/runs/{r.Id}/rerun"));
        Assert.Contains(fixture.Api.Calls, c => c.Path.StartsWith(Fixture.Source + "/commits/" + Migration + "/check-runs?", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Path.Contains("/commits/" + Head + "/check-runs"));
        Assert.True(fixture.Saves[^1].Complete);
        Assert.All(fixture.Saves.Take(fixture.Saves.Count - 1), s => Assert.False(s.Complete));
        Assert.All(fixture.Api.Calls, c => Assert.Equal("Bearer OWNER_ONLY", c.Authorization));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task Only_the_bound_initial_runs_are_cancelled_and_each_cancel_and_rerun_is_journaled_before_the_POST(string status)
    {
        var fixture = new Fixture { InitialStatus = status, IncludeUnrelated = true };
        var ready = await fixture.Run();
        Assert.True(ready.Complete);
        Assert.Equal(4, fixture.Writes("cancel").Count());
        Assert.Equal(4, fixture.Writes("rerun").Count());
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "POST" && c.Path.Contains("/999/"));
        foreach (var run in ready.Runs)
        {
            var cancel = fixture.Api.Calls.FindIndex(c => c.Method == "POST" && c.Path.EndsWith($"/{run.Id}/cancel", StringComparison.Ordinal));
            var rerun = fixture.Api.Calls.FindIndex(c => c.Method == "POST" && c.Path.EndsWith($"/{run.Id}/rerun", StringComparison.Ordinal));
            Assert.True(cancel >= 0 && rerun > cancel);
            Assert.Contains(fixture.Api.Calls.Skip(cancel + 1).Take(rerun - cancel - 1), c => c.Method == "GET" && c.Path.Contains($"/actions/runs/{run.Id}"));
        }
        // The fake POST handler also asserts the last durable save contains its exact run and write intent.
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("head-ref")]
    [InlineData("head-repo")]
    [InlineData("base-ref")]
    [InlineData("base-repo")]
    [InlineData("merge-sha")]
    [InlineData("admin")]
    [InlineData("bytes")]
    [InlineData("environment")]
    [InlineData("pin")]
    [InlineData("runner-offline")]
    [InlineData("runner-label")]
    [InlineData("runner-id")]
    public async Task Migration_authority_current_pin_and_promoted_runner_are_revalidated_before_any_run_write(string fault)
    {
        var fixture = new Fixture { Fault = fault };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method != "GET");
        Assert.DoesNotContain(fixture.Saves, s => s.Complete);
    }

    [Theory]
    [InlineData("run-sha")]
    [InlineData("run-path")]
    [InlineData("run-event")]
    [InlineData("run-repo")]
    [InlineData("run-branch")]
    [InlineData("run-id")]
    [InlineData("run-attempt")]
    [InlineData("duplicate")]
    public async Task A_workflow_listing_is_not_authority_to_act_on_a_wrong_or_ambiguous_push_run(string fault)
    {
        var fixture = new Fixture { Fault = fault };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.Empty(fixture.Writes("rerun"));
        Assert.Empty(fixture.Writes("cancel"));
        Assert.DoesNotContain(fixture.Saves, s => s.Complete);
    }

    [Theory]
    [InlineData("rerun-failed")]
    [InlineData("rerun-attempt")]
    [InlineData("rerun-id")]
    [InlineData("job-failed")]
    [InlineData("job-missing")]
    [InlineData("job-attempt")]
    [InlineData("job-head")]
    [InlineData("job-run")]
    [InlineData("job-duplicate")]
    [InlineData("job-unrelated")]
    [InlineData("source-context-failed")]
    [InlineData("pin-after-rerun")]
    [InlineData("controller-failed")]
    [InlineData("controller-sha")]
    public async Task Only_the_expected_rerun_attempt_its_real_job_and_aggregate_migration_CI_can_complete_readiness(string fault)
    {
        var fixture = new Fixture { Fault = fault };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.DoesNotContain(fixture.Saves, s => s.Complete);
        Assert.All(fixture.Writes("rerun").GroupBy(c => c.Path), group => Assert.Single(group));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("rerun-pending")]
    [InlineData("controller-pending")]
    [InlineData("controller-none")]
    [InlineData("source-context-missing")]
    [InlineData("source-context-pending")]
    public async Task Pending_or_absent_evidence_is_bounded_and_never_promoted_to_a_pass(string fault)
    {
        var fixture = new Fixture { Fault = fault };
        var pending = await fixture.Run(wait: TimeSpan.Zero);
        Assert.False(pending.Complete);
        Assert.DoesNotContain(fixture.Saves, s => s.Complete);
        var deadline = pending.Deadline;
        var writes = fixture.Api.Calls.Count(c => c.Method == "POST");
        var resumed = await fixture.Run(pending, TimeSpan.FromHours(1));
        Assert.False(resumed.Complete);
        Assert.Equal(deadline, resumed.Deadline);
        Assert.Equal(writes, fixture.Api.Calls.Count(c => c.Method == "POST"));
    }

    [Fact]
    public async Task An_uncertain_rerun_POST_is_reconciled_by_observation_never_blindly_repeated_on_resume()
    {
        var fixture = new Fixture { LoseRerunAnswer = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        var saved = fixture.Saves[^1];
        var first = Assert.Single(fixture.Writes("rerun"));
        Assert.Contains(saved.Runs, r => first.Path.EndsWith($"/{r.Id}/rerun", StringComparison.Ordinal) && r.RerunAttempt == r.InitialAttempt + 1);
        // GitHub accepted the POST but the caller never received the answer.
        fixture.LoseRerunAnswer = false;
        var resumed = await fixture.Run(saved);
        Assert.True(resumed.Complete);
        Assert.Single(fixture.Writes("rerun"), c => c.Path == first.Path);
        Assert.Equal(4, fixture.Writes("rerun").Count());
    }

    [Fact]
    public async Task An_uncertain_rerun_that_never_appears_stays_incomplete_without_another_POST()
    {
        var fixture = new Fixture { LoseRerunAnswer = true, AcceptLostRerun = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        var saved = fixture.Saves[^1];
        var first = Assert.Single(fixture.Writes("rerun"));
        fixture.LoseRerunAnswer = false;
        // Reuse an expired journal, not a fresh wait budget, so the test performs no real wait.
        var resumed = await fixture.Run(saved with { Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Assert.False(resumed.Complete);
        Assert.Equal(first, Assert.Single(fixture.Writes("rerun")));
    }

    [Fact]
    public async Task A_journal_cannot_substitute_an_unrelated_run_ID_for_its_observed_workflow()
    {
        var fixture = new Fixture { LoseRerunAnswer = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        var saved = fixture.Saves[^1];
        var runs = saved.Runs.Select((r, index) => index == 0 ? r with { Id = 999 } : r).ToArray();
        fixture.Api.Calls.Clear();
        fixture.LoseRerunAnswer = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(saved with { Runs = runs }));
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method != "GET");
    }

    [Fact]
    public async Task A_failed_journal_save_prevents_the_reserved_remote_write()
    {
        var fixture = new Fixture { RefuseIntentSave = true };
        await Assert.ThrowsAsync<IOException>(() => fixture.Run());
        Assert.Empty(fixture.Writes("rerun"));
        Assert.Empty(fixture.Writes("cancel"));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("pull")]
    [InlineData("sha")]
    public async Task A_journal_from_another_plan_pull_or_merge_cannot_authorize_resume(string changed)
    {
        var fixture = new Fixture { Fault = "controller-pending" };
        var saved = await fixture.Run(wait: TimeSpan.Zero);
        saved = changed switch {
            "plan" => saved with { PlanSha256 = new string('c', 64) },
            "pull" => saved with { MigrationPull = Pull + 1 },
            _ => saved with { MigrationSha = Head }
        };
        fixture.Api.Calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(saved));
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method != "GET");
    }

    [Fact]
    public void Migration_from_an_old_default_without_a_publisher_keeps_the_source_PR_request_helper_guarded()
    {
        var fixture = new Fixture();
        Assert.Null(fixture.Original.SourcePublisher);
        Assert.Null(fixture.Original.SourceHostedWorkflow);
        foreach (var workflow in fixture.Desired)
        {
            var yaml = new YamlDotNet.RepresentationModel.YamlStream();
            yaml.Load(new StringReader(workflow.Content));
            var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
            var jobs = (YamlDotNet.RepresentationModel.YamlMappingNode)root.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("jobs")];
            var helper = (YamlDotNet.RepresentationModel.YamlMappingNode)jobs.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("factory-source-request")];
            var guard = ((YamlDotNet.RepresentationModel.YamlScalarNode)helper.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("if")]).Value!;
            Assert.Contains("github.event_name == 'pull_request'", guard);
            Assert.Contains("github.event.pull_request.head.repo.full_name == github.repository", guard);
            Assert.Contains("github.event.pull_request.base.ref != 'main'", guard);
            // A migration PR to main cannot rely on a workflow_run publisher missing from the old default.
            // Its helper is deliberately skipped; the post-promotion PUSH reruns are the actual readiness proof.
            Assert.Contains("pull_request_target", workflow.Content);
        }
    }

    [Fact]
    public void The_real_CLI_pins_then_promotes_then_awaits_bootstrap_CI_before_writing_completion()
    {
        // Source guard only: this does not claim to execute the privileged installation path under a fake host.
        var source = File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "src", "DarkFactory.Orchestrator", "GuiCollectorCli.cs"));
        var pin = source.IndexOf("await gateway.PinInstalledConfigurationAsync(", StringComparison.Ordinal);
        var promote = source.IndexOf("await gateway.PromoteRunnerAsync(", StringComparison.Ordinal);
        var readiness = source.IndexOf("await gateway.ReconcileBootstrapCiAsync(", StringComparison.Ordinal);
        var incomplete = source.IndexOf("if (!readiness.Complete)", StringComparison.Ordinal);
        var completion = source.IndexOf("Save(completionPath,", StringComparison.Ordinal);
        Assert.True(pin >= 0 && promote > pin && readiness > promote && incomplete > readiness && completion > incomplete);
        Assert.Contains("prepared.BootstrapCi", source[readiness..incomplete]);
        Assert.Contains("Save(journal, prepared)", source[readiness..incomplete]);
        Assert.Contains("return 2;", source[incomplete..completion]);
        Assert.Contains("bootstrapCi = readiness", source[completion..]);
    }

    sealed class Fixture
    {
        public const string Source = "/repos/michaeltrefry/LensWorks";
        public readonly GuiCollectorDeployment.Evidence Original;
        public readonly GuiCollectorDeployment.Workflow[] Desired;
        public readonly Api Api;
        readonly GuiCollectorDeployment client;
        readonly Dictionary<string, string> definitions;
        readonly HashSet<long> cancelled = [], rerun = [];
        public readonly List<GuiCollectorDeployment.BootstrapReadiness> Saves = [];
        public string Fault = "", InitialStatus = "completed", InitialConclusion = "failure";
        public bool IncludeUnrelated, LoseRerunAnswer, RefuseIntentSave;
        public bool AcceptLostRerun = true;

        public Fixture()
        {
            var workflows = GuiCollectorDeployment.WorkflowPaths.Select((path, index) => {
                var text = OriginalText(Jobs[index]);
                return new GuiCollectorDeployment.Workflow(path, GuiCollectorDeployment.Hash(text), text);
            }).ToArray();
            Original = new(new string('a', 64), GuiCollectorDeployment.ControlRepository.FullName, Base, workflows, []) { DefaultBranch = "main" };
            Desired = workflows.Select(w => {
                var content = GuiSourceWorkflow.Migrate(w.Path, w.Content);
                return new GuiCollectorDeployment.Workflow(w.Path, GuiCollectorDeployment.Hash(content), content);
            }).ToArray();
            definitions = Desired.ToDictionary(w => w.Path, w => w.Content);
            definitions[GuiSourceWorkflow.PublisherPath] = GuiSourceWorkflow.Publisher(definitions);
            definitions[RemoteIosWorkflow.Path] = RemoteIosWorkflow.Text;
            Api = new(Answer);
            var http = new HttpClient(Api) { BaseAddress = new Uri("https://api.github.com/") };
            client = new(http, new GitHubApp(http, "12", Pem, TimeProvider.System), "OWNER_ONLY", TimeProvider.System);
        }

        public Task<GuiCollectorDeployment.BootstrapReadiness> Run(GuiCollectorDeployment.BootstrapReadiness? recorded = null, TimeSpan? wait = null) =>
            client.ReconcileBootstrapCiAsync(Original, Desired, Pull, Runner, Configuration, recorded, Save, CancellationToken.None,
                wait: wait ?? TimeSpan.FromSeconds(1), poll: TimeSpan.Zero);

        Task Save(GuiCollectorDeployment.BootstrapReadiness value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (RefuseIntentSave && value.Runs.Any(r => r.RerunAttempt is not null)) throw new IOException("simulated journal write failure");
            // Save a deep snapshot, as a real journal serializes before returning.
            Saves.Add(JsonSerializer.Deserialize<GuiCollectorDeployment.BootstrapReadiness>(JsonSerializer.Serialize(value))!);
            return Task.CompletedTask;
        }

        public IEnumerable<Api.Call> Writes(string action) => Api.Calls.Where(c => c.Method == "POST" && c.Path.EndsWith("/" + action, StringComparison.Ordinal));

        HttpResponseMessage Answer(string method, string path, string? body)
        {
            if (path == "/user") return Json(new { login = "michaeltrefry", type = "User" });
            if (path == Source) return Json(new { default_branch = "main", permissions = new { admin = true } });
            if (path == Source + "/pulls/4") return Json(new {
                state = "closed", merged = Fault != "merged", merge_commit_sha = Fault == "merge-sha" ? "" : Migration, changed_files = definitions.Count,
                head = new { sha = Head, @ref = Fault == "head-ref" ? "unrelated" : "factory/gui-collector-" + Original.PlanSha256[..16], repo = new { full_name = Fault == "head-repo" ? "outsider/fork" : GuiCollectorDeployment.SourceRepository.FullName } },
                @base = new { @ref = Fault == "base-ref" ? "other" : "main", repo = new { full_name = Fault == "base-repo" ? "other/repo" : GuiCollectorDeployment.SourceRepository.FullName } },
                user = new { login = "michaeltrefry", id = 1 }, merged_by = new { login = Fault == "admin" ? "outsider" : "michaeltrefry", type = "User" }
            });
            if (path.Contains("/collaborators/")) return Json(new { permission = "read" });
            if (path.StartsWith(Source + "/pulls/4/files?", StringComparison.Ordinal)) return Json(definitions.Keys.Select(filename => new { filename, status = GuiCollectorDeployment.WorkflowPaths.Contains(filename) ? "modified" : "added" }));
            if (path.StartsWith(Source + "/contents/", StringComparison.Ordinal))
            {
                var file = path[(Source.Length + "/contents/".Length)..].Split('?')[0];
                var atOldDefault = path.EndsWith("ref=" + Base, StringComparison.Ordinal);
                if (atOldDefault && !GuiCollectorDeployment.WorkflowPaths.Contains(file)) return new(HttpStatusCode.NotFound);
                var text = atOldDefault ? Original.Workflows.Single(w => w.Path == file).Content : definitions[file] + (Fault == "bytes" ? "\n# changed" : "");
                return Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });
            }
            if (path.StartsWith(Source + "/environments/", StringComparison.Ordinal))
            {
                if (path.Contains("/deployment-branch-policies")) return Json(new { total_count = 1, branch_policies = new[] { new { id = 1, name = "main", type = "branch" } } });
                if (path.EndsWith("/secrets/FACTORY_GUI_APP_PRIVATE_KEY", StringComparison.Ordinal)) return Json(new { name = "FACTORY_GUI_APP_PRIVATE_KEY" });
                if (path.EndsWith("/variables/FACTORY_GUI_APP_ID", StringComparison.Ordinal)) return Json(new { name = "FACTORY_GUI_APP_ID", value = "12" });
                if (path.EndsWith("/variables/FACTORY_GUI_CONFIGURATION_SHA256", StringComparison.Ordinal)) return Json(new { name = "FACTORY_GUI_CONFIGURATION_SHA256", value = Fault == "pin" || Fault == "pin-after-rerun" && rerun.Count == 4 ? new string('c', 64) : Configuration });
                return Json(new { deployment_branch_policy = new { protected_branches = false, custom_branch_policies = Fault != "environment" } });
            }
            var collector = "/repos/" + GuiCollectorDeployment.ControlRepository.FullName;
            if (path.StartsWith(collector + "/actions/runners", StringComparison.Ordinal))
            {
                var value = new { id = Fault == "runner-id" ? Runner + 1 : Runner, name = "gui-collector", status = Fault == "runner-offline" ? "offline" : "online",
                    labels = new[] { new { name = Fault == "runner-label" ? GuiCollectorDeployment.BootstrapLabel(Original.PlanSha256) : GuiCollectorDeployment.Label } } };
                return path.Contains("?") ? Json(new { total_count = 1, runners = new[] { value } }) : Json(value);
            }
            if (path.StartsWith(Source + "/actions/workflows/", StringComparison.Ordinal) && path.Contains("/runs?"))
            {
                var selector = Uri.UnescapeDataString(path[(Source.Length + "/actions/workflows/".Length)..].Split("/runs?")[0]);
                var index = Array.FindIndex(GuiCollectorDeployment.WorkflowPaths, w => w == selector || Path.GetFileName(w) == selector);
                Assert.InRange(index, 0, 3);
                var rows = Fault == "missing" ? new List<JsonObject>() : new List<JsonObject> { RunNode(index) };
                if (Fault == "duplicate") { var duplicate = RunNode(index); duplicate["id"] = 800 + index; rows.Add(duplicate); }
                if (IncludeUnrelated) { var unrelated = RunNode(index); unrelated["id"] = 999; unrelated["head_sha"] = Base; unrelated["event"] = "workflow_dispatch"; rows.Add(unrelated); }
                return Json(new { total_count = rows.Count, workflow_runs = rows });
            }
            if (path.StartsWith(Source + "/actions/runs/", StringComparison.Ordinal))
            {
                var pieces = path[(Source.Length + "/actions/runs/".Length)..].Split('?', 2)[0].Split('/');
                var id = long.Parse(pieces[0]);
                if (id == 999 && method == "GET")
                {
                    var unrelated = RunNode(0);
                    unrelated["id"] = 999;
                    unrelated["head_sha"] = Base;
                    unrelated["path"] = ".github/workflows/unrelated.yml";
                    return Json(unrelated);
                }
                var index = checked((int)(id - 100));
                Assert.InRange(index, 0, 3); // Any action on unrelated run 999 fails immediately.
                if (method == "POST")
                {
                    var reserved = Assert.Single(Saves[^1].Runs, r => r.Id == id);
                    Assert.NotEqual("observed", reserved.Phase);
                    Assert.False(Saves[^1].Complete);
                    if (pieces[^1] == "cancel") cancelled.Add(id);
                    else
                    {
                        Assert.Equal("rerun", pieces[^1]);
                        Assert.Equal(reserved.InitialAttempt + 1, reserved.RerunAttempt);
                        if (!LoseRerunAnswer || AcceptLostRerun) rerun.Add(id);
                        if (LoseRerunAnswer) return new(HttpStatusCode.BadGateway);
                    }
                    return new(HttpStatusCode.Accepted);
                }
                if (pieces[^1] == "jobs")
                {
                    var attempt = rerun.Contains(id) ? 2 : 1;
                    var rows = new List<object>();
                    if (Fault != "job-missing") rows.Add(new { id = 1000 + id, name = Jobs[index], run_id = Fault == "job-run" ? 999 : id, run_attempt = Fault == "job-attempt" ? 9 : attempt,
                        head_sha = Fault == "job-head" ? Head : Migration, status = "completed", conclusion = Fault == "job-failed" ? "failure" : "success" });
                    rows.Add(new { id = Fault == "job-duplicate" ? 1000 + id : 2000 + id, name = Fault == "job-unrelated" ? "unrelated" : "factory-source-request", run_id = id, run_attempt = attempt, head_sha = Migration, status = "completed", conclusion = "skipped" });
                    return Json(new { total_count = rows.Count, jobs = rows });
                }
                return Json(RunNode(index));
            }
            if (path.StartsWith(Source + "/commits/" + Migration + "/check-runs?", StringComparison.Ordinal))
            {
                var rows = Fault == "controller-none" ? Array.Empty<object>() : new object[] {
                    new { id = 600, name = "controller-contract", head_sha = Fault == "controller-sha" ? Head : Migration,
                        status = Fault == "controller-pending" ? "in_progress" : "completed", conclusion = Fault == "controller-failed" ? "failure" : "success" }
                };
                return Json(new { total_count = rows.Length, check_runs = rows });
            }
            if (path.StartsWith(Source + "/commits/" + Migration + "/status?", StringComparison.Ordinal))
            {
                var contexts = Jobs.Where((_, index) => Fault != "source-context-missing" || index != 0)
                    .Select((context, index) => new { id = 700 + index, context,
                        state = index == 0 && Fault == "source-context-pending" ? "pending" : index == 0 && Fault == "source-context-failed" ? "failure" : "success" }).ToArray();
                return Json(new { total_count = contexts.Length, statuses = contexts });
            }
            if (path.Contains("/check-suites?")) return Json(new { total_count = 0, check_suites = Array.Empty<object>() });
            throw new InvalidOperationException("Unexpected fake route " + method + " " + path);
        }

        JsonObject RunNode(int index)
        {
            var id = 100L + index;
            var retried = rerun.Contains(id);
            return JsonSerializer.SerializeToNode(new {
                id = Fault == "run-id" ? 0 : retried && Fault == "rerun-id" ? id + 500 : id,
                head_sha = Fault == "run-sha" ? Head : Migration,
                head_branch = Fault == "run-branch" ? "feature" : "main",
                @event = Fault == "run-event" ? "pull_request" : "push",
                path = Fault == "run-path" ? ".github/workflows/unrelated.yml" : GuiCollectorDeployment.WorkflowPaths[index],
                repository = new { full_name = Fault == "run-repo" ? "outsider/fork" : GuiCollectorDeployment.SourceRepository.FullName },
                head_repository = new { full_name = GuiCollectorDeployment.SourceRepository.FullName },
                run_attempt = Fault == "run-attempt" ? 0 : retried && Fault == "rerun-attempt" ? 3 : retried ? 2 : 1,
                status = retried ? Fault == "rerun-pending" ? "in_progress" : "completed" : cancelled.Contains(id) ? "completed" : InitialStatus,
                conclusion = retried ? Fault == "rerun-failed" ? "failure" : "success" : cancelled.Contains(id) ? "cancelled" : InitialConclusion
            })!.AsObject();
        }
    }

    static string OriginalText(string job) => $$$"""
        name: Source {{{job}}}
        on:
          pull_request: {}
          push: {branches: [main]}
        permissions: {contents: read}
        jobs:
          {{{job}}}:
            runs-on: [self-hosted, macOS, ARM64]
            timeout-minutes: 90
            steps:
              - uses: actions/checkout@v4
                with: {submodules: false}
              - name: Original source tests
                run: cargo test --locked
        """;

    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    sealed class Api(Func<string, string, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public sealed record Call(string Method, string Path, string? Body, string? Authorization);
        public readonly List<Call> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add(new(request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
            return answer(request.Method.Method, path, body);
        }
    }
}
