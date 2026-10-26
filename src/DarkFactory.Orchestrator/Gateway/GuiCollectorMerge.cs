using System.Net.Http;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    public sealed record PullProgress(bool Merged, string Reason, string Head);

    /// <summary>Only explicit owner apply/resume invokes this path. It merges only byte-for-byte compiler-rendered deployment
    /// definitions, with the owner's actual admin identity and an exact GitHub head precondition. No model-authored replacement,
    /// approval fabrication, branch-protection bypass or unrelated file can be merged here. Factory-code review authorizes the
    /// compiled templates; normal PRs retain their audit trail. No-report bootstrap is identified as provenance, never a CI pass.</summary>
    public async Task<PullProgress> MergeGeneratedPullAsync(Evidence original, Workflow[] desiredLegacy, long number, bool source,
        CancellationToken ct, TimeSpan? wait = null, TimeSpan? poll = null)
    {
        var repo = source ? SourceRepository : Collector;
        var path = RepoPath(repo);
        var owner = await SendAsync(HttpMethod.Get, "user", ownerToken, null, ct);
        if (owner["type"]?.GetValue<string>() != "User") throw new InvalidOperationException("Owner deployment merge requires an actual owner/admin account, not factory/model credentials.");
        var repository = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
        if (repository["permissions"]?["admin"]?.GetValue<bool>() != true) throw new InvalidOperationException("Owner admin permission is required to merge exact generated deployment definitions.");
        if (!source) await VerifyPrivateControlAsync(repository, ct);
        var branch = source ? original.DefaultBranch : original.CollectorDefaultBranch;
        if (branch is null || Text(repository, "default_branch") != branch) throw new InvalidOperationException("Deployment default branch changed; prepare again rather than widen its authority.");
        Workflow[] desired;
        Workflow[] before;
        if (source)
        {
            if (original.Workflows.Length != 4 || !original.Workflows.Select(w => w.Path).Order().SequenceEqual(WorkflowPaths.Order()))
                throw new InvalidOperationException("Source provenance inventory is not exactly the four legacy workflows.");
            var rendered = original.Workflows.Select(w => {
                if (Hash(w.Content) != w.Sha256) throw new InvalidOperationException("Original source workflow provenance was edited.");
                var content = GuiSourceWorkflow.Migrate(w.Path, w.Content, Collector.FullName, branch);
                return new Workflow(w.Path, Hash(content), content);
            }).ToArray();
            if (desiredLegacy.Length != 4 || rendered.Any(w => !desiredLegacy.Any(d => d.Path == w.Path && d.Content == w.Content && d.Sha256 == w.Sha256)))
                throw new InvalidOperationException("Owner merge accepts only exact compiled migration output, never unreviewed replacement programs.");
            var publisher = GuiSourceWorkflow.Publisher(rendered.ToDictionary(w => w.Path, w => w.Content), Collector.FullName, branch);
            desired = rendered.Append(new Workflow(GuiSourceWorkflow.PublisherPath, Hash(publisher), publisher))
                .Append(new Workflow(RemoteIosWorkflow.Path, Hash(RemoteIosWorkflow.Text), RemoteIosWorkflow.Text)).ToArray();
            before = original.Workflows.Append(original.SourcePublisher ?? new Workflow(GuiSourceWorkflow.PublisherPath, "", ""))
                .Append(original.SourceHostedWorkflow ?? new Workflow(RemoteIosWorkflow.Path, "", "")).ToArray();
        }
        else { desired = CollectorDefinitions; before = original.CollectorWorkflows; }
        if (before.Length != desired.Length) throw new InvalidOperationException("Prepared definition provenance is incomplete.");
        var expectedChanges = desired.Where(d => before.Single(b => b.Path == d.Path).Sha256 != d.Sha256).Select(d => d.Path).ToArray();
        var finish = time.GetUtcNow() + (wait ?? TimeSpan.FromMinutes(30));
        string head = "";
        for (var attempt = 0; attempt < 361; attempt++)
        {
            var pull = await SendAsync(HttpMethod.Get, path + $"/pulls/{number}", ownerToken, null, ct);
            head = pull["head"]?["sha"]?.GetValue<string>() ?? "";
            var expectedRef = (source ? "factory/gui-collector-" : "factory/gui-collector-infrastructure-") + original.PlanSha256[..16];
            if (head.Length != 40 || head.Any(c => !char.IsAsciiHexDigit(c))
                || pull["head"]?["ref"]?.GetValue<string>() != expectedRef
                || pull["base"]?["ref"]?.GetValue<string>() != branch
                || !string.Equals(pull["base"]?["repo"]?["full_name"]?.GetValue<string>(), repo.FullName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pull["head"]?["repo"]?["full_name"]?.GetValue<string>(), repo.FullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Generated PR identity, repository, base or immutable head changed.");
            var files = await SendArrayAsync(path + $"/pulls/{number}/files?per_page=100", ct);
            if (pull["changed_files"]?.GetValue<int>() != files.Count) throw new InvalidOperationException("Generated PR file inventory is incomplete.");
            if (source) RequireSourceFiles(files, expectedChanges); else RequireControlFiles(files, expectedChanges);
            foreach (var definition in desired)
                if ((await ReadDefinitionAsync(repo, definition.Path, head, false, ct)).Sha256 != definition.Sha256)
                    throw new InvalidOperationException("Generated PR head contains unreviewed or changed deployment bytes; nothing merged.");
            if (pull["merged"]?.GetValue<bool>() == true)
            {
                await VerifyReviewAuthorityAsync(repo, number, pull, ct);
                return new(true, "Exact generated deployment PR already has an owner/admin merge; reconciled without another write.", head);
            }
            if (pull["state"]?.GetValue<string>() != "open") throw new InvalidOperationException("Generated deployment PR was closed without merge; owner intent is not overridden.");
            if (!string.Equals(pull["user"]?["login"]?.GetValue<string>(), owner["login"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Owner apply does not take over another account's deployment PR.");
            var observation = await DeploymentCiAsync(repo, head, ct);
            if (observation == "failed") throw new InvalidOperationException("Generated PR has a failed or unusable head check; no owner merge was performed.");
            if (observation == "pending")
            {
                if (time.GetUtcNow() >= finish || attempt == 360)
                    return new(false, "Exact generated deployment PR is still waiting on its head CI. Explicit resume revalidates and continues; no installation was authorized.", head);
                await Task.Delay(poll ?? TimeSpan.FromSeconds(5), time, ct);
                continue;
            }
            // Revalidate every default definition after the wait, not only the PR head. Concurrent unrelated business-code
            // merges are harmless; an operator's different workflow edit is never overwritten by this deployment.
            var freshRepository = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
            if (freshRepository["permissions"]?["admin"]?.GetValue<bool>() != true || Text(freshRepository, "default_branch") != branch)
                throw new InvalidOperationException("Owner/default-branch authority changed while deployment CI was awaited.");
            if (!source) await VerifyPrivateControlAsync(freshRepository, ct);
            var tip = Text(await SendAsync(HttpMethod.Get, path + "/commits/" + Uri.EscapeDataString(branch), ownerToken, null, ct), "sha");
            foreach (var definition in desired)
            {
                var current = await ReadDefinitionAsync(repo, definition.Path, tip, true, ct);
                if (current.Sha256 != definition.Sha256 && current.Sha256 != before.Single(b => b.Path == definition.Path).Sha256)
                    throw new InvalidOperationException("Default deployment definition changed concurrently; no owner merge was performed.");
            }
            var result = await SendAsync(HttpMethod.Put, path + $"/pulls/{number}/merge", ownerToken, new JsonObject {
                ["sha"] = head, ["merge_method"] = "merge", ["commit_title"] = "Deploy reviewed compiled GUI collector protocol" }, ct);
            if (result["merged"]?.GetValue<bool>() != true) throw new InvalidOperationException("GitHub did not confirm the normal owner-authorized deployment merge; resume reconciles before installation.");
            var merged = await SendAsync(HttpMethod.Get, path + $"/pulls/{number}", ownerToken, null, ct);
            if (merged["head"]?["sha"]?.GetValue<string>() != head) throw new InvalidOperationException("Merged deployment head does not match the exact approved bytes.");
            await VerifyReviewAuthorityAsync(repo, number, merged, ct);
            return new(true, observation == "none"
                ? "Owner-authorized exact compiled deployment payload merged. No head checks reported for this bootstrap-only PR; this is provenance authority, not a CI pass."
                : "Exact compiled deployment payload merged by the owner/admin after its head checks passed.", head);
        }
        return new(false, "Deployment merge is not confirmed; explicit resume reconciles it before installation.", head);
    }

    async Task<string> DeploymentCiAsync(RepoRef repo, string head, CancellationToken ct)
    {
        var path = RepoPath(repo) + "/commits/" + head;
        var checks = await SendAsync(HttpMethod.Get, path + "/check-runs?per_page=100", ownerToken, null, ct);
        var runs = checks["check_runs"]!.AsArray();
        var statuses = await SendAsync(HttpMethod.Get, path + "/status?per_page=100", ownerToken, null, ct);
        var states = statuses["statuses"]!.AsArray();
        var suites = await SendAsync(HttpMethod.Get, path + "/check-suites?per_page=100", ownerToken, null, ct);
        var groups = suites["check_suites"]!.AsArray();
        if (checks["total_count"]!.GetValue<int>() != runs.Count || statuses["total_count"]!.GetValue<int>() != states.Count
            || suites["total_count"]!.GetValue<int>() != groups.Count || runs.Count >= 100 || states.Count >= 100 || groups.Count >= 100)
            throw new InvalidOperationException("Deployment CI inventory is incomplete; no owner merge is authorized.");
        var pending = false;
        foreach (var run in runs)
        {
            if (run?["head_sha"]?.GetValue<string>() != head) return "failed";
            if (run?["status"]?.GetValue<string>() != "completed") { pending = true; continue; }
            if (run?["conclusion"]?.GetValue<string>() is not ("success" or "neutral" or "skipped")) return "failed";
        }
        foreach (var state in states)
        {
            var value = state?["state"]?.GetValue<string>();
            if (value == "pending") pending = true;
            else if (value != "success") return "failed";
        }
        foreach (var suite in groups.Where(g => g?["app"]?["slug"]?.GetValue<string>() == "github-actions"))
        {
            if (suite?["head_sha"]?.GetValue<string>() != head) return "failed";
            if (suite?["status"]?.GetValue<string>() != "completed") pending = true;
            else if (suite?["conclusion"]?.GetValue<string>() is "failure" or "timed_out" or "cancelled" or "startup_failure") return "failed";
        }
        return pending ? "pending" : runs.Count + states.Count == 0 ? "none" : "passed";
    }
}
