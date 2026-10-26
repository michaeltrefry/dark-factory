using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator;

/// <summary>Explicit owner deployment, never hosted in the factory service. prepare performs authenticated GETs only;
/// apply publishes a normal migration PR or runs the explicitly authorized local installer; resume reconciles each boundary.</summary>
public static class GuiCollectorCli
{
    public sealed record Request(string Action, string Directory, string? Plan, string? Archive, string? Configuration, string OldRoot, string Checkout);
    public sealed record HostedState(GuiCollectorDeployment.HostedPlan Plan, long? PullRequest = null);
    public sealed record Prepared(string PlanFile, string Checkout, GuiCollectorDeployment.Evidence Original,
        GuiCollectorDeployment.Workflow[] Migrated, long? MigrationPullRequest = null, long? RegisteredRunnerId = null, bool InventoryVerified = false,
        long? CollectorPullRequest = null, HostedState[]? Hosted = null);

    public static Command Build(Func<Request, CancellationToken, Task<int>>? execute = null)
    {
        var root = new Command("gui-collector", "Owner-only GUI deployment: read-only prepare, then explicit apply/resume of exact reviewed compiled payloads through bounded-CI owner-authorized PRs and one-time root setup.");
        foreach (var action in new[] { "prepare", "apply", "resume" })
        {
            var directory = new Option<string>("--directory") {
                DefaultValueFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dark-factory", "gui-collector-deployment"),
                Description = "Private owner deployment directory (0700); stable prepare/apply/resume journal, never a service credential store" };
            var plan = new Option<string?>("--plan") { Description = "Existing installer plan to stage; alternatively prepare generates one from archive/configuration and exact old inventory" };
            var archive = new Option<string?>("--archive") { Description = "Already downloaded verified actions runner tar.gz; never executed during preparation" };
            var configuration = new Option<string?>("--configuration") { Description = "Pinned GUI helper configuration JSON (runtime, Xcode/build, allowlisted repositories); identity/path fields are generated" };
            var old = new Option<string>("--old-root") { DefaultValueFactory = _ => "/Users/lwrunner/actions-runner" };
            var checkout = new Option<string>("--checkout") { DefaultValueFactory = _ => System.IO.Directory.GetCurrentDirectory(), Description = "Compatibility provenance path only; privileged installer/helper/client code always comes from this compiled factory, never this path" };
            var command = new Command(action, action == "prepare" ? "Stage a unique plan, exact local inventory and authenticated remote evidence; no token mint, root or API write"
                : "Explicit owner authorization: exact generated PRs with bounded head CI and owner merges, then register/verify/retire and activate the isolated collector") { directory, plan, archive, configuration, old, checkout };
            command.SetAction((parse, ct) => (execute ?? ExecuteAsync)(new(action, parse.GetValue(directory)!, parse.GetValue(plan), parse.GetValue(archive),
                parse.GetValue(configuration), parse.GetValue(old)!, parse.GetValue(checkout)!), ct));
            root.Subcommands.Add(command);
        }
        return root;
    }

    static async Task<int> ExecuteAsync(Request request, CancellationToken ct)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("GUI collector deployment requires macOS.");
            var directory = Path.GetFullPath(request.Directory);
            var journal = Path.Combine(directory, "deployment.json");
            if (request.Action != "prepare" && (!System.IO.Directory.Exists(directory)
                || new DirectoryInfo(directory).LinkTarget is not null
                || (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
                throw new InvalidOperationException("Deployment directory must remain a private owner directory (0700), never a symlink.");
            using var lease = request.Action == "prepare" ? null : new FileStream(Path.Combine(directory, "deployment.lock"),
                new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            var settings = FactoryOptions.LoadConfiguration();
            var options = new FactoryOptions(settings, new MacKeychain());
            var reservedRepository = RequestedCollector(request, journal, settings["Gate:Gui:CollectorRepo"]);
            using var http = OutboundHttp.GitHubApi();
            var owner = GitHubCli.AuthToken() ?? throw new InvalidOperationException("Sign the owner into gh first; its admin token is never persisted or sent to a service.");
            var app = new GitHubApp(http, options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem, TimeProvider.System, reservedController: reservedRepository);
            var gateway = new GuiCollectorDeployment(http, app, owner, TimeProvider.System, controlRepository: reservedRepository);
            if (request.Action == "prepare")
            {
                if (System.IO.Directory.Exists(directory) || File.Exists(directory))
                    throw new InvalidOperationException("Use a fresh deployment directory; never overwrite a prepared plan.");
                System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                await Worker.WorkerSandbox.CloseDirectoryToOwnerAsync(directory, ct);
                // Kept in the journal for CLI compatibility only; privileged code/payloads are compiled resources,
                // never scripts read from this path or from the caller's potentially source-writable current directory.
                var checkout = Path.GetFullPath(request.Checkout);
                JsonObject plan;
                GuiCollectorArchive.VerifiedSnapshot verifiedArchive;
                if (request.Plan is not null)
                {
                    plan = JsonNode.Parse(ReadRegular(request.Plan))!.AsObject();
                    await GuiCollectorHost.VerifyAsync(plan["configuration"]!.AsObject(), ct);
                    var verified = await GuiCollectorArchive.AcquireAsync(http, directory, plan["archive"]!.GetValue<string>(), ct);
                    verifiedArchive = await GuiCollectorArchive.VerifySnapshotAsync(verified, ct);
                    plan["archive"] = verifiedArchive.Path;
                }
                else
                {
                    var host = request.Configuration is null ? GuiCollectorHost.DefaultConfiguration(reservedRepository.Owner) : JsonNode.Parse(ReadRegular(request.Configuration))!.AsObject();
                    host["collectorRepository"] = reservedRepository.FullName;
                    await GuiCollectorHost.VerifyAsync(host, ct);
                    var verified = await GuiCollectorArchive.AcquireAsync(http, directory, request.Archive, ct);
                    verifiedArchive = await GuiCollectorArchive.VerifySnapshotAsync(verified, ct);
                    var oldRunner = (await gateway.RunnersAsync(GuiCollectorDeployment.SourceRepository, ct))
                        .SingleOrDefault(r => r.Name == "lensworks-guest")
                        ?? throw new InvalidOperationException("The exact old lensworks-guest registration is absent; prepare never chooses another registration.");
                    plan = MakePlan(request with { Archive = verifiedArchive.Path }, checkout, oldRunner, host, verifiedArchive.Sha256);
                }
                var trusted = plan["configuration"]!["trustedRepositories"] as JsonArray
                    ?? throw new InvalidOperationException("Collector configuration requires its explicit trusted repository list.");
                foreach (var required in new[] { GuiCollectorDeployment.SourceRepository.FullName, gateway.FixtureRepository.FullName })
                    if (!trusted.Any(r => string.Equals(r?.GetValue<string>(), required, StringComparison.OrdinalIgnoreCase))) trusted.Add(required);
                // All root inputs live in one private owner directory. Root snapshots them with openat/no-follow before use.
                var archivePath = verifiedArchive.Path;
                var helperPath = Path.Combine(directory, "factory-gui-exec");
                if (verifiedArchive.Sha256 != plan["archiveSha256"]!.GetValue<string>())
                    throw new InvalidOperationException("Runner archive does not match the plan's pinned hash.");
                // Acquire already made a fresh owner-private snapshot. Reuse its streamed size/hash proof and canonical
                // path for root's handoff; never allocate or copy another whole 512 MiB archive into memory.
                var helper = System.Text.Encoding.UTF8.GetBytes(RemoteGuiWorkflow.Helper);
                if (plan["helperSha256"]?.GetValue<string>() is { } helperHash && helperHash != GuiCollectorDeployment.Hash(helper))
                    throw new InvalidOperationException("Plan helper checksum differs from this reviewed checkout.");
                WritePrivate(helperPath, helper);
                var sourceJob = System.Text.Encoding.UTF8.GetBytes(RemoteGuiWorkflow.SourceJob);
                if (plan["sourceJobSha256"]?.GetValue<string>() is { } sourceHash && sourceHash != GuiCollectorDeployment.Hash(sourceJob))
                    throw new InvalidOperationException("Plan source-client checksum differs from this compiled reviewed runtime.");
                var sourceJobPath = Path.Combine(directory, "factory-gui-source-job");
                WritePrivate(sourceJobPath, sourceJob);
                plan["archive"] = archivePath;
                plan["helper"] = helperPath;
                plan["helperSha256"] = GuiCollectorDeployment.Hash(helper);
                plan["sourceJob"] = sourceJobPath;
                plan["sourceJobSha256"] = GuiCollectorDeployment.Hash(sourceJob);
                var planFile = Path.Combine(directory, "plan.json");
                var planBytes = JsonSerializer.SerializeToUtf8Bytes(plan, GuiCollectorDeployment.Json);
                WritePrivate(planFile, planBytes);
                // Root-owned/source-owned 0600 credentials cannot be inventoried by the owner. A draft carries no
                // invented hashes: explicit apply obtains a read-only root snapshot before any registration authority.
                var evidence = await gateway.InspectAsync(planBytes, ct);
                var name = plan["runnerName"]!.GetValue<string>();
                if (evidence.Runners.Any(r => r.Name == name)) throw new InvalidOperationException("New runner name already exists.");
                var oldId = plan["old"]!["runnerId"]!.GetValue<long>();
                if (!(evidence.OldRunners ?? evidence.Runners).Any(r => r.Id == oldId && r.Name == plan["old"]!["runnerName"]!.GetValue<string>()))
                    throw new InvalidOperationException("Exact old runner is absent from authenticated inventory; prepare cannot adopt or guess one.");
                var migrated = evidence.Workflows.Select(w => {
                    var content = GuiSourceWorkflow.Migrate(w.Path, w.Content, evidence.Repository, evidence.DefaultBranch);
                    return new GuiCollectorDeployment.Workflow(w.Path, GuiCollectorDeployment.Hash(content), content);
                }).ToArray();
                Save(journal, new Prepared(planFile, checkout, evidence, migrated));
                Console.WriteLine("Prepared read-only remote evidence and migration in " + directory + ". No authority receipt, API write, registration token or root operation was produced. Exact private local inventory is deferred to explicit apply's authorized read-only root snapshot, before publishing the migration PR.");
                return 0;
            }
            var prepared = JsonSerializer.Deserialize<Prepared>(ReadRegular(journal), GuiCollectorDeployment.Json)
                ?? throw new InvalidOperationException("Invalid deployment journal.");
            var currentPlan = ReadRegular(prepared.PlanFile);
            if (GuiCollectorDeployment.Hash(currentPlan) != prepared.Original.PlanSha256)
                throw new InvalidOperationException("Prepared plan changed; refuse to resume under another plan.");
            var parsed = JsonNode.Parse(currentPlan)!.AsObject();
            if (!prepared.InventoryVerified)
            {
                var inventory = await GuiCollectorDeployment.InvokeInventoryAsync(prepared.Checkout, parsed["old"]!["runnerRoot"]!.GetValue<string>(), ct);
                if (inventory["repository"]?.GetValue<string>()?.ToLowerInvariant() != GuiCollectorDeployment.SourceRepository.Key)
                    throw new InvalidOperationException("Root inventory names another old registration repository; refusing to select a different ID namespace.");
                var actualOld = inventory["old"]!.AsObject();
                var expectedOld = parsed["old"]!.AsObject();
                if (actualOld["runnerId"]!.GetValue<long>() != expectedOld["runnerId"]!.GetValue<long>()
                    || actualOld["runnerName"]!.GetValue<string>() != expectedOld["runnerName"]!.GetValue<string>()
                    || actualOld["runnerRoot"]!.GetValue<string>() != expectedOld["runnerRoot"]!.GetValue<string>()
                    || (expectedOld["files"] is JsonObject pinned && pinned.Count != 0 && !JsonNode.DeepEquals(expectedOld, actualOld)))
                    throw new InvalidOperationException("Exact root inventory differs from the prepared ID/name/path or pinned hashes.");
                parsed["old"] = actualOld.DeepClone();
                currentPlan = JsonSerializer.SerializeToUtf8Bytes(parsed, GuiCollectorDeployment.Json);
                var verifiedPlanFile = Path.Combine(directory, "plan-" + GuiCollectorDeployment.Hash(currentPlan) + ".json");
                WritePrivate(verifiedPlanFile, currentPlan);
                var rebound = await gateway.InspectAsync(currentPlan, ct);
                if (rebound.Runners.Any(r => r.Name == parsed["runnerName"]!.GetValue<string>())
                    || rebound.Workflows.Any(w => prepared.Original.Workflows.Single(o => o.Path == w.Path).Sha256 != w.Sha256))
                    throw new InvalidOperationException("Remote reservation or source workflows changed before root inventory completed; re-prepare.");
                prepared = prepared with { PlanFile = verifiedPlanFile, Original = rebound, InventoryVerified = true };
                Save(journal, prepared);
            }
            // Re-render the migration from the recorded originals using this checkout's trusted protocol, never accept caller-supplied hashes.
            if (prepared.Migrated.Length != 4 || !prepared.Original.Workflows.Select(w => w.Path).Order().SequenceEqual(GuiCollectorDeployment.WorkflowPaths.Order()))
                throw new InvalidOperationException("Prepared workflow inventory changed.");
            foreach (var original in prepared.Original.Workflows)
            {
                var expected = GuiSourceWorkflow.Migrate(original.Path, original.Content, prepared.Original.Repository, prepared.Original.DefaultBranch);
                var recorded = prepared.Migrated.Single(w => w.Path == original.Path);
                if (GuiCollectorDeployment.Hash(original.Content) != original.Sha256 || recorded.Content != expected || recorded.Sha256 != GuiCollectorDeployment.Hash(expected))
                    throw new InvalidOperationException("Prepared migration does not match the trusted source protocol renderer.");
            }
            var trustedRepositories = parsed["configuration"]!["trustedRepositories"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();
            GuiCollectorSettings.CheckEnvironment(prepared.Original.Repository, trustedRepositories);
            var fresh = await gateway.InspectAsync(currentPlan, ct);
            if (!fresh.CollectorExists || !fresh.AppInstallationVerified)
            {
                await gateway.ProvisionControlRepositoryAsync(ct);
                fresh = await gateway.InspectAsync(currentPlan, ct);
            }
            if (fresh.DefaultBranch != prepared.Original.DefaultBranch)
                throw new InvalidOperationException("Source default branch changed; prepare a new reviewed migration rather than dispatch from an unprotected ref.");
            if (!prepared.Original.CollectorExists)
            {
                prepared = prepared with { Original = prepared.Original with {
                    CollectorExists = fresh.CollectorExists, CollectorPrivateOwnerOnly = fresh.CollectorPrivateOwnerOnly,
                    AppInstallationVerified = fresh.AppInstallationVerified, CollectorDefaultBranch = fresh.CollectorDefaultBranch,
                    CollectorDefaultCommit = fresh.CollectorDefaultCommit, CollectorWorkflows = fresh.CollectorWorkflows } };
                Save(journal, prepared);
            }
            var controlMatches = GuiCollectorDeployment.CollectorDefinitions.All(d => fresh.CollectorWorkflows.Any(w => w.Path == d.Path && w.Sha256 == d.Sha256 && w.Content == d.Content));
            if (!controlMatches)
            {
                if (fresh.CollectorWorkflows.Any(w => prepared.Original.CollectorWorkflows.Single(o => o.Path == w.Path).Sha256 != w.Sha256))
                    throw new InvalidOperationException("Control default definitions changed concurrently; re-prepare rather than overwrite them.");
                var controlPull = await gateway.CollectorPullRequestAsync(prepared.Original, ct);
                prepared = prepared with { CollectorPullRequest = controlPull };
                Save(journal, prepared);
                var merged = await gateway.MergeGeneratedPullAsync(prepared.Original, prepared.Migrated, controlPull, source: false, ct);
                Console.WriteLine(merged.Reason);
                if (!merged.Merged) return 2;
                fresh = await gateway.InspectAsync(currentPlan, ct);
            }
            if (prepared.CollectorPullRequest is { } controlReview)
                await gateway.VerifyReviewedCollectorAsync(controlReview, GuiCollectorDeployment.CollectorDefinitions, ct);
            var workerApp = new GitHubApp(http, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System, reservedController: reservedRepository);
            var hostedStates = (prepared.Hosted ?? []).ToList();
            foreach (var trusted in trustedRepositories.Select(RepoRef.Parse).DistinctBy(r => r.Key))
            {
                var hosted = await gateway.PrepareHostedRepositoryAsync(trusted, workerApp, ct);
                if (trusted.Is(GuiCollectorDeployment.SourceRepository)) continue; // Installed atomically with the four legacy migrations below.
                var state = hostedStates.SingleOrDefault(h => RepoRef.Parse(h.Plan.Repository).Is(trusted));
                if (state is null)
                {
                    state = new HostedState(hosted);
                    hostedStates.Add(state);
                    prepared = prepared with { Hosted = hostedStates.ToArray() };
                    Save(journal, prepared);
                }
                if (hosted.Before.Content != RemoteIosWorkflow.Text)
                {
                    if (hosted.Before.Sha256 != state.Plan.Before.Sha256)
                        throw new InvalidOperationException("A hosted default definition changed outside this deployment; prepare again instead of overwriting it.");
                    var hostedPull = state.PullRequest ?? await gateway.HostedPullRequestAsync(state.Plan, prepared.Original.PlanSha256, ct);
                    var updated = state with { PullRequest = hostedPull };
                    hostedStates[hostedStates.IndexOf(state)] = updated;
                    prepared = prepared with { Hosted = hostedStates.ToArray() };
                    Save(journal, prepared);
                    var merged = await gateway.MergeHostedPullAsync(state.Plan, prepared.Original.PlanSha256, hostedPull, ct);
                    Console.WriteLine(merged.Reason);
                    if (!merged.Merged) return 2;
                    var verified = await gateway.PrepareHostedRepositoryAsync(trusted, workerApp, ct);
                    if (verified.Before.Content != RemoteIosWorkflow.Text)
                        throw new InvalidOperationException("Hosted default did not read back as the exact compiled workflow; no fixture publication is authorized.");
                }
            }
            // Metadata cannot reveal whether an encrypted environment secret contains the current still-valid App key.
            // Explicit apply/resume refreshes that same logical value, after re-verifying its default-only restriction.
            await gateway.ProvisionDispatcherEnvironmentAsync(fresh.DefaultBranch, options.GitHubGateAppPrivateKeyPem, ct);
            fresh = await gateway.InspectAsync(currentPlan, ct);
            var publisherText = GuiSourceWorkflow.Publisher(prepared.Migrated.ToDictionary(w => w.Path, w => w.Content), fresh.Repository, fresh.DefaultBranch);
            var matches = fresh.Workflows.All(w => prepared.Migrated.Single(m => m.Path == w.Path).Sha256 == w.Sha256)
                && fresh.SourcePublisher is { } sourcePublisher && sourcePublisher.Content == publisherText
                && sourcePublisher.Sha256 == GuiCollectorDeployment.Hash(publisherText)
                && fresh.SourceHostedWorkflow is { } hostedWorkflow && hostedWorkflow.Content == RemoteIosWorkflow.Text
                && hostedWorkflow.Sha256 == GuiCollectorDeployment.Hash(RemoteIosWorkflow.Text);
            if (!matches)
            {
                // Every default file must still be exactly what prepare inspected; never overwrite a concurrent workflow change.
                if (fresh.Workflows.Any(w => prepared.Original.Workflows.Single(o => o.Path == w.Path).Sha256 != w.Sha256)
                    || fresh.SourcePublisher?.Sha256 != prepared.Original.SourcePublisher?.Sha256
                    || fresh.SourceHostedWorkflow?.Sha256 != prepared.Original.SourceHostedWorkflow?.Sha256)
                    throw new InvalidOperationException("Default workflow inventory changed; re-prepare and review the new migration.");
                var pull = await gateway.MigrationPullRequestAsync(prepared.Original, prepared.Migrated, ct);
                prepared = prepared with { MigrationPullRequest = pull };
                Save(journal, prepared);
                var merged = await gateway.MergeGeneratedPullAsync(prepared.Original, prepared.Migrated, pull, source: true, ct);
                Console.WriteLine(merged.Reason);
                if (!merged.Merged) return 2;
                fresh = await gateway.InspectAsync(currentPlan, ct);
            }
            if (prepared.MigrationPullRequest is not { } migration)
                throw new InvalidOperationException("No plan-bound reviewed migration PR is recorded; cannot fabricate migration authority.");
            var repo = RepoRef.Parse(fresh.Repository);
            await gateway.VerifyReviewedMigrationAsync(GuiCollectorDeployment.SourceRepository, migration, prepared.Migrated, ct, fresh.DefaultBranch);
            // Private file bytes are never read by this owner process: the authorized root snapshot pinned them,
            // and the root installer revalidates them before its protected backup and exact local retirement.
            var newName = parsed["runnerName"]!.GetValue<string>();
            var authorityPath = Path.Combine(directory, "authority.json");
            var hashes = prepared.Migrated.ToDictionary(w => w.Path, w => w.Sha256);
            var complete = await gateway.CompleteInstallationAsync(currentPlan, prepared.Original, hashes, prepared.RegisteredRunnerId,
                async (authority, registrationToken, c) => {
                    Save(authorityPath, authority);
                    // Registration credential goes directly to root's stdin, never argv/env/files/journal or a service.
                    return await GuiCollectorDeployment.InvokeInstallerAsync(prepared.Checkout, prepared.PlanFile, authorityPath, registrationToken, c);
                }, (runner, _) => {
                    prepared = prepared with { RegisteredRunnerId = runner };
                    Save(journal, prepared);
                    return Task.CompletedTask;
                }, ct);
            var runnerId = complete["runnerId"]!.GetValue<long>();
            var removed = parsed["old"]!["runnerId"]!.GetValue<long>();
            fresh = await gateway.InspectAsync(currentPlan, ct);
            var configurationHash = complete["configurationSha256"]?.GetValue<string>();
            if (configurationHash is null || configurationHash.Length != 64 || configurationHash.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidOperationException("Installer did not attest the installed configuration hash.");
            GuiCollectorSettings.CheckEnvironment(repo.FullName, trustedRepositories, configurationHash);
            await gateway.PinInstalledConfigurationAsync(fresh.DefaultBranch, configurationHash, ct);
            await GuiCollectorSettings.PersistAsync(repo.FullName, trustedRepositories, configurationHash, ct);
            await gateway.PromoteRunnerAsync(currentPlan, complete, ct);
            var completionPath = Path.Combine(directory, "completion.json");
            var firstCompletion = !File.Exists(completionPath);
            Save(completionPath, new { planSha256 = fresh.PlanSha256, repository = repo.FullName,
                runnerId, runnerName = newName, removedOldRunnerId = removed, configurationSha256 = configurationHash });
            Console.WriteLine("Collector registered, authenticated online, exact old registration removed, protected local retirement verified and public factory settings persisted. Deployment complete.");
            if (firstCompletion) Console.WriteLine("Restart an already-running factory host once to load its persisted GUI settings. No per-job flags are needed.");
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only our own fixed messages are public. Input parser / filesystem exceptions may contain confidential data.
            Console.Error.WriteLine(ex is InvalidOperationException ? ex.Message : "GUI collector deployment refused (" + ex.GetType().Name + "); retain the private journal and reconcile before retrying.");
            return 1;
        }
    }

    internal static RepoRef RequestedCollector(Request request, string journal, string? configured)
    {
        string? name;
        if (request.Action != "prepare")
            name = JsonNode.Parse(ReadRegular(journal))?["original"]?["repository"]?.GetValue<string>();
        else if (request.Plan is not null)
            name = JsonNode.Parse(ReadRegular(request.Plan))?["configuration"]?["collectorRepository"]?.GetValue<string>();
        else if (request.Configuration is not null)
            name = JsonNode.Parse(ReadRegular(request.Configuration))?["collectorRepository"]?.GetValue<string>();
        else name = configured ?? RemoteGuiSourceWorkflow.Repository;
        if (name is null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z"))
            throw new InvalidOperationException("Deployment must name its exact configured private collector repository.");
        return RepoRef.Parse(name);
    }

    static JsonObject MakePlan(Request request, string checkout, GuiCollectorDeployment.Runner oldRunner, JsonObject config, string verifiedArchiveSha256)
    {
        if (request.Archive is null) throw new InvalidOperationException("Official runner archive verification is required before constructing the draft plan.");
        var name = "factory-gui-" + Guid.NewGuid().ToString("N");
        var user = "_factoryguicollector";
        var home = "/Users/" + user;
        // The configuration pins the explicitly chosen free uid. Root independently proves availability before reserving it.
        var uid = config["collectorUid"]?.GetValue<int>() ?? 550;
        config["collectorUid"] = uid;
        config["sourceUid"] = 502;
        config["sourceUser"] = "lwrunner";
        config["runnerName"] = name;
        config["runnerRoot"] = home + "/actions-runner";
        config["stateDirectory"] = home + "/gui-state";
        var oldRoot = Path.GetFullPath(request.OldRoot);
        config["retiredRunnerRoots"] = new JsonArray(oldRoot);
        // This is explicitly a draft, not an installer-ready plan or an authority receipt. Private credentials are
        // source-user/root-owned 0600; only explicit apply's read-only root inventory may fill these hashes.
        var files = new JsonObject();
        return new JsonObject {
            ["version"] = 1, ["user"] = user, ["uid"] = uid, ["home"] = home, ["runnerName"] = name,
            ["archive"] = Path.GetFullPath(request.Archive), ["archiveSha256"] = verifiedArchiveSha256,
            ["helper"] = "", ["helperSha256"] = GuiCollectorDeployment.Hash(RemoteGuiWorkflow.Helper), ["configuration"] = config,
            ["old"] = new JsonObject { ["runnerRoot"] = oldRoot, ["runnerId"] = oldRunner.Id,
                ["runnerName"] = oldRunner.Name, ["serviceLabel"] = "", ["servicePlist"] = "", ["files"] = files }
        };
    }

    internal static byte[] ReadRegular(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Deployment input must be a regular file, never a symlink.");
        for (var parent = info.Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new InvalidOperationException("Deployment input parent may not be a symlink; use its canonical path.");
        return File.ReadAllBytes(info.FullName);
    }
    static void Save<T>(string path, T value) => WritePrivate(path, JsonSerializer.SerializeToUtf8Bytes(value, GuiCollectorDeployment.Json));
    static void WritePrivate(string path, byte[] value)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Collector deployment files require Unix owner-private modes.");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        { file.Write(value); file.Flush(true); }
        File.Move(temporary, path, true);
    }
}
