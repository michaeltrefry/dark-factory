using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    public static readonly RepoRef ControlRepository = RepoRef.Parse(RemoteGuiSourceWorkflow.Repository);
    public static Workflow[] CollectorDefinitions => new[] {
        new Workflow(RemoteGuiWorkflow.Path, Hash(RemoteGuiWorkflow.Text), RemoteGuiWorkflow.Text),
        new Workflow(RemoteGuiSourceWorkflow.Path, RemoteGuiSourceWorkflow.Sha256, RemoteGuiSourceWorkflow.Text)
    };

    async Task<JsonObject?> OptionalAsync(string path, string token, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, path, "Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GUI deployment GET refused ({(int)response.StatusCode}); no response body retained.");
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct))?.AsObject()
            ?? throw new InvalidOperationException("Invalid deployment API metadata.");
    }

    async Task<Workflow> ReadDefinitionAsync(RepoRef repo, string path, string commit, bool optional, CancellationToken ct)
    {
        var file = await OptionalAsync(RepoPath(repo) + "/contents/" + path + "?ref=" + Uri.EscapeDataString(commit), ownerToken, ct);
        if (file is null)
        {
            if (!optional) throw new InvalidOperationException("An inventoried source workflow is missing.");
            return new(path, "", "");
        }
        if (Text(file, "type") != "file" || Text(file, "encoding") != "base64")
            throw new InvalidOperationException("Workflow metadata is not a regular base64 file.");
        var bytes = Convert.FromBase64String(Text(file, "content"));
        if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Workflow exceeds the deployment read bound.");
        return new(path, Hash(bytes), new UTF8Encoding(false, true).GetString(bytes));
    }

    async Task VerifyPrivateControlAsync(JsonObject repository, CancellationToken ct)
    {
        if (repository["private"]?.GetValue<bool>() != true || repository["fork"]?.GetValue<bool>() != false
            || !string.Equals(repository["full_name"]?.GetValue<string>(), Collector.FullName, StringComparison.OrdinalIgnoreCase)
            || repository["owner"]?["type"]?.GetValue<string>() != "User"
            || !string.Equals(repository["owner"]?["login"]?.GetValue<string>(), Collector.Owner, StringComparison.OrdinalIgnoreCase)
            || repository["permissions"]?["admin"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Collector must be the exact private, non-fork, owner-admin control repository; public/source repositories cannot host its credentialed runner.");
        var collaborators = await SendArrayAsync(RepoPath(Collector) + "/collaborators?affiliation=all&per_page=100", ct);
        if (collaborators.Count >= 100 || collaborators.Any(c =>
            (c?["permissions"]?["push"]?.GetValue<bool>() == true || c?["permissions"]?["maintain"]?.GetValue<bool>() == true || c?["permissions"]?["admin"]?.GetValue<bool>() == true)
            && !string.Equals(c?["login"]?.GetValue<string>(), Collector.Owner, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Control repository collaborator inventory is incomplete or grants another principal write access. Remove unexpected writers before deploying.");
    }

    /// <summary>Explicit apply only. Creates a PRIVATE owner-only control repository if absent; never creates a public fallback.
    /// Gate App selection/permission approval is an actual authority boundary, not a receipt boolean or a runtime bypass.</summary>
    public async Task ProvisionControlRepositoryAsync(CancellationToken ct)
    {
        var user = await SendAsync(HttpMethod.Get, "user", ownerToken, null, ct);
        if (!string.Equals(Text(user, "login"), Collector.Owner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only the control repository owner may provision the private collector.");
        var repository = await OptionalAsync(RepoPath(Collector), ownerToken, ct);
        if (repository is null)
        {
            repository = await SendAsync(HttpMethod.Post, "user/repos", ownerToken, new JsonObject {
                ["name"] = Collector.Name, ["private"] = true, ["auto_init"] = true,
                ["description"] = "Private factory GUI collector control plane; no source pull-request execution",
                ["has_issues"] = false, ["has_projects"] = false, ["has_wiki"] = false,
                ["allow_forking"] = false, ["delete_branch_on_merge"] = true }, ct);
            repository = await SendAsync(HttpMethod.Get, RepoPath(Collector), ownerToken, null, ct);
        }
        await VerifyPrivateControlAsync(repository, ct);
        await SendAsync(HttpMethod.Put, RepoPath(Collector) + "/actions/permissions", ownerToken,
            new JsonObject { ["enabled"] = true, ["allowed_actions"] = "selected" }, ct);
        await SendAsync(HttpMethod.Put, RepoPath(Collector) + "/actions/permissions/selected-actions", ownerToken,
            new JsonObject { ["github_owned_allowed"] = true, ["verified_allowed"] = false, ["patterns_allowed"] = new JsonArray() }, ct);
        await SendAsync(HttpMethod.Put, RepoPath(Collector) + "/actions/permissions/workflow", ownerToken,
            new JsonObject { ["default_workflow_permissions"] = "read", ["can_approve_pull_request_reviews"] = false }, ct);
        var installation = await OptionalAsync(RepoPath(Collector) + "/installation", gateApp.CreateJwt(), ct);
        if (installation is null)
        {
            JsonObject installations;
            try { installations = await SendAsync(HttpMethod.Get, "user/installations?per_page=100", ownerToken, null, ct); }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("The owner's GitHub credential cannot select the private collector for the Gate App installation. Add/approve that repository on the Gate App installation, then resume; no registration or root installation was authorized.");
            }
            var rows = installations["installations"]!.AsArray();
            if (installations["total_count"]!.GetValue<int>() > rows.Count) throw new InvalidOperationException("App installation inventory is incomplete.");
            var matches = rows.Where(i => i?["app_id"]?.ToString() == gateApp.AppId
                && string.Equals(i?["account"]?["login"]?.GetValue<string>(), Collector.Owner, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Install/approve the Gate App for the private dark-factory-gui-control repository, then resume. No registration or root installation is authorized yet.");
            await SendAsync(HttpMethod.Put, $"user/installations/{matches[0]!["id"]!.GetValue<long>()}/repositories/{repository["id"]!.GetValue<long>()}", ownerToken, null, ct);
            installation = await OptionalAsync(RepoPath(Collector) + "/installation", gateApp.CreateJwt(), ct);
        }
        if (installation?["permissions"]?["actions"]?.GetValue<string>() != "write")
            throw new InvalidOperationException("Gate App Actions-write approval on the private control repository is required. Approve that installation and resume; no fallback is used.");
    }

    async Task<bool> DispatcherEnvironmentAsync(string branch, CancellationToken ct)
    {
        var path = RepoPath(SourceRepository) + "/environments/" + RemoteGuiSourceWorkflow.Environment;
        var environment = await OptionalAsync(path, ownerToken, ct);
        if (environment is null || environment["deployment_branch_policy"]?["protected_branches"]?.GetValue<bool>() != false
            || environment["deployment_branch_policy"]?["custom_branch_policies"]?.GetValue<bool>() != true) return false;
        var policies = await SendAsync(HttpMethod.Get, path + "/deployment-branch-policies?per_page=100", ownerToken, null, ct);
        var rows = policies["branch_policies"]!.AsArray();
        if (policies["total_count"]!.GetValue<int>() != 1 || rows.Count != 1
            || rows[0]?["name"]?.GetValue<string>() != branch || rows[0]?["type"]?.GetValue<string>() != "branch") return false;
        var secret = await OptionalAsync(path + "/secrets/FACTORY_GUI_APP_PRIVATE_KEY", ownerToken, ct);
        var variable = await OptionalAsync(path + "/variables/FACTORY_GUI_APP_ID", ownerToken, ct);
        return secret?["name"]?.GetValue<string>() == "FACTORY_GUI_APP_PRIVATE_KEY" && variable?["value"]?.GetValue<string>() == gateApp.AppId;
    }

    /// <summary>Explicit apply grants only the Gate-App signing key to a hosted, default-branch-only environment. The owner's
    /// PAT is used by gh locally and is never a GitHub secret, collector environment variable or source-account credential.</summary>
    public async Task ProvisionDispatcherEnvironmentAsync(string branch, string privateKeyPem, CancellationToken ct,
        Func<bool, string, string, CancellationToken, Task>? writeValue = null)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem) || privateKeyPem == ownerToken)
            throw new InvalidOperationException("Only the Gate App key may provision the protected dispatcher.");
        var path = RepoPath(SourceRepository) + "/environments/" + RemoteGuiSourceWorkflow.Environment;
        await SendAsync(HttpMethod.Put, path, ownerToken, new JsonObject {
            ["wait_timer"] = 0, ["reviewers"] = new JsonArray(), ["prevent_self_review"] = true,
            ["deployment_branch_policy"] = new JsonObject { ["protected_branches"] = false, ["custom_branch_policies"] = true } }, ct);
        var policies = await SendAsync(HttpMethod.Get, path + "/deployment-branch-policies?per_page=100", ownerToken, null, ct);
        var rows = policies["branch_policies"]!.AsArray();
        if (policies["total_count"]!.GetValue<int>() != rows.Count || rows.Count >= 100)
            throw new InvalidOperationException("Protected environment branch inventory is incomplete; no key was published.");
        foreach (var row in rows)
            if (row?["name"]?.GetValue<string>() != branch || row?["type"]?.GetValue<string>() != "branch")
                await SendAsync(HttpMethod.Delete, path + "/deployment-branch-policies/" + row!["id"]!.GetValue<long>(), ownerToken, null, ct);
        if (!rows.Any(r => r?["name"]?.GetValue<string>() == branch && r?["type"]?.GetValue<string>() == "branch"))
            await SendAsync(HttpMethod.Post, path + "/deployment-branch-policies", ownerToken, new JsonObject { ["name"] = branch, ["type"] = "branch" }, ct);
        // Re-read the restriction BEFORE publishing signing material; an acknowledged write alone is not proof.
        var restricted = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
        var verified = await SendAsync(HttpMethod.Get, path + "/deployment-branch-policies?per_page=100", ownerToken, null, ct);
        if (restricted["deployment_branch_policy"]?["protected_branches"]?.GetValue<bool>() != false
            || restricted["deployment_branch_policy"]?["custom_branch_policies"]?.GetValue<bool>() != true
            || verified["total_count"]!.GetValue<int>() != 1 || verified["branch_policies"]!.AsArray().Count != 1
            || verified["branch_policies"]![0]?["name"]?.GetValue<string>() != branch
            || verified["branch_policies"]![0]?["type"]?.GetValue<string>() != "branch")
            throw new InvalidOperationException("Default-only environment restriction did not read back exactly; no App key was published.");
        writeValue ??= (secret, name, value, c) => GitHubCli.SetGuiEnvironmentValueAsync(ownerToken, secret, name, value, c);
        await writeValue(false, "FACTORY_GUI_APP_ID", gateApp.AppId, ct);
        await writeValue(true, "FACTORY_GUI_APP_PRIVATE_KEY", privateKeyPem, ct);
        if (!await DispatcherEnvironmentAsync(branch, ct))
            throw new InvalidOperationException("Protected dispatcher environment credentials did not read back as configured.");
    }

    public async Task PinInstalledConfigurationAsync(string branch, string hash, CancellationToken ct)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || !await DispatcherEnvironmentAsync(branch, ct))
            throw new InvalidOperationException("Cannot pin an installed configuration without the verified protected dispatcher environment.");
        await GitHubCli.SetGuiEnvironmentValueAsync(ownerToken, false, "FACTORY_GUI_CONFIGURATION_SHA256", hash, ct);
        var value = await SendAsync(HttpMethod.Get, RepoPath(SourceRepository) + "/environments/" + RemoteGuiSourceWorkflow.Environment
            + "/variables/FACTORY_GUI_CONFIGURATION_SHA256", ownerToken, null, ct);
        if (value["value"]?.GetValue<string>() != hash) throw new InvalidOperationException("Installed configuration pin did not read back exactly.");
    }

    public async Task<long> CollectorPullRequestAsync(Evidence prepared, CancellationToken ct)
    {
        if (!prepared.CollectorExists || prepared.CollectorDefaultCommit is null || !prepared.CollectorPrivateOwnerOnly)
            throw new InvalidOperationException("Private control repository must be provisioned and verified before its normal workflow PR.");
        var desired = CollectorDefinitions;
        var original = prepared.CollectorWorkflows;
        if (original.Length != desired.Length) throw new InvalidOperationException("Collector definition inventory is incomplete.");
        var path = RepoPath(Collector);
        var branch = "factory/gui-collector-infrastructure-" + prepared.PlanSha256[..16];
        var refs = await SendArrayAsync(path + "/git/matching-refs/heads/" + branch, ct);
        if (!refs.Any(n => n?["ref"]?.GetValue<string>() == "refs/heads/" + branch))
            await SendAsync(HttpMethod.Post, path + "/git/refs", ownerToken,
                new JsonObject { ["ref"] = "refs/heads/" + branch, ["sha"] = prepared.CollectorDefaultCommit }, ct);
        foreach (var replacement in desired)
        {
            var file = await OptionalAsync(path + "/contents/" + replacement.Path + "?ref=" + branch, ownerToken, ct);
            var current = file is null ? "" : Hash(Convert.FromBase64String(Text(file, "content")));
            if (current == replacement.Sha256) continue;
            if (current != original.Single(w => w.Path == replacement.Path).Sha256)
                throw new InvalidOperationException("Control workflow branch changed outside this exact deployment plan; refusing overwrite.");
            var body = new JsonObject { ["message"] = "Install sealed isolated GUI collector workflows", ["branch"] = branch,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(replacement.Content)) };
            if (file is not null) body["sha"] = Text(file, "sha");
            await SendAsync(HttpMethod.Put, path + "/contents/" + replacement.Path, ownerToken, body, ct);
        }
        var comparison = await SendAsync(HttpMethod.Get, path + "/compare/" + prepared.CollectorDefaultCommit + "..." + branch, ownerToken, null, ct);
        var changed = desired.Where(w => original.Single(o => o.Path == w.Path).Sha256 != w.Sha256).Select(w => w.Path).ToArray();
        RequireControlFiles(comparison["files"]?.AsArray(), changed);
        var pulls = await SendArrayAsync(path + "/pulls?state=all&head=" + Uri.EscapeDataString(Collector.Owner + ":" + branch) + "&per_page=100", ct);
        if (pulls.Count > 1) throw new InvalidOperationException("Multiple control migration PRs require reconciliation.");
        if (pulls.Count == 1) return pulls[0]!["number"]!.GetValue<long>();
        var pull = await SendAsync(HttpMethod.Post, path + "/pulls", ownerToken, new JsonObject {
            ["title"] = "Install sealed GUI collector control workflows", ["head"] = branch, ["base"] = prepared.CollectorDefaultBranch,
            ["body"] = "Deployment plan " + prepared.PlanSha256 + ". Private collector control plane only; source code runs as lwrunner. Requires normal authorized review and merge. Deployment automation neither approves nor merges this PR." }, ct);
        return pull["number"]!.GetValue<long>();
    }

    static void RequireControlFiles(JsonArray? files, IReadOnlyCollection<string> expected)
    {
        if (files is null || files.Count != expected.Count || files.Count is < 1 or > 2
            || !files.Select(f => f?["filename"]?.GetValue<string>()).Order().SequenceEqual(expected.Order())
            || files.Any(f => f?["status"]?.GetValue<string>() is not ("added" or "modified")))
            throw new InvalidOperationException("Control PR must contain only its exact sealed workflow definition changes.");
    }

    public async Task VerifyReviewedCollectorAsync(long number, Workflow[] expected, CancellationToken ct)
    {
        var path = RepoPath(Collector);
        var pull = await SendAsync(HttpMethod.Get, path + $"/pulls/{number}", ownerToken, null, ct);
        var files = await SendArrayAsync(path + $"/pulls/{number}/files?per_page=100", ct);
        if (pull["changed_files"]?.GetValue<int>() != files.Count || files.Any(f => !expected.Any(w => w.Path == f?["filename"]?.GetValue<string>())))
            throw new InvalidOperationException("Reviewed control PR changes unrelated files.");
        RequireControlFiles(files, files.Select(f => f!["filename"]!.GetValue<string>()).ToArray());
        await VerifyReviewAuthorityAsync(Collector, number, pull, ct);
        var head = pull["head"]!["sha"]!.GetValue<string>();
        foreach (var definition in expected)
            if ((await ReadDefinitionAsync(Collector, definition.Path, head, false, ct)).Sha256 != definition.Sha256)
                throw new InvalidOperationException("Reviewed control head does not contain the exact sealed definitions.");
    }

    async Task VerifyReviewAuthorityAsync(RepoRef repo, long number, JsonObject pull, CancellationToken ct)
    {
        if (pull["merged"]?.GetValue<bool>() != true
            || !string.Equals(pull["base"]?["repo"]?["full_name"]?.GetValue<string>(), repo.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Migration PR must first complete normal authorized review and merge; deployment never merges it.");
        var head = pull["head"]?["sha"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing reviewed head.");
        if (head.Length != 40 || head.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Reviewed head is not immutable.");
        var mergedBy = pull["merged_by"];
        var login = mergedBy?["login"]?.GetValue<string>();
        if (mergedBy?["type"]?.GetValue<string>() != "User" || string.IsNullOrWhiteSpace(login))
            throw new InvalidOperationException("An actual owner/admin merge is required; factory/model auto-merges do not grant deployment authority.");
        if (string.Equals(login, repo.Owner, StringComparison.OrdinalIgnoreCase)) return;
        if (repo.Is(Collector)) throw new InvalidOperationException("Only the private control repository owner may authorize its deployment merge.");
        var permission = await SendAsync(HttpMethod.Get, RepoPath(repo) + "/collaborators/" + Uri.EscapeDataString(login) + "/permission", ownerToken, null, ct);
        if (permission["permission"]?.GetValue<string>() != "admin")
            throw new InvalidOperationException("Migration was not merged by the owner or an authorized repository admin.");
        // Normal code/adversarial review is performed by the owner/coordinator before this explicit merge. An owner-only
        // PR cannot have a second human writer approve its author; never fabricate an independent formal approval here.
    }
}
