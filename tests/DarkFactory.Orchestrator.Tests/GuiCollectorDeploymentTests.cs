using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorDeploymentTests
{
    static readonly RepoRef Repo = GuiCollectorDeployment.ControlRepository;
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
    const string Name = "factory-gui-unique-12345678";
    const string Old = "lensworks-guest";
    const string Sha = "1111111111111111111111111111111111111111";
    static readonly byte[] Plan = Encoding.UTF8.GetBytes("""{"runnerName":"factory-gui-unique-12345678","configuration":{"collectorRepository":"michaeltrefry/dark-factory-gui-control"},"old":{"runnerId":7,"runnerName":"lensworks-guest"}}""");
    static GuiCollectorDeployment.Runner Replacement => new(9, Name, "online", [GuiCollectorDeployment.Label]);
    static GuiCollectorDeployment.Workflow[] Workflows => GuiCollectorDeployment.WorkflowPaths.Select(path => {
        var job = path.Split('/').Last() switch { "inference-adapter.yml" => "inference", "live-raw-acceptance.yml" => "live-raw",
            "rust-catalog-tests.yml" => "cargo-test", _ => "deterministic-fixtures-and-tour" };
        var content = GuiSourceWorkflow.Migrate(path, GuiSourceWorkflowTests.Original(job, 90, "false")
            .Replace("name: Existing source work", "name: " + job, StringComparison.Ordinal));
        return new GuiCollectorDeployment.Workflow(path, GuiCollectorDeployment.Hash(content), content);
    }).ToArray();
    static GuiCollectorDeployment.Workflow Publisher(GuiCollectorDeployment.Workflow[] workflows)
    {
        var content = GuiSourceWorkflow.Publisher(workflows.ToDictionary(w => w.Path, w => w.Content), Repo.FullName);
        return new(GuiSourceWorkflow.PublisherPath, GuiCollectorDeployment.Hash(content), content);
    }
    static GuiCollectorDeployment.Evidence Evidence => new(GuiCollectorDeployment.Hash(Plan), Repo.FullName, Sha, Workflows, [], [new(7, Old, "online", ["self-hosted"])]) {
        CollectorExists = true, CollectorPrivateOwnerOnly = true, AppInstallationVerified = true,
        CollectorDefaultBranch = "main", CollectorDefaultCommit = Sha,
        CollectorWorkflows = GuiCollectorDeployment.CollectorDefinitions, SourcePublisher = Publisher(Workflows),
        SourceHostedWorkflow = new(RemoteIosWorkflow.Path, GuiCollectorDeployment.Hash(RemoteIosWorkflow.Text), RemoteIosWorkflow.Text), DispatcherEnvironmentVerified = true
    };
    static Dictionary<string, string> Hashes => Workflows.ToDictionary(w => w.Path, w => w.Sha256);
    static GuiCollectorDeployment Client(FakeApi api)
    {
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") };
        return new(http, new GitHubApp(http, "12", Pem, new Clock()), "OWNER_SECRET", new Clock());
    }
    static HttpResponseMessage Answer(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    static object Runner(long id, string name, string status = "online", params string[] labels) => new { id, name, status, labels = labels.Select(name => new { name }) };
    static HttpResponseMessage Runners(params object[] runners) => Answer(new { total_count = runners.Length, runners });
    static HttpResponseMessage FileAnswer(string content, string sha = Sha) => Answer(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), sha });

    static FakeApi Inspection(string appPermission = "write", string installation = "write", bool admin = true) => new((method, path, _) => path switch {
        "/app" => Answer(new { permissions = new { actions = appPermission } }),
        "/repos/michaeltrefry/dark-factory-gui-control/installation" => Answer(new { permissions = new { actions = installation } }),
        "/repos/michaeltrefry/dark-factory-gui-control" => Answer(new { full_name = Repo.FullName, @private = true, fork = false,
            owner = new { login = Repo.Owner, type = "User" }, permissions = new { admin }, default_branch = "main" }),
        "/repos/michaeltrefry/dark-factory-gui-control/collaborators?affiliation=all&per_page=100" => Answer(Array.Empty<object>()),
        "/repos/michaeltrefry/LensWorks" => Answer(new { permissions = new { admin }, default_branch = "main" }),
        "/repos/michaeltrefry/LensWorks/commits/main" or "/repos/michaeltrefry/dark-factory-gui-control/commits/main" => Answer(new { sha = Sha }),
        _ when path.StartsWith("/repos/michaeltrefry/dark-factory-gui-control/contents/") => FileAnswer(GuiCollectorDeployment.CollectorDefinitions.Single(w => path.Contains(w.Path + "?")).Content),
        _ when path.StartsWith("/repos/michaeltrefry/LensWorks/contents/") => FileAnswer(path.Contains(GuiSourceWorkflow.PublisherPath) ? Publisher(Workflows).Content : path.Contains(RemoteIosWorkflow.Path) ? RemoteIosWorkflow.Text : Workflows.Single(w => path.Contains(w.Path + "?")).Content),
        "/repos/michaeltrefry/LensWorks/environments/factory-gui-dispatch" => Answer(new { deployment_branch_policy = new { protected_branches = false, custom_branch_policies = true } }),
        "/repos/michaeltrefry/LensWorks/environments/factory-gui-dispatch/deployment-branch-policies?per_page=100" => Answer(new { total_count = 1, branch_policies = new[] { new { name = "main", type = "branch" } } }),
        "/repos/michaeltrefry/LensWorks/environments/factory-gui-dispatch/secrets/FACTORY_GUI_APP_PRIVATE_KEY" => Answer(new { name = "FACTORY_GUI_APP_PRIVATE_KEY" }),
        "/repos/michaeltrefry/LensWorks/environments/factory-gui-dispatch/variables/FACTORY_GUI_APP_ID" => Answer(new { value = "12" }),
        _ when path.StartsWith("/repos/michaeltrefry/dark-factory-gui-control/actions/runners?") => Runners(),
        _ when path.StartsWith("/repos/michaeltrefry/LensWorks/actions/runners?") => Runners(Runner(7, Old, labels: ["self-hosted"])),
        _ => throw new InvalidOperationException("Unexpected API request " + method + " " + path)
    });

    [Theory]
    [InlineData("read", "write", true)]
    [InlineData("write", "read", true)]
    [InlineData("write", "write", false)]
    public async Task Authenticated_app_installation_and_owner_permissions_all_fail_closed_without_writes(string app, string installation, bool admin)
    {
        var api = Inspection(app, installation, admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(api).InspectAsync(Plan, CancellationToken.None));
        Assert.All(api.Calls, call => Assert.Equal("GET", call.Method));
        Assert.DoesNotContain(api.Calls, call => call.Path.Contains("registration-token"));
    }

    [Fact]
    public async Task Prepare_reads_the_exact_four_workflows_at_the_real_default_commit_without_any_mutation_or_token_mint()
    {
        var api = Inspection();
        var evidence = await Client(api).InspectAsync(Plan, CancellationToken.None);
        Assert.Equal(GuiCollectorDeployment.Hash(Plan), evidence.PlanSha256);
        Assert.Equal(Sha, evidence.DefaultCommit);
        Assert.Equal(GuiCollectorDeployment.WorkflowPaths, evidence.Workflows.Select(w => w.Path));
        Assert.All(api.Calls, call => Assert.Equal("GET", call.Method));
        var files = api.Calls.Where(c => c.Path.Contains("/contents/")).ToArray();
        Assert.Equal(8, files.Length);
        Assert.Equal(RemoteIosWorkflow.Text, evidence.SourceHostedWorkflow!.Content);
        Assert.True(evidence.CollectorPrivateOwnerOnly);
        Assert.True(evidence.AppInstallationVerified);
        Assert.True(evidence.DispatcherEnvironmentVerified);
        Assert.Equal(Publisher(Workflows), evidence.SourcePublisher);
        Assert.Equal(GuiCollectorDeployment.CollectorDefinitions, evidence.CollectorWorkflows);
        Assert.All(files, file => Assert.EndsWith("?ref=" + Sha, file.Path));
        Assert.All(api.Calls.Where(c => c.Path.EndsWith("/installation") || c.Path == "/app"), c => {
            Assert.StartsWith("Bearer ey", c.Authorization);
            Assert.DoesNotContain("OWNER_SECRET", c.Authorization!);
        });
    }

    [Fact]
    public void Receipt_authority_never_accepts_missing_changed_or_structurally_unsafe_migration_evidence()
    {
        var client = Client(Inspection());
        var authority = client.Authorize(Evidence, Name, Hashes);
        Assert.Equal(4, authority.MigratedWorkflows.Length);
        // Exact source hashes alone cannot authorize a collector without fresh private infrastructure proof.
        var unsafeTopology = Assert.Throws<InvalidOperationException>(() => client.Authorize(Evidence with { CollectorPrivateOwnerOnly = false }, Name, Hashes));
        Assert.Contains("private collector", unsafeTopology.Message);
        var missing = Hashes;
        missing.Remove(GuiCollectorDeployment.WorkflowPaths[0]);
        Assert.Throws<InvalidOperationException>(() => client.Authorize(Evidence, Name, missing));
        var changed = Hashes;
        changed[GuiCollectorDeployment.WorkflowPaths[0]] = new string('a', 64);
        Assert.Throws<InvalidOperationException>(() => client.Authorize(Evidence, Name, changed));
        Assert.DoesNotContain("OWNER_SECRET", unsafeTopology.Message);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("private")]
    [InlineData("installation")]
    [InlineData("environment")]
    [InlineData("definitions")]
    [InlineData("publisher-missing")]
    [InlineData("publisher-content")]
    [InlineData("publisher-hash")]
    [InlineData("hosted-missing")]
    [InlineData("hosted-content")]
    public void Fresh_authority_requires_every_private_control_and_publisher_proof(string missing)
    {
        var client = Client(Inspection());
        var valid = Evidence;
        Assert.Equal(4, client.Authorize(valid, Name, Hashes).MigratedWorkflows.Length);
        var invalid = missing switch {
            "exists" => valid with { CollectorExists = false },
            "private" => valid with { CollectorPrivateOwnerOnly = false },
            "installation" => valid with { AppInstallationVerified = false },
            "environment" => valid with { DispatcherEnvironmentVerified = false },
            "definitions" => valid with { CollectorWorkflows = [] },
            "publisher-missing" => valid with { SourcePublisher = null },
            "hosted-missing" => valid with { SourceHostedWorkflow = null },
            "hosted-content" => valid with { SourceHostedWorkflow = valid.SourceHostedWorkflow! with { Content = "changed" } },
            "publisher-content" => valid with { SourcePublisher = valid.SourcePublisher! with { Content = valid.SourcePublisher!.Content + "# changed" } },
            _ => valid with { SourcePublisher = valid.SourcePublisher! with { Sha256 = new string('0', 64) } }
        };
        Assert.Throws<InvalidOperationException>(() => client.Authorize(invalid, Name, Hashes));
    }

    [Fact]
    public void An_existing_name_requires_this_plans_initial_absence_reservation_and_the_online_proof_has_only_custom_label()
    {
        var client = Client(Inspection());
        var current = Evidence with { Runners = [Replacement] };
        Assert.Throws<InvalidOperationException>(() => client.Authorize(current, Name, Hashes));
        Assert.True(client.Authorize(current, Name, Hashes, reservation: Evidence).NewNameAbsent);
        Assert.Throws<InvalidOperationException>(() => client.Authorize(current, Name, Hashes, reservation: Evidence with { PlanSha256 = "other" }));
        Assert.Throws<InvalidOperationException>(() => client.Authorize(current, Name, Hashes, Replacement with { Labels = ["self-hosted", GuiCollectorDeployment.Label] }));
        Assert.Throws<InvalidOperationException>(() => client.Authorize(current, Name, Hashes, Replacement with { Status = "offline" }));
    }

    [Fact]
    public async Task Replacement_is_authenticated_online_before_only_the_exact_old_id_is_removed_and_a_repeat_is_read_only()
    {
        bool removed = false;
        var api = new FakeApi((method, path, _) => {
            if (method == "GET") return removed ? Runners(Runner(9, Name, labels: [GuiCollectorDeployment.Label]))
                : Runners(Runner(9, Name, labels: [GuiCollectorDeployment.Label]), Runner(7, Old));
            Assert.Equal("DELETE", method);
            Assert.Equal("/repos/michaeltrefry/dark-factory-gui-control/actions/runners/7", path);
            removed = true;
            return new(HttpStatusCode.NoContent);
        });
        var client = Client(api);
        Assert.Equal(7, await client.RetireAsync(Repo, Replacement, 7, Old, CancellationToken.None));
        Assert.Equal("GET", api.Calls[0].Method);
        Assert.Equal("DELETE", api.Calls[1].Method);
        Assert.Equal("GET", api.Calls[2].Method);
        Assert.Equal(7, await client.RetireAsync(Repo, Replacement, 7, Old, CancellationToken.None));
        Assert.Single(api.Calls, c => c.Method == "DELETE");
    }

    [Fact]
    public async Task A_central_collector_replacement_does_not_delete_an_unrelated_registration_from_its_own_repository()
    {
        var deleted = false;
        var api = new FakeApi((method, path, _) => {
            if (path.StartsWith("/repos/michaeltrefry/dark-factory-gui-control/")) {
                Assert.Equal("GET", method);
                return Runners(Runner(9, Name, labels: [GuiCollectorDeployment.Label]));
            }
            Assert.StartsWith("/repos/michaeltrefry/LensWorks/", path);
            if (method == "DELETE") { Assert.EndsWith("/actions/runners/7", path); deleted = true; return new(HttpStatusCode.NoContent); }
            return deleted ? Runners() : Runners(Runner(7, Old));
        });
        Assert.Equal(7, await Client(api).RetireAsync(Repo, Replacement, 7, Old, CancellationToken.None, GuiCollectorDeployment.SourceRepository));
        Assert.Equal("GET", api.Calls[0].Method);
        Assert.StartsWith("/repos/michaeltrefry/dark-factory-gui-control/", api.Calls[0].Path);
        Assert.Single(api.Calls, c => c.Method == "DELETE");
    }

    [Theory]
    [InlineData("offline", "lensworks-guest", "factory-gui-collector")]
    [InlineData("online", "other-old-name", "factory-gui-collector")]
    [InlineData("online", "lensworks-guest", "self-hosted")]
    public async Task Changed_online_label_or_old_inventory_proof_never_deletes_any_registration(string status, string oldName, string label)
    {
        var api = new FakeApi((_, _, _) => Runners(Runner(9, Name, status, label), Runner(7, oldName)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(api).RetireAsync(Repo, Replacement, 7, Old, CancellationToken.None));
        Assert.DoesNotContain(api.Calls, c => c.Method == "DELETE");
    }

    [Fact]
    public async Task Lost_delete_answer_is_reconciled_by_a_get_never_by_deleting_a_name_or_another_id()
    {
        var deleted = false;
        var api = new FakeApi((method, _, _) => {
            if (method == "DELETE") { deleted = true; throw new HttpRequestException("answer lost"); }
            return deleted ? Runners(Runner(9, Name, labels: [GuiCollectorDeployment.Label]))
                : Runners(Runner(9, Name, labels: [GuiCollectorDeployment.Label]), Runner(7, Old));
        });
        Assert.Equal(7, await Client(api).RetireAsync(Repo, Replacement, 7, Old, CancellationToken.None));
        Assert.Single(api.Calls, c => c.Method == "DELETE");
        Assert.Equal(3, api.Calls.Count(c => c.Method == "GET"));
    }

    [Fact]
    public async Task Registration_secret_is_never_in_process_arguments_or_environment_and_error_bodies_are_not_exposed()
    {
        var api = new FakeApi((_, _, _) => Answer(new { token = "REGISTRATION_SECRET", expires_at = Now.AddMinutes(59).ToString("O") }));
        var token = await Client(api).RegistrationTokenAsync(Repo, CancellationToken.None);
        Assert.Equal("REGISTRATION_SECRET", token);
        var start = GuiCollectorDeployment.InstallerStartInfo("/reviewed/checkout", "/private/deploy/plan.json", "/private/deploy/authority.json");
        Assert.True(start.RedirectStandardInput);
        Assert.DoesNotContain(token, string.Join(" ", start.ArgumentList));
        Assert.DoesNotContain("--token", start.ArgumentList);
        Assert.DoesNotContain("-S", start.ArgumentList);
        Assert.Equal(["PATH"], start.Environment.Keys);
        var refused = new FakeApi((_, _, _) => new(HttpStatusCode.Forbidden) { Content = new StringContent(token) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(refused).RegistrationTokenAsync(Repo, CancellationToken.None));
        Assert.DoesNotContain(token, error.Message);
    }

    [Fact]
    public async Task Partial_workflow_update_resumes_without_repeating_successful_updates_or_opening_multiple_prs()
    {
        var contents = Workflows.ToDictionary(w => w.Path, w => w.Content);
        var original = Evidence with { SourcePublisher = null, SourceHostedWorkflow = null };
        var desired = Workflows.Select(w => {
            var planJob = w.Path.Split('/').Last() switch { "inference-adapter.yml" => "inference", "live-raw-acceptance.yml" => "live-raw",
                "rust-catalog-tests.yml" => "cargo-test", _ => "deterministic-fixtures-and-tour" };
            var content = GuiSourceWorkflow.Migrate(w.Path, GuiSourceWorkflowTests.Original(planJob, 90, "false")
                .Replace("name: Existing source work", "name: " + planJob, StringComparison.Ordinal)
                .Replace("cargo test --locked", "cargo test --locked --all", StringComparison.Ordinal));
            return w with { Content = content, Sha256 = GuiCollectorDeployment.Hash(content) };
        }).ToArray();
        var fail = true;
        var updates = new List<string>();
        var api = new FakeApi((method, path, body) => {
            if (path.Contains("/git/matching-refs/")) return Answer(new[] { new { @ref = "refs/heads/factory/gui-collector-" + Evidence.PlanSha256[..16] } });
            if (path.Contains("/contents/"))
            {
                var file = path.Split("/contents/")[1].Split('?')[0];
                if (method == "GET") return contents.TryGetValue(file, out var content) ? FileAnswer(content) : new(HttpStatusCode.NotFound);
                if (file == GuiCollectorDeployment.WorkflowPaths[1] && fail) { fail = false; return new(HttpStatusCode.ServiceUnavailable); }
                contents[file] = Encoding.UTF8.GetString(Convert.FromBase64String(JsonNode.Parse(body!)!["content"]!.GetValue<string>()));
                updates.Add(file);
                return Answer(new { });
            }
            if (path.Contains("/compare/")) return Answer(new { files = GuiCollectorDeployment.WorkflowPaths.Append(GuiSourceWorkflow.PublisherPath).Append(RemoteIosWorkflow.Path)
                .Select(filename => new { filename, status = filename == GuiSourceWorkflow.PublisherPath || filename == RemoteIosWorkflow.Path ? "added" : "modified" }) });
            if (path == "/repos/michaeltrefry/LensWorks") return Answer(new { default_branch = "main" });
            if (method == "GET" && path.Contains("/pulls?")) return Answer(new[] { new { number = 44 } });
            throw new InvalidOperationException("Unexpected migration API " + path);
        });
        var client = Client(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.MigrationPullRequestAsync(original, desired, CancellationToken.None));
        Assert.Equal(44, await client.MigrationPullRequestAsync(original, desired, CancellationToken.None));
        Assert.Equal(6, updates.Count);
        Assert.Equal(6, updates.Distinct().Count());
        Assert.Equal(RemoteIosWorkflow.Text, contents[RemoteIosWorkflow.Path]);
        Assert.Equal(Publisher(desired).Content, contents[GuiSourceWorkflow.PublisherPath]);
        Assert.DoesNotContain(api.Calls, c => c.Path.Contains("/merge") || c.Path.EndsWith("/reviews"));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task Migration_authority_requires_merged_exact_known_file_inventory_owner_authorized_merge_and_exact_hashes(
        bool merged, bool exactInventory, bool approved, bool matchingHashes)
    {
        var api = new FakeApi((_, path, _) => {
            if (path.EndsWith("/pulls/44")) return Answer(new { merged, changed_files = 6,
                @base = new { repo = new { full_name = GuiCollectorDeployment.SourceRepository.FullName } },
                head = new { sha = Sha }, merged_by = new { login = approved ? "michaeltrefry" : "outsider", type = "User" } });
            if (path.Contains("/files?")) return Answer(GuiCollectorDeployment.WorkflowPaths.Append(GuiSourceWorkflow.PublisherPath).Append(RemoteIosWorkflow.Path).Select((filename, i) => new {
                filename = i == 0 && !exactInventory ? "unrelated-source.cs" : filename,
                status = filename == GuiSourceWorkflow.PublisherPath || filename == RemoteIosWorkflow.Path ? "added" : "modified" }));
            if (path.EndsWith("/collaborators/outsider/permission")) return Answer(new { permission = "read" });
            if (path.Contains("/contents/")) {
                var file = path.Split("/contents/")[1].Split('?')[0];
                return FileAnswer((file == GuiSourceWorkflow.PublisherPath ? Publisher(Workflows).Content : file == RemoteIosWorkflow.Path ? RemoteIosWorkflow.Text : Workflows.Single(w => w.Path == file).Content) + (matchingHashes ? "" : " changed"));
            }
            throw new InvalidOperationException("Unexpected proof API " + path);
        });
        if (merged && exactInventory && approved && matchingHashes)
            await Client(api).VerifyReviewedMigrationAsync(GuiCollectorDeployment.SourceRepository, 44, Workflows, CancellationToken.None);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Client(api).VerifyReviewedMigrationAsync(GuiCollectorDeployment.SourceRepository, 44, Workflows, CancellationToken.None));
        Assert.All(api.Calls, c => Assert.Equal("GET", c.Method));
        Assert.DoesNotContain(api.Calls, c => c.Path.Contains("/reviews"));
    }

    [Fact]
    public void Explicit_root_inventory_is_a_distinct_read_only_mode_without_any_authority_or_registration_token_argument()
    {
        var start = GuiCollectorDeployment.InventoryStartInfo("/reviewed/checkout", "/Users/lwrunner/actions-runner");
        Assert.Equal("/usr/bin/sudo", start.FileName);
        Assert.Contains("--inventory", start.ArgumentList);
        Assert.DoesNotContain("--apply", start.ArgumentList);
        Assert.DoesNotContain("--authority", start.ArgumentList);
        Assert.DoesNotContain("--token", start.ArgumentList);
        Assert.Equal(["PATH"], start.Environment.Keys);
        Assert.Equal("/", start.WorkingDirectory);
    }

    [Fact]
    public async Task Runner_inventory_cannot_claim_completeness_with_duplicate_ids()
    {
        var api = new FakeApi((_, _, _) => Runners(Runner(7, Old), Runner(7, "duplicate")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(api).RunnersAsync(Repo, CancellationToken.None));
        Assert.All(api.Calls, c => Assert.Equal("GET", c.Method));
    }

    [Fact]
    public async Task Expired_registration_token_is_never_delivered_to_an_installer()
    {
        var api = new FakeApi((_, _, _) => Answer(new { token = "REGISTRATION_SECRET", expires_at = Now.AddSeconds(-1).ToString("O") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(api).RegistrationTokenAsync(Repo, CancellationToken.None));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("apply")]
    [InlineData("resume")]
    public async Task Owner_cli_has_explicit_separate_read_only_prepare_and_mutating_apply_resume(string action)
    {
        GuiCollectorCli.Request? received = null;
        var root = new RootCommand { GuiCollectorCli.Build((request, _) => { received = request; return Task.FromResult(0); }) };
        Assert.Equal(0, await root.Parse(["gui-collector", action, "--directory", "/private/deploy"]).InvokeAsync());
        Assert.Equal(action, received!.Action);
        Assert.Equal("/private/deploy", received.Directory);
        Assert.Equal("/Users/lwrunner/actions-runner", received.OldRoot);
    }

    [Fact]
    public async Task Installation_engine_records_registration_then_authenticates_online_then_protects_backup_before_exact_remote_delete_and_local_retirement()
    {
        var fixture = new InstallationFixture();
        var complete = await fixture.Run();
        Assert.Equal("complete", complete["status"]!.GetValue<string>());
        Assert.Equal(new[] { "token", "register", "record-id", "online-get", "backup", "delete-old-7", "retire" }, fixture.Events);
        Assert.Equal(9, fixture.KnownId);
        Assert.Single(fixture.Api.Calls, c => c.Method == "DELETE");
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Path.Contains("/merge") || c.Path.Contains("/reviews"));
    }

    [Fact]
    public async Task Installation_engine_without_authenticated_online_replacement_never_backs_up_or_deletes_old_registration()
    {
        var fixture = new InstallationFixture { Online = false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.Contains("old registration untouched", error.Message);
        Assert.Equal(new[] { "token", "register", "record-id", "online-get" }, fixture.Events);
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "DELETE");
        Assert.Equal(9, fixture.KnownId);
    }

    [Fact]
    public async Task Installation_engine_refuses_unprotected_backup_before_deleting_any_old_registration()
    {
        var fixture = new InstallationFixture { ProtectedBackup = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.Contains("backup", fixture.Events);
        Assert.DoesNotContain("delete-old-7", fixture.Events);
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "DELETE");
    }

    [Fact]
    public async Task Installation_engine_resumes_after_remote_delete_without_new_registration_token_or_repeating_the_delete()
    {
        var fixture = new InstallationFixture { CompleteRetirement = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.True(fixture.Removed);
        Assert.Equal(9, fixture.KnownId);
        fixture.CompleteRetirement = true;
        var complete = await fixture.Run();
        Assert.Equal("complete", complete["status"]!.GetValue<string>());
        Assert.Single(fixture.Events, e => e == "token");
        Assert.Single(fixture.Events, e => e == "delete-old-7");
        Assert.Equal(2, fixture.Events.Count(e => e == "retire"));
        Assert.Equal(2, fixture.Events.Count(e => e == "record-id"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("plan")]
    [InlineData("runner")]
    [InlineData("stage")]
    public void Final_label_online_authority_requires_a_plan_and_registration_bound_root_retirement_receipt(string mismatch)
    {
        var evidence = Evidence with { Runners = [Replacement], OldRunners = [] };
        var receipt = new JsonObject { ["version"] = 1, ["planSha256"] = evidence.PlanSha256,
            ["runnerName"] = Name, ["runnerId"] = 9L, ["stages"] = new JsonArray("old-retired") };
        var client = Client(Inspection());
        Assert.NotNull(client.Authorize(evidence, Name, Hashes, Replacement, retiredReceipt: receipt).Online);
        if (mismatch == "plan") receipt["planSha256"] = new string('0', 64);
        if (mismatch == "runner") receipt["runnerId"] = 10L;
        if (mismatch == "stage") receipt["stages"] = new JsonArray("old-backup-protected");
        Assert.Throws<InvalidOperationException>(() => client.Authorize(evidence, Name, Hashes, Replacement,
            retiredReceipt: mismatch == "missing" ? null : receipt));
    }

    [Fact]
    public async Task Final_job_label_is_withheld_until_root_retirement_and_verified_configuration_pin()
    {
        var fixture = new InstallationFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes,
            fixture.Receipt("awaiting-owner-online-verification-old-untouched"), CancellationToken.None));
        Assert.Empty(fixture.Events);
        var complete = await fixture.Run();
        Assert.True(fixture.RootRetired);
        Assert.False(fixture.FinalLabel);
        fixture.ConfigurationPin = new string('d', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes, complete, CancellationToken.None));
        Assert.DoesNotContain("promote", fixture.Events);
        fixture.ConfigurationPin = new string('c', 64);
        await Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes, complete, CancellationToken.None);
        Assert.True(fixture.FinalLabel);
        Assert.True(fixture.Events.IndexOf("retire") < fixture.Events.IndexOf("promote"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_lost_label_promotion_resumes_without_reregistering_or_deleting_old_again(bool lostAnswer)
    {
        var fixture = new InstallationFixture { FailPromotion = !lostAnswer, LostPromotionAnswer = lostAnswer };
        var complete = await fixture.Run();
        if (lostAnswer) await Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes, complete, CancellationToken.None);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes, complete, CancellationToken.None));
        fixture.FailPromotion = false;
        fixture.LostPromotionAnswer = false;
        await Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes, complete, CancellationToken.None);
        await Client(fixture.Api).PromoteRunnerAsync(fixture.PlanBytes, complete, CancellationToken.None);
        Assert.True(fixture.FinalLabel);
        Assert.Single(fixture.Events, e => e == "token");
        Assert.Single(fixture.Events, e => e == "delete-old-7");
        Assert.Equal(lostAnswer ? 1 : 2, fixture.Events.Count(e => e == "promote"));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("planSha256")]
    [InlineData("runnerName")]
    [InlineData("runnerId")]
    [InlineData("uid")]
    [InlineData("helperSha256")]
    [InlineData("sourceJobSha256")]
    [InlineData("configurationSha256")]
    public void Protected_installer_receipt_must_bind_exact_plan_identity_uid_and_payloads(string changed)
    {
        var fixture = new InstallationFixture();
        var receipt = fixture.Receipt("complete");
        Assert.Equal(9, GuiCollectorDeployment.VerifyInstallerReceipt(receipt, fixture.PlanObject, GuiCollectorDeployment.Hash(fixture.PlanBytes), 9));
        receipt[changed] = changed switch {
            "version" => JsonValue.Create(2), "runnerId" => JsonValue.Create(10L), "uid" => JsonValue.Create(502),
            "configurationSha256" => JsonValue.Create("not-a-sha256"), _ => JsonValue.Create("wrong")
        };
        Assert.Throws<InvalidOperationException>(() => GuiCollectorDeployment.VerifyInstallerReceipt(receipt,
            fixture.PlanObject, GuiCollectorDeployment.Hash(fixture.PlanBytes), 9));
    }

    [Fact]
    public async Task A_receipt_for_another_plan_never_checkpoints_registration_or_advances_to_old_backup()
    {
        var fixture = new InstallationFixture { WrongPlanReceipt = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.Null(fixture.KnownId);
        Assert.Equal(new[] { "token", "register" }, fixture.Events);
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "DELETE");
    }

    [Theory]
    [InlineData("reserved")]
    [InlineData("unreleased")]
    public async Task A_root_verified_prelaunch_crash_gets_one_fresh_plan_and_nonce_bound_absence_retry(string state)
    {
        var fixture = new InstallationFixture { ChallengeFirst = true, ChallengeState = state };
        var complete = await fixture.Run();
        Assert.Equal("complete", complete["status"]!.GetValue<string>());
        Assert.Equal(1, fixture.Challenges);
        Assert.Equal(2, fixture.Events.Count(e => e == "token"));
        Assert.True(fixture.Events.IndexOf("challenge") < fixture.Events.IndexOf("absence-get"));
        Assert.True(fixture.Events.IndexOf("absence-get") < fixture.Events.IndexOf("register"));
        Assert.Single(fixture.Events, e => e == "delete-old-7");
        Assert.NotNull(fixture.Absence);
        Assert.Equal(new string('d', 32), fixture.Absence.AttemptNonce);
        Assert.Equal(Name, fixture.Absence.RunnerName);
        Assert.Equal("absent", fixture.Absence.Status);
        Assert.Equal(Now.ToUnixTimeMilliseconds() / 1000.0, fixture.Absence.CheckedAt);
        Assert.True(fixture.Absence.AppPermissionsVerified);
    }

    [Fact]
    public async Task A_registration_seen_after_the_root_challenge_is_never_treated_as_absent_or_retried()
    {
        var fixture = new InstallationFixture { ChallengeFirst = true, ConflictAfterChallenge = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.Equal(1, fixture.Challenges);
        Assert.Single(fixture.Events, e => e == "token");
        Assert.DoesNotContain("register", fixture.Events);
        Assert.DoesNotContain("delete-old-7", fixture.Events);
    }

    [Fact]
    public async Task A_repeated_unstarted_challenge_is_bounded_to_one_automatic_gateway_retry()
    {
        var fixture = new InstallationFixture { ChallengeFirst = true, ChallengeAgain = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run());
        Assert.Equal(2, fixture.Challenges);
        Assert.Equal(2, fixture.Events.Count(e => e == "token"));
        Assert.DoesNotContain("register", fixture.Events);
        Assert.DoesNotContain("delete-old-7", fixture.Events);
    }

    [Theory]
    [InlineData("released")]
    [InlineData("unverified")]
    [InlineData("reserved-pid")]
    [InlineData("unreleased-no-identity")]
    [InlineData("wrong-plan")]
    [InlineData("wrong-name")]
    [InlineData("bad-nonce")]
    [InlineData("exhausted")]
    [InlineData("expired")]
    [InlineData("future-intent")]
    public async Task Ambiguous_POSTs_and_unbound_or_expired_root_intents_never_get_a_remote_absence_authority(string corrupt)
    {
        var fixture = new InstallationFixture();
        var challenge = fixture.Challenge();
        var intent = challenge["registrationIntent"]!.AsObject();
        switch (corrupt)
        {
            case "released": intent["state"] = "released"; break;
            case "unverified": intent["localUnstartedVerified"] = false; break;
            case "reserved-pid": intent["pid"] = 123; break;
            case "unreleased-no-identity": intent["state"] = "unreleased"; intent["pid"] = 123; break;
            case "wrong-plan": challenge["planSha256"] = new string('0', 64); break;
            case "wrong-name": challenge["runnerName"] = "factory-gui-other"; break;
            case "bad-nonce": intent["attemptNonce"] = "not-a-nonce"; break;
            case "exhausted": challenge["attempts"] = 2; break;
            case "expired": challenge["firstStartedAt"] = Now.AddHours(-2).ToUnixTimeMilliseconds() / 1000.0; break;
            case "future-intent": intent["createdAt"] = Now.AddSeconds(1).ToUnixTimeMilliseconds() / 1000.0; break;
        }
        var reservation = Evidence with { PlanSha256 = GuiCollectorDeployment.Hash(fixture.PlanBytes) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(fixture.Api).RegistrationAbsenceAsync(fixture.PlanBytes, reservation, Hashes, challenge, CancellationToken.None));
        Assert.Empty(fixture.Api.Calls);
    }

    sealed class InstallationFixture
    {
        public readonly JsonObject PlanObject;
        public readonly byte[] PlanBytes;
        public readonly FakeApi Api;
        public readonly List<string> Events = [];
        public long? KnownId;
        public bool Online = true, ProtectedBackup = true, CompleteRetirement = true, WrongPlanReceipt;
        public bool Removed, RootRetired, FinalLabel, FailPromotion, LostPromotionAnswer;
        public string ConfigurationPin = new('c', 64);
        bool registered;
        bool onlineListed;
        public bool ChallengeFirst, ChallengeAgain, ConflictAfterChallenge;
        public int Challenges;
        public string ChallengeState = "reserved";
        public GuiCollectorDeployment.RegistrationAbsenceProof? Absence;
        public InstallationFixture()
        {
            PlanObject = JsonNode.Parse(Plan)!.AsObject();
            PlanObject["uid"] = 503;
            PlanObject["helperSha256"] = new string('a', 64);
            PlanObject["sourceJobSha256"] = new string('b', 64);
            PlanBytes = Encoding.UTF8.GetBytes(PlanObject.ToJsonString());
            var inspection = Inspection();
            Api = new FakeApi((method, path, body) => {
                if (path == "/repos/michaeltrefry/dark-factory-gui-control/actions/runners/registration-token") {
                    Assert.Equal("POST", method); Events.Add("token");
                    return Answer(new { token = "REGISTRATION_SECRET", expires_at = Now.AddMinutes(59).ToString("O") });
                }
                if (path == "/repos/michaeltrefry/dark-factory-gui-control/actions/runners/9/labels") {
                    Assert.Equal("PUT", method); Assert.True(RootRetired); Assert.True(Removed);
                    Assert.Equal(new[] { GuiCollectorDeployment.Label }, JsonNode.Parse(body!)!["labels"]!.AsArray().Select(n => n!.GetValue<string>()));
                    Events.Add("promote");
                    if (FailPromotion) return new(HttpStatusCode.ServiceUnavailable);
                    FinalLabel = true;
                    if (LostPromotionAnswer) throw new HttpRequestException("promotion answer lost");
                    return Answer(new { labels = new[] { new { name = GuiCollectorDeployment.Label } } });
                }
                if (path == "/repos/michaeltrefry/LensWorks/environments/factory-gui-dispatch/variables/FACTORY_GUI_CONFIGURATION_SHA256")
                    return Answer(new { value = ConfigurationPin });
                if (path.StartsWith("/repos/michaeltrefry/dark-factory-gui-control/actions/runners?")) {
                    Assert.Equal("GET", method);
                    if (Challenges > 0 && !registered) {
                        Events.Add("absence-get");
                        if (ConflictAfterChallenge) return Runners(Runner(44, Name, "offline", GuiCollectorDeployment.BootstrapLabel(GuiCollectorDeployment.Hash(PlanBytes))));
                    }
                    if (registered && !onlineListed) { Events.Add("online-get"); onlineListed = true; }
                    return registered ? Runners(Runner(9, Name, Online ? "online" : "offline", FinalLabel ? GuiCollectorDeployment.Label : GuiCollectorDeployment.BootstrapLabel(GuiCollectorDeployment.Hash(PlanBytes)))) : Runners();
                }
                if (path == "/repos/michaeltrefry/LensWorks/actions/runners/7") {
                    Assert.Equal("DELETE", method);
                    Assert.Contains("backup", Events);
                    Assert.True(ProtectedBackup);
                    Assert.False(Removed);
                    Events.Add("delete-old-7"); Removed = true;
                    return new(HttpStatusCode.NoContent);
                }
                if (path.StartsWith("/repos/michaeltrefry/LensWorks/actions/runners?")) return Removed ? Runners() : Runners(Runner(7, Old));
                return inspection.Reply(method, path, body);
            });
        }
        public JsonObject Receipt(string status) => new() {
            ["version"] = 1, ["planSha256"] = WrongPlanReceipt ? new string('0', 64) : GuiCollectorDeployment.Hash(PlanBytes),
            ["runnerId"] = 9L, ["runnerName"] = Name, ["uid"] = 503,
            ["helperSha256"] = new string('a', 64), ["sourceJobSha256"] = new string('b', 64),
            ["configurationSha256"] = new string('c', 64), ["status"] = status,
            ["stages"] = RootRetired ? new JsonArray("old-backup-protected", "old-retired") : ProtectedBackup ? new JsonArray("old-backup-protected") : new JsonArray()
        };
        public JsonObject Challenge()
        {
            var challenge = Receipt("registration-reconciliation-required");
            challenge.Remove("runnerId");
            challenge["attempts"] = 1;
            challenge["firstStartedAt"] = Now.AddSeconds(-30).ToUnixTimeMilliseconds() / 1000.0;
            challenge["registrationIntent"] = new JsonObject { ["attemptNonce"] = new string('d', 32),
                ["createdAt"] = Now.AddSeconds(-10).ToUnixTimeMilliseconds() / 1000.0, ["state"] = ChallengeState,
                ["pid"] = ChallengeState == "unreleased" ? JsonValue.Create(123) : null,
                ["startIdentity"] = ChallengeState == "unreleased" ? JsonValue.Create("503 Mon Oct 26 01:02:03 2026") : null,
                ["localUnstartedVerified"] = true };
            return challenge;
        }
        public Task<JsonObject> Run()
        {
            var reservation = Evidence with { PlanSha256 = GuiCollectorDeployment.Hash(PlanBytes) };
            return Client(Api).CompleteInstallationAsync(PlanBytes, reservation, Hashes, KnownId, (authority, token, _) => {
                if (authority.RemovedOldRunnerId is not null) {
                    Assert.Equal(7, authority.RemovedOldRunnerId); Assert.True(Removed); Assert.Equal("", token); Events.Add("retire");
                    RootRetired = CompleteRetirement;
                    return Task.FromResult(Receipt(CompleteRetirement ? "complete" : "awaiting-owner-exact-old-registration-removal"));
                }
                if (authority.Online is not null) {
                    Assert.Equal(9, KnownId); Assert.Contains("online-get", Events); Assert.Equal("", token); Events.Add("backup");
                    return Task.FromResult(Receipt("awaiting-owner-exact-old-registration-removal"));
                }
                Assert.Equal(KnownId is null ? "REGISTRATION_SECRET" : "", token);
                if (ChallengeFirst && (Challenges == 0 || ChallengeAgain)) {
                    Challenges++; Events.Add("challenge"); return Task.FromResult(Challenge());
                }
                if (ChallengeFirst) {
                    Assert.NotNull(authority.RegistrationAbsence);
                    Absence = authority.RegistrationAbsence;
                    Assert.Contains("absence-get", Events);
                }
                Events.Add("register"); registered = true;
                return Task.FromResult(Receipt("awaiting-owner-online-verification-old-untouched"));
            }, (id, ct) => {
                Assert.False(ct.CanBeCanceled); Events.Add("record-id"); KnownId = id; return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.Zero, onlineAttempts: 1);
        }
    }

    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class FakeApi(Func<string, string, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public sealed record Call(string Method, string Path, string? Authorization, string? Body);
        public List<Call> Calls { get; } = [];
        public HttpResponseMessage Reply(string method, string path, string? body) => answer(method, path, body);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new(request.Method.Method, path, request.Headers.Authorization?.ToString(), body));
            return answer(request.Method.Method, path, body);
        }
    }
}
