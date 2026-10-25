using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>Owner-only deployment gateway. No worker, dashboard or service receives the owner's credential.
/// A preparation is evidence, not authority: every apply/resume repeats the authenticated reads.
/// GitHub mutations are invoked only by the explicit owner CLI apply/resume path.</summary>
public sealed partial class GuiCollectorDeployment(HttpClient http, GitHubApp gateApp, string ownerToken, TimeProvider time, RepoRef? controlRepository = null)
{
    private RepoRef Collector => controlRepository ?? ControlRepository;
    public const string Label = "factory-gui-collector";
    public const string AuthorityKind = "gui-collector-authority-v1";
    public static string BootstrapLabel(string planHash)
    {
        if (planHash.Length != 64 || planHash.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Bootstrap label requires an exact plan hash.");
        return "factory-gui-bootstrap-" + planHash[..16].ToLowerInvariant();
    }
    public static readonly RepoRef SourceRepository = new("michaeltrefry", "LensWorks");
    public static readonly string[] WorkflowPaths = [
        ".github/workflows/inference-adapter.yml", ".github/workflows/live-raw-acceptance.yml",
        ".github/workflows/rust-catalog-tests.yml", ".github/workflows/s47-procedural-ui-tour.yml"];
    public sealed record Workflow(string Path, string Sha256, string Content);
    public sealed record Runner(long Id, string Name, string Status, string[] Labels);
    public sealed record Evidence(string PlanSha256, string Repository, string DefaultCommit, Workflow[] Workflows, Runner[] Runners, Runner[]? OldRunners = null)
    {
        public string DefaultBranch { get; init; } = "main";
        public bool CollectorExists { get; init; }
        public bool CollectorPrivateOwnerOnly { get; init; }
        public bool AppInstallationVerified { get; init; }
        public string? CollectorDefaultBranch { get; init; }
        public string? CollectorDefaultCommit { get; init; }
        public Workflow[] CollectorWorkflows { get; init; } = [];
        public Workflow? SourcePublisher { get; init; }
        public Workflow? SourceHostedWorkflow { get; init; }
        public bool DispatcherEnvironmentVerified { get; init; }
    }
    public sealed record WorkflowAuthority(string Path, string Sha256, bool CollectorProtocol);
    public sealed record OnlineAuthority(long RunnerId, string RunnerName, string Status, string[] Labels);
    public sealed record Authority(string Kind, string PlanSha256, long ExpiresAt, bool AppPermissionsVerified,
        bool NewNameAbsent, WorkflowAuthority[] MigratedWorkflows, OnlineAuthority? Online = null, long? RemovedOldRunnerId = null);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    async Task<JsonObject> SendAsync(HttpMethod method, string path, string token, JsonNode? body, CancellationToken ct)
    {
        using var request = GitHubApp.Request(method, path, "Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        // Bodies from token endpoints (including failed requests) must never enter errors, logs or receipts.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GUI deployment GitHub {method} failed ({(int)response.StatusCode}); no response body retained.");
        if (response.StatusCode == HttpStatusCode.NoContent) return new JsonObject();
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct))?.AsObject()
            ?? throw new InvalidOperationException("GUI deployment received an invalid API document.");
    }
    static string Text(JsonObject value, string key) => value[key]?.GetValue<string>()
        ?? throw new InvalidOperationException($"GUI deployment API document has no {key}.");
    static string RepoPath(RepoRef repo) => $"repos/{repo.Owner}/{repo.Name}";

    public async Task<Evidence> InspectAsync(byte[] planBytes, CancellationToken ct)
    {
        var plan = JsonNode.Parse(planBytes)?.AsObject() ?? throw new InvalidOperationException("Invalid deployment plan.");
        var config = plan["configuration"]!.AsObject();
        var repositoryName = Text(config, "collectorRepository");
        if (!System.Text.RegularExpressions.Regex.IsMatch(repositoryName, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z"))
            throw new InvalidOperationException("Collector repository must be an exact owner/name.");
        var repo = RepoRef.Parse(repositoryName);
        var path = RepoPath(repo);
        var app = await SendAsync(HttpMethod.Get, "app", gateApp.CreateJwt(), null, ct);
        if (app["permissions"]?["actions"]?.GetValue<string>() != "write")
            throw new InvalidOperationException("The gate App metadata does not grant Actions write. Update and approve the App before deploying.");
        if (!repo.Is(Collector)) throw new InvalidOperationException("Collector repository must be the dedicated private dark-factory-gui-control control plane, never a source/public repository.");
        var repository = await OptionalAsync(path, ownerToken, ct);
        JsonObject? installation = null;
        if (repository is not null)
        {
            await VerifyPrivateControlAsync(repository, ct);
            // Installation approval may lag the App's own metadata. An absent new installation remains a real prerequisite.
            installation = await OptionalAsync(path + "/installation", gateApp.CreateJwt(), ct);
            if (installation is not null && installation["permissions"]?["actions"]?.GetValue<string>() != "write")
                throw new InvalidOperationException("The private collector installation has not approved Actions write.");
        }
        var workflowPath = RepoPath(SourceRepository);
        var sourceRepository = await SendAsync(HttpMethod.Get, workflowPath, ownerToken, null, ct);
        if (sourceRepository["permissions"]?["admin"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The owner needs admin permission on the source workflow repository too.");
        var branch = Text(sourceRepository, "default_branch");
        var tip = await SendAsync(HttpMethod.Get, workflowPath + "/commits/" + Uri.EscapeDataString(branch), ownerToken, null, ct);
        var sha = Text(tip, "sha");
        if (sha.Length != 40 || sha.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Default branch read returned no immutable commit SHA.");
        var workflows = new List<Workflow>();
        foreach (var workflow in WorkflowPaths)
        {
            var file = await SendAsync(HttpMethod.Get, workflowPath + "/contents/" + workflow + "?ref=" + sha, ownerToken, null, ct);
            if (Text(file, "type") != "file" || Text(file, "encoding") != "base64")
                throw new InvalidOperationException("A migration workflow is not a regular UTF-8 file.");
            var bytes = Convert.FromBase64String(Text(file, "content"));
            var content = new UTF8Encoding(false, true).GetString(bytes);
            workflows.Add(new(workflow, Hash(bytes), content));
        }
        string? collectorBranch = null;
        string? collectorCommit = null;
        var collectorWorkflows = new List<Workflow>();
        if (repository is not null)
        {
            collectorBranch = Text(repository, "default_branch");
            collectorCommit = Text(await SendAsync(HttpMethod.Get, path + "/commits/" + Uri.EscapeDataString(collectorBranch), ownerToken, null, ct), "sha");
            if (collectorCommit.Length != 40 || collectorCommit.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidOperationException("Private collector default head is not immutable.");
            foreach (var definition in CollectorDefinitions)
                collectorWorkflows.Add(await ReadDefinitionAsync(repo, definition.Path, collectorCommit, true, ct));
        }
        Runner[] runners = repository is null ? [] : await RunnersAsync(repo, ct);
        var oldRunners = await RunnersAsync(SourceRepository, ct);
        var old = plan["old"]!.AsObject();
        var oldId = old["runnerId"]!.GetValue<long>();
        var oldName = Text(old, "runnerName");
        if (oldRunners.Any(r => r.Id == oldId && r.Name != oldName) || oldRunners.Any(r => r.Name == oldName && r.Id != oldId))
            throw new InvalidOperationException("The exact old runner ID/name inventory changed; nothing may be retired.");
        var name = Text(plan, "runnerName");
        if (runners.Count(r => r.Name == name) > 1 || name == oldName)
            throw new InvalidOperationException("The new runner name is not unique and distinct.");
        return new(Hash(planBytes), repo.ToString(), sha, workflows.ToArray(), runners, oldRunners) {
            DefaultBranch = branch, CollectorExists = repository is not null, CollectorPrivateOwnerOnly = repository is not null,
            AppInstallationVerified = installation is not null,
            CollectorDefaultBranch = collectorBranch, CollectorDefaultCommit = collectorCommit, CollectorWorkflows = collectorWorkflows.ToArray(),
            SourcePublisher = await ReadDefinitionAsync(SourceRepository, Gate.GuiSourceWorkflow.PublisherPath, sha, true, ct),
            SourceHostedWorkflow = await ReadDefinitionAsync(SourceRepository, Gate.RemoteIosWorkflow.Path, sha, true, ct),
            DispatcherEnvironmentVerified = await DispatcherEnvironmentAsync(branch, ct)
        };
    }

    public async Task<Runner[]> RunnersAsync(RepoRef repo, CancellationToken ct)
    {
        var result = new List<Runner>();
        for (var page = 1; page <= 100; page++)
        {
            var answer = await SendAsync(HttpMethod.Get, RepoPath(repo) + $"/actions/runners?per_page=100&page={page}", ownerToken, null, ct);
            var runners = answer["runners"]!.AsArray();
            foreach (var node in runners)
            {
                var runner = node!.AsObject();
                result.Add(new(runner["id"]!.GetValue<long>(), Text(runner, "name"), Text(runner, "status"),
                    runner["labels"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray()));
            }
            if (result.Count == answer["total_count"]!.GetValue<int>())
            {
                if (result.Any(r => r.Id <= 0) || result.Select(r => r.Id).Distinct().Count() != result.Count)
                    throw new InvalidOperationException("Runner inventory contains duplicate or invalid IDs; refusing incomplete evidence.");
                return result.ToArray();
            }
            if (runners.Count == 0) break;
        }
        throw new InvalidOperationException("Runner inventory was incomplete; refusing deployment.");
    }

    public Authority Authorize(Evidence evidence, string newName, IReadOnlyDictionary<string, string> expectedHashes, Runner? online = null, long? removed = null,
        Evidence? reservation = null, JsonObject? retiredReceipt = null)
    {
        if (expectedHashes.Count != 4 || !WorkflowPaths.All(expectedHashes.ContainsKey) || evidence.Workflows.Length != 4
            || evidence.Workflows.Any(w => !expectedHashes.TryGetValue(w.Path, out var sha) || sha != w.Sha256
                || !Gate.GuiSourceWorkflow.IsMigrated(w.Path, w.Content, evidence.Repository, evidence.DefaultBranch)))
            throw new InvalidOperationException("The exact four default-branch migration hashes do not match the prepared reviewed migration.");
        var expectedPublisher = Gate.GuiSourceWorkflow.Publisher(evidence.Workflows.ToDictionary(w => w.Path, w => w.Content), evidence.Repository, evidence.DefaultBranch);
        if (evidence.SourcePublisher is not { } publisher || publisher.Path != Gate.GuiSourceWorkflow.PublisherPath
            || publisher.Content != expectedPublisher || publisher.Sha256 != Hash(expectedPublisher))
            throw new InvalidOperationException("The protected default workflow_run publisher is not the exact reviewed companion to the four migrations; changeset-base source CI cannot be authorized.");
        if (evidence.SourceHostedWorkflow is not { } hosted || hosted.Path != Gate.RemoteIosWorkflow.Path
            || hosted.Content != Gate.RemoteIosWorkflow.Text || hosted.Sha256 != Hash(Gate.RemoteIosWorkflow.Text))
            throw new InvalidOperationException("The source repository does not yet carry this compiled sealed hosted iOS workflow; no installation authority is issued.");
        var rootRetired = retiredReceipt?["version"]?.GetValue<int>() == 1 && retiredReceipt?["planSha256"]?.GetValue<string>() == evidence.PlanSha256
            && retiredReceipt?["runnerName"]?.GetValue<string>() == newName && retiredReceipt?["runnerId"]?.GetValue<long>() == online?.Id
            && retiredReceipt?["stages"] is JsonArray retiredStages && retiredStages.Any(s => s?.GetValue<string>() == "old-retired");
        if (online is not null && (online.Name != newName || online.Status != "online"
            || !(online.Labels.SequenceEqual([BootstrapLabel(evidence.PlanSha256)]) || rootRetired && online.Labels.SequenceEqual([Label]))
            || !evidence.Runners.Any(r => r.Id == online.Id && r.Name == online.Name && r.Status == "online" && r.Labels.SequenceEqual(online.Labels))))
            throw new InvalidOperationException("The new collector must be authenticated online with only its plan-bound bootstrap label; the final job label requires verified local retirement.");
        if (online is null && evidence.Runners.Any(r => r.Name == newName)
            && (reservation is null || reservation.PlanSha256 != evidence.PlanSha256 || reservation.Repository != evidence.Repository
                || reservation.Runners.Any(r => r.Name == newName)))
            throw new InvalidOperationException("The new collector name already exists without this plan's absent-name reservation; reconcile its protected installer receipt.");
        if (!RepoRef.Parse(evidence.Repository).Is(Collector) || !evidence.CollectorExists || !evidence.CollectorPrivateOwnerOnly
            || !evidence.AppInstallationVerified || !evidence.DispatcherEnvironmentVerified
            || evidence.CollectorWorkflows.Length != CollectorDefinitions.Length
            || CollectorDefinitions.Any(d => !evidence.CollectorWorkflows.Any(w => w.Path == d.Path && w.Sha256 == d.Sha256 && w.Content == d.Content)))
            throw new InvalidOperationException("The separate private collector, exact sealed definitions, App approval and protected default dispatcher environment must all be freshly verified before installation authority.");
        if (removed is not null && (evidence.OldRunners ?? []).Any(r => r.Id == removed))
            throw new InvalidOperationException("The exact old registration is still present; local retirement is not authorized.");
        return new(AuthorityKind, evidence.PlanSha256, time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(), true, true,
            evidence.Workflows.Select(w => new WorkflowAuthority(w.Path, w.Sha256, true)).ToArray(),
            online is null ? null : new OnlineAuthority(online.Id, online.Name, online.Status, online.Labels), removed);
    }

    /// <summary>Call only after the current authenticated inventory proves the exact replacement is online.
    /// A lost DELETE answer is reconciled by a fresh GET; never broaden the deletion to a name or label.</summary>
    public async Task<long> RetireAsync(RepoRef repo, Runner online, long oldId, string oldName, CancellationToken ct, RepoRef? oldRepository = null)
    {
        oldRepository ??= repo;
        var runners = await RunnersAsync(repo, ct);
        if (!(online.Labels.SequenceEqual([Label]) || online.Labels.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(online.Labels[0], @"\Afactory-gui-bootstrap-[0-9a-f]{16}\z"))
            || !runners.Any(r => r.Id == online.Id && r.Name == online.Name && r.Status == "online" && r.Labels.SequenceEqual(online.Labels)) || online.Id == oldId)
            throw new InvalidOperationException("Replacement online proof changed; the old registration is untouched.");
        var oldRunners = oldRepository.Is(repo) ? runners : await RunnersAsync(oldRepository, ct);
        var old = oldRunners.SingleOrDefault(r => r.Id == oldId);
        if (old is null) return oldId; // an earlier exact deletion may have completed before a crash
        if (old.Name != oldName) throw new InvalidOperationException("Old runner ID/name mismatch; refusing deletion.");
        try { await SendAsync(HttpMethod.Delete, RepoPath(oldRepository) + $"/actions/runners/{oldId}", ownerToken, null, ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            if ((await RunnersAsync(oldRepository, ct)).Any(r => r.Id == oldId)) throw;
        }
        if ((await RunnersAsync(oldRepository, ct)).Any(r => r.Id == oldId))
            throw new InvalidOperationException("Old registration still exists; local retirement is not authorized.");
        return oldId;
    }

    /// <summary>Publish a deterministic migration branch and open a normal PR. Never merges or approves it.
    /// A crash after a file update resumes only while every branch file is either the prepared original or its replacement.</summary>
    public async Task<long> MigrationPullRequestAsync(Evidence original, Workflow[] desired, CancellationToken ct)
    {
        if (desired.Length != 4 || !desired.Select(w => w.Path).Order().SequenceEqual(WorkflowPaths.Order()))
            throw new InvalidOperationException("Migration inventory must be exactly the four source workflows.");
        var repo = SourceRepository;
        var path = RepoPath(repo);
        var branch = "factory/gui-collector-" + original.PlanSha256[..16];
        // List refs rather than interpreting a 404/error as permission to create.
        var refs = await SendArrayAsync(path + "/git/matching-refs/heads/" + branch, ct);
        if (!refs.Any(n => n?["ref"]?.GetValue<string>() == "refs/heads/" + branch))
            await SendAsync(HttpMethod.Post, path + "/git/refs", ownerToken,
                new JsonObject { ["ref"] = "refs/heads/" + branch, ["sha"] = original.DefaultCommit }, ct);
        var publisherText = Gate.GuiSourceWorkflow.Publisher(desired.ToDictionary(w => w.Path, w => w.Content), original.Repository, original.DefaultBranch);
        var publisher = new Workflow(Gate.GuiSourceWorkflow.PublisherPath, Hash(publisherText), publisherText);
        var hosted = new Workflow(Gate.RemoteIosWorkflow.Path, Hash(Gate.RemoteIosWorkflow.Text), Gate.RemoteIosWorkflow.Text);
        var allDesired = desired.Append(publisher).Append(hosted).ToArray();
        var allOriginal = original.Workflows.Append(original.SourcePublisher ?? new Workflow(Gate.GuiSourceWorkflow.PublisherPath, "", ""))
            .Append(original.SourceHostedWorkflow ?? new Workflow(Gate.RemoteIosWorkflow.Path, "", "")).ToArray();
        foreach (var replacement in allDesired)
        {
            var file = await OptionalAsync(path + "/contents/" + replacement.Path + "?ref=" + branch, ownerToken, ct);
            var hash = file is null ? "" : Hash(Convert.FromBase64String(Text(file, "content")));
            if (hash == replacement.Sha256) continue;
            if (hash != allOriginal.Single(w => w.Path == replacement.Path).Sha256)
                throw new InvalidOperationException("Migration branch was edited outside this exact plan; refusing to overwrite it.");
            var body = new JsonObject {
                ["message"] = "Route the four source jobs through isolated collector and trusted default publisher",
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(replacement.Content)), ["branch"] = branch };
            if (file is not null) body["sha"] = Text(file, "sha");
            await SendAsync(HttpMethod.Put, path + "/contents/" + replacement.Path, ownerToken, body, ct);
        }
        var comparison = await SendAsync(HttpMethod.Get, path + "/compare/" + original.DefaultCommit + "..." + branch, ownerToken, null, ct);
        var changed = allDesired.Where(w => allOriginal.Single(o => o.Path == w.Path).Sha256 != w.Sha256).Select(w => w.Path).ToArray();
        RequireSourceFiles(comparison["files"]?.AsArray(), changed);
        var repository = await SendAsync(HttpMethod.Get, path, ownerToken, null, ct);
        var pulls = await SendArrayAsync(path + "/pulls?state=all&head=" + Uri.EscapeDataString(repo.Owner + ":" + branch) + "&per_page=100", ct);
        if (pulls.Count > 1) throw new InvalidOperationException("Migration has multiple pull requests; reconcile before continuing.");
        if (pulls.Count == 1) return pulls[0]!["number"]!.GetValue<long>();
        var pull = await SendAsync(HttpMethod.Post, path + "/pulls", ownerToken, new JsonObject {
            ["title"] = "Migrate GUI source workflows to isolated factory collector",
            ["head"] = branch, ["base"] = Text(repository, "default_branch"),
            ["body"] = "Owner deployment plan " + original.PlanSha256 + ". Migrates exactly four existing source workflows and adds their protected default workflow_run publisher so changeset-base PRs retain actual-head CI. "
                + "Requires normal authorized review and merge; deployment automation neither approves nor merges this PR." }, ct);
        return pull["number"]!.GetValue<long>();
    }

    static void RequireSourceFiles(JsonArray? files, IReadOnlyCollection<string> expected)
    {
        if (files is null || files.Count != expected.Count || files.Count is < 1 or > 6 || expected.Distinct(StringComparer.Ordinal).Count() != expected.Count
            || !files.Select(f => f?["filename"]?.GetValue<string>()).Order().SequenceEqual(expected.Order())
            || files.Any(f => {
                var name = f?["filename"]?.GetValue<string>();
                var status = f?["status"]?.GetValue<string>();
                return name == Gate.GuiSourceWorkflow.PublisherPath || name == Gate.RemoteIosWorkflow.Path ? status is not ("added" or "modified")
                    : name is null || !WorkflowPaths.Contains(name, StringComparer.Ordinal) || status != "modified";
            })) throw new InvalidOperationException("Source PR may change only the exact four legacy workflows and their reviewed default publisher companion.");
    }

    async Task<JsonArray> SendArrayAsync(string path, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, path, "Bearer", ownerToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GUI deployment GitHub GET failed ({(int)response.StatusCode}).");
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct))?.AsArray()
            ?? throw new InvalidOperationException("Expected GitHub list.");
    }

    public async Task VerifyReviewedMigrationAsync(RepoRef repo, long pullNumber, Workflow[] expected, CancellationToken ct, string defaultBranch = "main")
    {
        if (!repo.Is(SourceRepository)) throw new InvalidOperationException("Legacy workflow migration proof belongs only to the known LensWorks source repository.");
        var path = RepoPath(repo);
        var pull = await SendAsync(HttpMethod.Get, path + $"/pulls/{pullNumber}", ownerToken, null, ct);
        if (pull["merged"]?.GetValue<bool>() != true || pull["base"]?["repo"]?["full_name"]?.GetValue<string>()?.ToLowerInvariant() != repo.Key)
            throw new InvalidOperationException("The migration PR must first be normally reviewed and merged; automation never merges it.");
        var files = await SendArrayAsync(path + $"/pulls/{pullNumber}/files?per_page=100", ct);
        if (pull["changed_files"]?.GetValue<int>() != files.Count)
            throw new InvalidOperationException("Reviewed source migration diff inventory is incomplete.");
        RequireSourceFiles(files, files.Select(f => f!["filename"]!.GetValue<string>()).ToArray());
        var head = pull["head"]?["sha"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing migration PR head.");
        await VerifyReviewAuthorityAsync(repo, pullNumber, pull, ct);
        foreach (var workflow in expected)
        {
            var file = await SendAsync(HttpMethod.Get, path + "/contents/" + workflow.Path + "?ref=" + head, ownerToken, null, ct);
            if (Hash(Convert.FromBase64String(Text(file, "content"))) != workflow.Sha256)
                throw new InvalidOperationException("Reviewed migration head does not contain the exact prepared workflows.");
        }
        var publisher = Gate.GuiSourceWorkflow.Publisher(expected.ToDictionary(w => w.Path, w => w.Content), Collector.FullName, defaultBranch);
        if ((await ReadDefinitionAsync(SourceRepository, Gate.GuiSourceWorkflow.PublisherPath, head, false, ct)).Sha256 != Hash(publisher))
            throw new InvalidOperationException("Reviewed source head does not contain its exact trusted workflow_run publisher.");
        if ((await ReadDefinitionAsync(SourceRepository, Gate.RemoteIosWorkflow.Path, head, false, ct)).Sha256 != Hash(Gate.RemoteIosWorkflow.Text))
            throw new InvalidOperationException("Reviewed source head does not contain this exact compiled hosted iOS workflow.");
    }

    internal static System.Diagnostics.ProcessStartInfo InstallerStartInfo(string bootstrapScript, string plan, string authority)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/sudo") {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = "/" };
        foreach (var argument in new[] { "/usr/bin/python3", "-I", bootstrapScript,
            "--apply", "--plan", plan, "--authority", authority }) start.ArgumentList.Add(argument);
        // sudo reads an owner password from the controlling terminal, never from the registration-token pipe.
        // No owner gh/PAT/token environment is inherited by either Python or the demoted runner.
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        return start;
    }

    internal static System.Diagnostics.ProcessStartInfo InventoryStartInfo(string bootstrapScript, string oldRoot)
    {
        var start = InstallerStartInfo(bootstrapScript, "", "");
        while (start.ArgumentList.Count > 3) start.ArgumentList.RemoveAt(3);
        start.ArgumentList.Add("--inventory");
        start.ArgumentList.Add("--old-root");
        start.ArgumentList.Add(oldRoot);
        return start;
    }

    public static async Task<JsonObject> InvokeInventoryAsync(string checkout, string oldRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Caller checkout is intentionally ignored. Only compiled, freshly staged owner-private code executes as root.
        using var bootstrap = await StageBootstrapAsync(ct);
        using var process = System.Diagnostics.Process.Start(InventoryStartInfo(bootstrap.Script, oldRoot))
            ?? throw new InvalidOperationException("Cannot start explicitly authorized read-only inventory.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await stdout;
        await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Read-only root inventory was refused or root authorization was not granted. No registration authority was issued.");
        var result = JsonNode.Parse(output)?.AsObject() ?? throw new InvalidOperationException("Invalid inventory receipt.");
        if (result["version"]?.GetValue<int>() != 1 || result["status"]?.GetValue<string>() != "inventory" || result["old"] is not JsonObject)
            throw new InvalidOperationException("Inventory did not return exact old registration metadata.");
        return result;
    }

    public static async Task<JsonObject> InvokeInstallerAsync(string checkout, string plan, string authority, string registrationToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Never elevate a script from caller CWD or --checkout, even when that tree looks like this repository.
        using var bootstrap = await StageBootstrapAsync(ct);
        using var process = System.Diagnostics.Process.Start(InstallerStartInfo(bootstrap.Script, plan, authority))
            ?? throw new InvalidOperationException("Cannot start explicitly authorized installer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.StandardInput.WriteLineAsync(registrationToken); }
        finally { process.StandardInput.Close(); }
        registrationToken = "";
        // Never leave an authorized root installer racing a second resume after cancelling its caller.
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await stdout;
        await stderr; // never relay runner/sudo errors, which may contain credentials
        if (process.ExitCode != 0) throw new InvalidOperationException("Local installer refused or root authorization was not granted. Old retirement is not assumed; retain the deployment journal and resume.");
        var result = JsonNode.Parse(output)?.AsObject() ?? throw new InvalidOperationException("Invalid installer receipt.");
        if (result["status"]?.GetValue<string>() is not ("awaiting-owner-online-verification-old-untouched" or "awaiting-owner-exact-old-registration-removal" or "complete"))
            throw new InvalidOperationException("Installer returned no accepted stage receipt.");
        return result;
    }

    /// <summary>Ephemeral registration credential stays in memory and is passed to the installer on stdin only.</summary>
    public async Task<string> RegistrationTokenAsync(RepoRef repo, CancellationToken ct)
    {
        var token = await SendAsync(HttpMethod.Post, RepoPath(repo) + "/actions/runners/registration-token", ownerToken, null, ct);
        var expires = DateTimeOffset.Parse(Text(token, "expires_at"), System.Globalization.CultureInfo.InvariantCulture);
        if (expires <= time.GetUtcNow() || expires > time.GetUtcNow().AddHours(1).AddMinutes(1))
            throw new InvalidOperationException("Registration token has an invalid lifetime.");
        var value = Text(token, "token");
        if (value.Length is < 10 or > 2048 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new InvalidOperationException("Invalid registration credential.");
        return value;
    }
}
