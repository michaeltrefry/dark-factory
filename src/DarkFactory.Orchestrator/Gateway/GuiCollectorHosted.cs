using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    public sealed record HostedPlan(string Repository, string Branch, string Commit, Workflow Before);
    public RepoRef FixtureRepository => new(Collector.Owner, "dark-factory-s6-fixtures");

    /// <summary>Explicit owner apply creates only the dedicated public fixture repository, never an operational sandbox.
    /// All other trusted repositories must already exist. No generated projects are written to a default branch here.</summary>
    public async Task<HostedPlan> PrepareHostedRepositoryAsync(RepoRef repo, GitHubApp workerApp, CancellationToken ct)
    {
        if (repo.Is(Collector) || FactoryRepositoryPolicy.IsReservedController(repo, Collector))
            throw new InvalidOperationException("A collector control repository cannot be a hosted source/fixture repository.");
        var path = RepoPath(repo);
        var metadata = await OptionalAsync(path, ownerToken, ct);
        if (metadata is null)
        {
            if (!repo.Is(FixtureRepository)) throw new InvalidOperationException("A configured trusted source is absent; deployment creates only its dedicated fixture repository.");
            var user = await SendAsync(HttpMethod.Get, "user", ownerToken, null, ct);
            if (!string.Equals(Text(user, "login"), repo.Owner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the fixture repository owner may create its public scaffold.");
            await SendAsync(HttpMethod.Post, "user/repos", ownerToken, new JsonObject { ["name"] = repo.Name,
                ["private"] = false, ["auto_init"] = true, ["has_issues"] = false, ["has_projects"] = false, ["has_wiki"] = false,
                ["description"] = "Dedicated generated dark-factory S6 acceptance fixtures; no operational source" }, ct);
            metadata = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
        }
        if (metadata["permissions"]?["admin"]?.GetValue<bool>() != true || metadata["fork"]?.GetValue<bool>() != false
            || !string.Equals(metadata["full_name"]?.GetValue<string>(), repo.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hosted source metadata must name the exact admin-visible, non-fork repository.");
        var branch = Text(metadata, "default_branch");
        var tip = Text(await SendAsync(HttpMethod.Get, path + "/commits/" + Uri.EscapeDataString(branch), ownerToken, null, ct), "sha");
        if (tip.Length != 40 || tip.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Hosted source default head is not immutable.");
        if (repo.Is(FixtureRepository))
        {
            if (metadata["private"]?.GetValue<bool>() != false) throw new InvalidOperationException("Dedicated fixture repository must be public; an existing private repository is never declassified automatically.");
            var tree = await SendAsync(HttpMethod.Get, path + "/git/trees/" + tip + "?recursive=1", ownerToken, null, ct);
            var entries = tree["tree"]!.AsArray();
            if (tree["truncated"]?.GetValue<bool>() != false || entries.Count > 2000 || entries.Any(e => !FixtureScaffold(e!.AsObject())))
                throw new InvalidOperationException("Dedicated fixture default branch is not a README/license/.github/.gitignore scaffold; operational projects are never overwritten.");
        }
        await EnsureHostedAppAsync(repo, metadata, workerApp, "worker", ["contents", "pull_requests"], ct);
        await EnsureHostedAppAsync(repo, metadata, gateApp, "Gate", ["actions"], ct);
        return new(repo.FullName, branch, tip, await ReadDefinitionAsync(repo, RemoteIosWorkflow.Path, tip, true, ct));
    }

    static bool FixtureScaffold(JsonObject entry)
    {
        var path = entry["path"]?.GetValue<string>() ?? "";
        if (path.Length == 0 || path.StartsWith('/') || path.Split('/').Any(p => p is "" or "." or "..")
            || entry["type"]?.GetValue<string>() is not ("blob" or "tree") || entry["mode"]?.GetValue<string>() is "120000" or "160000") return false;
        if (path == ".github" || path.StartsWith(".github/", StringComparison.Ordinal)) return true;
        return !path.Contains('/') && (path == ".gitignore" || path.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase));
    }

    async Task EnsureHostedAppAsync(RepoRef repo, JsonObject repository, GitHubApp app, string role, string[] requiredWrites, CancellationToken ct)
    {
        var appMetadata = await SendAsync(HttpMethod.Get, "app", app.CreateJwt(), null, ct);
        if (requiredWrites.Any(p => appMetadata["permissions"]?[p]?.GetValue<string>() != "write"))
            throw new InvalidOperationException($"The {role} App has not approved the required fixture/source permissions; approve its App update and resume.");
        var installation = await OptionalAsync(RepoPath(repo) + "/installation", app.CreateJwt(), ct);
        if (installation is null)
        {
            JsonObject visible;
            try { visible = await SendAsync(HttpMethod.Get, "user/installations?per_page=100", ownerToken, null, ct); }
            catch (InvalidOperationException) { throw new InvalidOperationException($"Select {repo} on the {role} App installation and resume; the owner's current credential cannot change that selection automatically."); }
            var rows = visible["installations"]!.AsArray();
            if (visible["total_count"]!.GetValue<int>() != rows.Count) throw new InvalidOperationException("Hosted App selection inventory is incomplete.");
            var candidates = rows.Where(r => r?["app_id"]?.ToString() == app.AppId
                && string.Equals(r?["account"]?["login"]?.GetValue<string>(), repo.Owner, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException($"Install/select {repo} on the {role} App, then resume; no fixture authority is fabricated.");
            await SendAsync(HttpMethod.Put, $"user/installations/{candidates[0]!["id"]!.GetValue<long>()}/repositories/{repository["id"]!.GetValue<long>()}", ownerToken, null, ct);
            installation = await OptionalAsync(RepoPath(repo) + "/installation", app.CreateJwt(), ct);
        }
        if (installation is null || requiredWrites.Any(p => installation["permissions"]?[p]?.GetValue<string>() != "write"))
            throw new InvalidOperationException($"The {role} App installation has not approved {repo}'s required scope; approve it and resume.");
    }

    public async Task<long> HostedPullRequestAsync(HostedPlan prepared, string planHash, CancellationToken ct)
    {
        var repo = RepoRef.Parse(prepared.Repository);
        var path = RepoPath(repo);
        var branch = HostedBranch(repo, planHash);
        var desired = new Workflow(RemoteIosWorkflow.Path, Hash(RemoteIosWorkflow.Text), RemoteIosWorkflow.Text);
        var refs = await SendArrayAsync(path + "/git/matching-refs/heads/" + branch, ct);
        if (!refs.Any(r => r?["ref"]?.GetValue<string>() == "refs/heads/" + branch))
            await SendAsync(HttpMethod.Post, path + "/git/refs", ownerToken, new JsonObject { ["ref"] = "refs/heads/" + branch, ["sha"] = prepared.Commit }, ct);
        var file = await OptionalAsync(path + "/contents/" + desired.Path + "?ref=" + branch, ownerToken, ct);
        var hash = file is null ? "" : Hash(Convert.FromBase64String(Text(file, "content")));
        if (hash != desired.Sha256)
        {
            if (hash != prepared.Before.Sha256) throw new InvalidOperationException("Hosted deployment branch was edited outside this plan; no overwrite performed.");
            var body = new JsonObject { ["message"] = "Install exact reviewed factory hosted iOS check", ["branch"] = branch,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(desired.Content)) };
            if (file is not null) body["sha"] = Text(file, "sha");
            await SendAsync(HttpMethod.Put, path + "/contents/" + desired.Path, ownerToken, body, ct);
        }
        var pulls = await SendArrayAsync(path + "/pulls?state=all&head=" + Uri.EscapeDataString(repo.Owner + ":" + branch) + "&per_page=100", ct);
        if (pulls.Count > 1) throw new InvalidOperationException("Hosted definition has ambiguous deployment PRs.");
        if (pulls.Count == 1) return pulls[0]!["number"]!.GetValue<long>();
        var pull = await SendAsync(HttpMethod.Post, path + "/pulls", ownerToken, new JsonObject { ["head"] = branch, ["base"] = prepared.Branch,
            ["title"] = "Install sealed hosted iOS new-tests workflow", ["body"] = "Owner deployment plan " + planHash
                + ". Exact compiled factory template only, no projects or operational source added to the default branch. Normal owner-authorized PR merge and bounded head CI; no model-authored code." }, ct);
        return pull["number"]!.GetValue<long>();
    }

    static string HostedBranch(RepoRef repo, string planHash) => "factory/gui-hosted-" + planHash[..16] + "-" + Hash(repo.Key)[..8];

    public async Task<PullProgress> MergeHostedPullAsync(HostedPlan prepared, string planHash, long number, CancellationToken ct,
        TimeSpan? wait = null, TimeSpan? poll = null)
    {
        var repo = RepoRef.Parse(prepared.Repository);
        var path = RepoPath(repo);
        var owner = await SendAsync(HttpMethod.Get, "user", ownerToken, null, ct);
        var metadata = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
        if (owner["type"]?.GetValue<string>() != "User" || metadata["permissions"]?["admin"]?.GetValue<bool>() != true
            || Text(metadata, "default_branch") != prepared.Branch) throw new InvalidOperationException("Hosted template deployment needs the current owner's normal admin authority.");
        var finish = time.GetUtcNow() + (wait ?? TimeSpan.FromMinutes(30));
        for (var attempt = 0; attempt < 361; attempt++)
        {
            var pull = await SendAsync(HttpMethod.Get, path + $"/pulls/{number}", ownerToken, null, ct);
            var head = pull["head"]?["sha"]?.GetValue<string>() ?? "";
            if (head.Length != 40 || head.Any(c => !char.IsAsciiHexDigit(c)) || pull["head"]?["ref"]?.GetValue<string>() != HostedBranch(repo, planHash)
                || pull["base"]?["ref"]?.GetValue<string>() != prepared.Branch
                || !string.Equals(pull["head"]?["repo"]?["full_name"]?.GetValue<string>(), repo.FullName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pull["base"]?["repo"]?["full_name"]?.GetValue<string>(), repo.FullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Hosted generated PR identity changed; nothing merged.");
            var files = await SendArrayAsync(path + $"/pulls/{number}/files?per_page=100", ct);
            if (pull["changed_files"]?.GetValue<int>() != 1 || files.Count != 1 || files[0]?["filename"]?.GetValue<string>() != RemoteIosWorkflow.Path
                || files[0]?["status"]?.GetValue<string>() is not ("added" or "modified")
                || (await ReadDefinitionAsync(repo, RemoteIosWorkflow.Path, head, false, ct)).Content != RemoteIosWorkflow.Text)
                throw new InvalidOperationException("Hosted PR is not the exact single compiled workflow payload.");
            if (pull["merged"]?.GetValue<bool>() == true) { await VerifyReviewAuthorityAsync(repo, number, pull, ct); return new(true, "Exact hosted template owner merge already confirmed.", head); }
            if (pull["state"]?.GetValue<string>() != "open" || !string.Equals(pull["user"]?["login"]?.GetValue<string>(), owner["login"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Hosted PR is closed or belongs to another actor; owner intent is not overridden.");
            var ci = await DeploymentCiAsync(repo, head, ct);
            if (ci == "failed") throw new InvalidOperationException("Hosted template PR head CI failed; no owner merge performed.");
            if (ci == "pending")
            {
                if (time.GetUtcNow() >= finish || attempt == 360) return new(false, "Hosted template PR still awaits its exact head CI; resume revalidates automatically.", head);
                await Task.Delay(poll ?? TimeSpan.FromSeconds(5), time, ct); continue;
            }
            var current = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
            if (current["permissions"]?["admin"]?.GetValue<bool>() != true || Text(current, "default_branch") != prepared.Branch)
                throw new InvalidOperationException("Hosted source authority/default branch changed during CI.");
            var tip = Text(await SendAsync(HttpMethod.Get, path + "/commits/" + Uri.EscapeDataString(prepared.Branch), ownerToken, null, ct), "sha");
            var definition = await ReadDefinitionAsync(repo, RemoteIosWorkflow.Path, tip, true, ct);
            if (definition.Sha256 != prepared.Before.Sha256 && definition.Content != RemoteIosWorkflow.Text)
                throw new InvalidOperationException("Hosted default workflow changed concurrently; no owner overwrite performed.");
            var merged = await SendAsync(HttpMethod.Put, path + $"/pulls/{number}/merge", ownerToken,
                new JsonObject { ["sha"] = head, ["merge_method"] = "merge", ["commit_title"] = "Deploy reviewed compiled hosted iOS check" }, ct);
            if (merged["merged"]?.GetValue<bool>() != true) throw new InvalidOperationException("Hosted owner merge was not confirmed; resume reconciles it.");
            var accepted = await SendAsync(HttpMethod.Get, path + $"/pulls/{number}", ownerToken, null, ct);
            if (accepted["head"]?["sha"]?.GetValue<string>() != head) throw new InvalidOperationException("Hosted merged head changed.");
            await VerifyReviewAuthorityAsync(repo, number, accepted, ct);
            return new(true, ci == "none" ? "Exact compiled hosted template owner merge confirmed; no head checks reported (not a CI pass)." : "Hosted template merged after exact head CI passed.", head);
        }
        return new(false, "Hosted owner merge is not confirmed; resume reconciles before fixture publication.", "");
    }
}
