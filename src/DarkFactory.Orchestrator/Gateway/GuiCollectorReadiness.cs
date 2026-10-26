using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    public sealed record BootstrapRun(string Workflow, string Job, long Id, int InitialAttempt,
        string Phase = "observed", int? RerunAttempt = null);
    public sealed record BootstrapReadiness(string PlanSha256, long MigrationPull, string MigrationSha,
        DateTimeOffset Deadline, BootstrapRun[] Runs, bool Complete = false);

    // Four serialized source jobs may each use the renderer's 180-minute ceiling, plus dispatch/upload time.
    // The deadline belongs to the journal, not to an invocation: resume cannot buy another thirteen hours.
    internal static readonly TimeSpan BootstrapCiTimeout = TimeSpan.FromHours(13);

    /// <summary>Explicit owner deployment only. Refresh the initial migration push's captured environment after installation.
    /// No other commit/workflow is rerun, and no absent, skipped source job or controller failure counts as readiness.</summary>
    public async Task<BootstrapReadiness> ReconcileBootstrapCiAsync(Evidence original, Workflow[] desiredLegacy,
        long migrationPull, long runnerId, string configurationSha256, BootstrapReadiness? recorded,
        Func<BootstrapReadiness, CancellationToken, Task> save, CancellationToken ct,
        TimeSpan? wait = null, TimeSpan? poll = null)
    {
        static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Bootstrap source CI: " + reason);
        }
        static bool Sha(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);
        var source = RepoPath(SourceRepository);
        Require(original.PlanSha256.Length == 64 && original.PlanSha256.All(char.IsAsciiHexDigit)
            && migrationPull > 0 && runnerId > 0, "invalid deployment identity.");
        Require(original.Workflows.Length == WorkflowPaths.Length && desiredLegacy.Length == WorkflowPaths.Length
            && original.Workflows.Select(w => w.Path).Order().SequenceEqual(WorkflowPaths.Order())
            && desiredLegacy.Select(w => w.Path).Order().SequenceEqual(WorkflowPaths.Order()), "incomplete compiled workflow inventory.");
        foreach (var before in original.Workflows)
        {
            var desired = desiredLegacy.Single(w => w.Path == before.Path);
            Require(Hash(before.Content) == before.Sha256 && Hash(desired.Content) == desired.Sha256
                && GuiSourceWorkflow.Migrate(before.Path, before.Content, Collector.FullName, original.DefaultBranch) == desired.Content,
                "migration is not the exact compiled source adapter.");
        }
        var pull = await SendAsync(HttpMethod.Get, source + $"/pulls/{migrationPull}", ownerToken, null, ct);
        var migrationSha = pull["merge_commit_sha"]?.GetValue<string>();
        Require(pull["merged"]?.GetValue<bool>() == true && Sha(migrationSha)
            && pull["head"]?["ref"]?.GetValue<string>() == "factory/gui-collector-" + original.PlanSha256[..16]
            && pull["base"]?["ref"]?.GetValue<string>() == original.DefaultBranch
            && string.Equals(pull["head"]?["repo"]?["full_name"]?.GetValue<string>(), SourceRepository.FullName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pull["base"]?["repo"]?["full_name"]?.GetValue<string>(), SourceRepository.FullName, StringComparison.OrdinalIgnoreCase),
            "the exact owner migration merge is not proven.");
        await VerifyReviewedMigrationAsync(SourceRepository, migrationPull, desiredLegacy, ct, original.DefaultBranch);
        // Check the merge tree too, not merely the PR's pre-merge head.
        foreach (var definition in desiredLegacy.Append(new Workflow(GuiSourceWorkflow.PublisherPath,
            Hash(GuiSourceWorkflow.Publisher(desiredLegacy.ToDictionary(w => w.Path, w => w.Content), Collector.FullName, original.DefaultBranch)), ""))
            .Append(new Workflow(RemoteIosWorkflow.Path, Hash(RemoteIosWorkflow.Text), "")))
            Require((await ReadDefinitionAsync(SourceRepository, definition.Path, migrationSha!, false, ct)).Sha256 == definition.Sha256,
                "the migration merge tree differs from the reviewed definitions.");
        async Task VerifyInstalledAsync()
        {
            Require(configurationSha256.Length == 64 && configurationSha256.All(char.IsAsciiHexDigit)
                && await DispatcherEnvironmentAsync(original.DefaultBranch, ct), "the protected dispatcher environment is not verified.");
            var pin = await SendAsync(HttpMethod.Get, source + "/environments/" + RemoteGuiSourceWorkflow.Environment
                + "/variables/FACTORY_GUI_CONFIGURATION_SHA256", ownerToken, null, ct);
            Require(pin["value"]?.GetValue<string>() == configurationSha256, "the installed configuration pin changed.");
            var runner = (await RunnersAsync(Collector, ct)).SingleOrDefault(r => r.Id == runnerId);
            Require(runner is { Status: "online" } && runner.Labels.SequenceEqual([Label]), "the exact final-label collector is not online.");
        }
        await VerifyInstalledAsync();

        var state = recorded ?? new BootstrapReadiness(original.PlanSha256, migrationPull, migrationSha!,
            time.GetUtcNow() + (wait ?? BootstrapCiTimeout), []);
        Require(state.PlanSha256 == original.PlanSha256 && state.MigrationPull == migrationPull && state.MigrationSha == migrationSha,
            "journal belongs to another plan, pull request or merge.");
        Require(state.Deadline <= time.GetUtcNow() + BootstrapCiTimeout && state.Runs.Length <= WorkflowPaths.Length
            && state.Runs.Select(r => r.Workflow).Distinct().Count() == state.Runs.Length
            && state.Runs.Select(r => r.Id).Distinct().Count() == state.Runs.Length, "invalid journal bounds or duplicate runs.");
        foreach (var run in state.Runs)
            Require(WorkflowPaths.Contains(run.Workflow) && run.Job == GuiSourceWorkflow.JobId(run.Workflow)
                && run.Id > 0 && run.InitialAttempt == 1 && run.Phase is "observed" or "cancel-requested" or "rerun-requested" or "rerun-observed" or "complete"
                && (run.RerunAttempt is null ? run.Phase is "observed" or "cancel-requested" : run.RerunAttempt == 2 && run.Phase is "rerun-requested" or "rerun-observed" or "complete"),
                "invalid run reservation.");
        if (recorded is null) await save(state, ct);

        async Task Store(BootstrapRun run)
        {
            state = state with { Complete = false, Runs = state.Runs.Where(r => r.Workflow != run.Workflow).Append(run).OrderBy(r => r.Workflow, StringComparer.Ordinal).ToArray() };
            await save(state, ct); // Every write intent is durable BEFORE its POST.
        }
        void Validate(JsonObject value, string workflow, long? id = null)
        {
            Require(value["id"]?.GetValue<long>() > 0 && (id is null || value["id"]!.GetValue<long>() == id)
                && value["head_sha"]?.GetValue<string>() == migrationSha && value["event"]?.GetValue<string>() == "push"
                && value["head_branch"]?.GetValue<string>() == original.DefaultBranch && value["path"]?.GetValue<string>() == workflow
                && string.Equals(value["repository"]?["full_name"]?.GetValue<string>(), SourceRepository.FullName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value["head_repository"]?["full_name"]?.GetValue<string>(), SourceRepository.FullName, StringComparison.OrdinalIgnoreCase)
                && value["run_attempt"]?.GetValue<int>() > 0
                && value["status"]?.GetValue<string>() is "queued" or "in_progress" or "completed" or "waiting" or "pending" or "requested",
                "run identity, provenance or state is not the exact migration push.");
        }
        async Task<JsonObject> Read(BootstrapRun run)
        {
            var value = await SendAsync(HttpMethod.Get, source + $"/actions/runs/{run.Id}", ownerToken, null, ct);
            Validate(value, run.Workflow, run.Id);
            return value;
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Validate the complete listing before acting on any one run, including a resumed journal's IDs.
            var found = new List<BootstrapRun>();
            foreach (var workflow in WorkflowPaths)
            {
                var listing = await SendAsync(HttpMethod.Get, source + "/actions/workflows/" + Uri.EscapeDataString(Path.GetFileName(workflow))
                    + "/runs?event=push&head_sha=" + migrationSha + "&per_page=100", ownerToken, null, ct);
                var rows = listing["workflow_runs"]!.AsArray();
                Require(listing["total_count"]?.GetValue<int>() == rows.Count && rows.Count < 100, "push-run inventory is incomplete.");
                var matches = rows.Where(r => r?["head_sha"]?.GetValue<string>() == migrationSha || r?["event"]?.GetValue<string>() == "push").ToArray();
                Require(matches.Length <= 1, "ambiguous migration push runs.");
                var known = state.Runs.SingleOrDefault(r => r.Workflow == workflow);
                if (matches.Length == 0) { Require(known is null, "a journaled run vanished from its workflow inventory."); continue; }
                var value = matches[0]!.AsObject();
                Validate(value, workflow, known?.Id);
                var attempt = value["run_attempt"]!.GetValue<int>();
                Require(known is null ? attempt == 1 : attempt == known.InitialAttempt || attempt == known.RerunAttempt,
                    "unexpected migration run attempt.");
                found.Add(known ?? new BootstrapRun(workflow, GuiSourceWorkflow.JobId(workflow), value["id"]!.GetValue<long>(), attempt));
            }
            Require(found.Select(r => r.Id).Distinct().Count() == found.Count, "run IDs are not distinct across workflows.");
            if (!state.Runs.OrderBy(r => r.Workflow).SequenceEqual(found.OrderBy(r => r.Workflow)))
            {
                state = state with { Complete = false, Runs = found.ToArray() };
                await save(state, ct);
            }
            if (found.Count == WorkflowPaths.Length)
            {
                foreach (var initial in state.Runs.ToArray())
                {
                    var run = initial;
                    var value = await Read(run);
                    var attempt = value["run_attempt"]!.GetValue<int>();
                    Require(attempt == run.InitialAttempt || attempt == run.RerunAttempt, "a run attempt moved outside the reservation.");
                    if (run.RerunAttempt is null)
                    {
                        if (recorded is not null && time.GetUtcNow() >= state.Deadline) continue;
                        if (value["status"]!.GetValue<string>() != "completed")
                        {
                            if (run.Phase == "observed")
                            {
                                run = run with { Phase = "cancel-requested" };
                                await Store(run);
                                await BootstrapActionAsync(source + $"/actions/runs/{run.Id}/cancel", ct);
                            }
                            value = await Read(run);
                            if (value["status"]!.GetValue<string>() != "completed") continue;
                        }
                        run = run with { Phase = "rerun-requested", RerunAttempt = run.InitialAttempt + 1 };
                        await Store(run);
                        // A lost answer retains this intent. Resume only observes; it never resends an uncertain POST.
                        await BootstrapActionAsync(source + $"/actions/runs/{run.Id}/rerun", ct);
                        value = await Read(run);
                    }
                    attempt = value["run_attempt"]!.GetValue<int>();
                    Require(attempt == run.InitialAttempt || attempt == run.RerunAttempt, "unexpected rerun attempt.");
                    if (attempt == run.InitialAttempt) continue; // Accepted rerun has not become visible yet.
                    if (run.Phase == "rerun-requested") { run = run with { Phase = "rerun-observed" }; await Store(run); }
                    if (value["status"]!.GetValue<string>() != "completed") continue;
                    Require(value["conclusion"]?.GetValue<string>() == "success", "the fresh-environment source rerun did not pass.");
                    var jobs = await SendAsync(HttpMethod.Get, source + $"/actions/runs/{run.Id}/jobs?filter=latest&per_page=100", ownerToken, null, ct);
                    var entries = jobs["jobs"]!.AsArray();
                    Require(jobs["total_count"]?.GetValue<int>() == entries.Count && entries.Count is > 0 and < 100,
                        "rerun job inventory is incomplete.");
                    Require(entries.Count <= 2 && entries.Select(j => j?["id"]?.GetValue<long>()).Distinct().Count() == entries.Count
                        && entries.All(j => j?["id"]?.GetValue<long>() > 0 && j?["name"]?.GetValue<string>() is { } jobName
                        && (jobName == run.Job || jobName == "factory-source-request")
                        && j?["run_id"]?.GetValue<long>() == run.Id && j?["run_attempt"]?.GetValue<int>() == run.RerunAttempt
                        && j?["head_sha"]?.GetValue<string>() == migrationSha && j?["status"]?.GetValue<string>() == "completed"
                        && j?["conclusion"]?.GetValue<string>() is "success" or "skipped" or "neutral")
                        && entries.Count(j => j?["name"]?.GetValue<string>() == run.Job && j?["conclusion"]?.GetValue<string>() == "success") == 1,
                        "the real source job did not pass on the reserved rerun attempt.");
                    if (run.Phase != "complete") await Store(run with { Phase = "complete" });
                }
                if (state.Runs.All(r => r.Phase == "complete"))
                {
                    var statuses = await SendAsync(HttpMethod.Get, source + "/commits/" + migrationSha + "/status?per_page=100", ownerToken, null, ct);
                    var rows = statuses["statuses"]!.AsArray();
                    Require(statuses["total_count"]?.GetValue<int>() == rows.Count && rows.Count < 100, "source-head status inventory is incomplete.");
                    var sourcePassed = state.Runs.All(run => rows.Count(s => s?["context"]?.GetValue<string>() == run.Job
                        && s?["state"]?.GetValue<string>() == "success") == 1);
                    var checks = await SendAsync(HttpMethod.Get, source + "/commits/" + migrationSha + "/check-runs?per_page=100", ownerToken, null, ct);
                    var ci = await DeploymentCiAsync(SourceRepository, migrationSha!, ct);
                    Require(ci != "failed", "migration-head controller/check/status failed.");
                    if (sourcePassed && checks["check_runs"]!.AsArray().Count > 0 && ci == "passed")
                    {
                        await VerifyInstalledAsync(); // The pin/runner must still hold after the potentially long CI wait.
                        state = state with { Complete = true };
                        await save(state, ct);
                        return state;
                    }
                }
            }
            if (time.GetUtcNow() >= state.Deadline) return state with { Complete = false };
            await Task.Delay(poll ?? TimeSpan.FromSeconds(5), time, ct);
        }
    }

    async Task BootstrapActionAsync(string path, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Post, path, "Bearer", ownerToken);
        using var response = await http.SendAsync(request, ct);
        // Both Actions endpoints acknowledge with 202 and may have no JSON body. Never infer execution from that ACK.
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            throw new InvalidOperationException($"Bootstrap source CI action was not acknowledged ({(int)response.StatusCode}); resume observes its reserved run before any further action.");
    }
}
