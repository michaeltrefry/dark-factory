using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>Versioned adapter for the four existing LensWorks source jobs. The credentialed collector executes only the
/// fixed installed client. Original bash programs are data to that client, which must hold one shared GUI lease and execute
/// every program as lwrunner against an immutable, credential-free source archive. Unknown Actions semantics fail closed.
/// Source workflows run only the compiled dispatcher on hosted Ubuntu from a protected default environment. The credentialed
/// collector is in a separate repository; source PRs cannot replace its workflow or obtain the dispatch environment's App key.</summary>
public static class GuiSourceWorkflow
{
    public const string Client = "/usr/local/libexec/dark-factory/factory-gui-source-job";
    public const string Protocol = "factory-gui-source-v1";
    public const string DefaultRepository = RemoteGuiSourceWorkflow.Repository;
    public sealed record SourceStep(string Name, string? Id, string Run, string Condition, Dictionary<string, string> Env, int TimeoutMinutes);
    public sealed record SourceArtifact(string Name, string[] Paths, string Condition, string IfNoFilesFound, int RetentionDays);
    public sealed record JobPlan(int Version, string Workflow, string JobId, int TimeoutMinutes, string Submodules,
        Dictionary<string, string> Env, SourceStep[] Steps, SourceArtifact[] Artifacts);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly IReadOnlyDictionary<string, string> Jobs = new Dictionary<string, string> {
        [".github/workflows/inference-adapter.yml"] = "inference",
        [".github/workflows/live-raw-acceptance.yml"] = "live-raw",
        [".github/workflows/rust-catalog-tests.yml"] = "cargo-test",
        [".github/workflows/s47-procedural-ui-tour.yml"] = "deterministic-fixtures-and-tour"
    };

    public static string Migrate(string path, string originalYaml, string collectorRepository = DefaultRepository, string defaultBranch = "main")
    {
        Require(Jobs.ContainsKey(path), "Unknown source workflow; only the exact four inventoried LensWorks jobs may migrate.");
        Require(ValidCollector(collectorRepository), "Source dispatch requires a distinct configured private collector repository.");
        Require(Regex.IsMatch(defaultBranch, @"\A[A-Za-z0-9_][A-Za-z0-9_./-]{0,200}\z"), "Unsupported default branch name.");
        if (IsMigrated(path, originalYaml, collectorRepository, defaultBranch)) return originalYaml;
        var root = Parse(originalYaml);
        Only(root, ["name", "on", "permissions", "concurrency", "jobs"]);
        var jobs = Mapping(Child(root, "jobs"));
        Require(jobs.Children.Count == 1 && jobs.Children.ContainsKey(new YamlScalarNode(Jobs[path])), "Source job inventory changed; re-evaluate the migration.");
        var job = Mapping(Child(jobs, Jobs[path]));
        Only(job, ["runs-on", "timeout-minutes", "env", "steps"]);
        var labels = Child(job, "runs-on") as YamlSequenceNode;
        Require(labels is not null && labels.Children.Select(Scalar).Order().SequenceEqual(new[] { "ARM64", "macOS", "self-hosted" }.Order()),
            "Expected the exact old generic macOS runner selection, never an arbitrary runner or reusable job.");
        var timeout = Number(Child(job, "timeout-minutes"), 1, 180);
        var env = Environment(Child(job, "env"), jobLevel: true);
        var steps = Child(job, "steps") as YamlSequenceNode ?? throw Refused("Missing source steps.");
        var programs = new List<SourceStep>();
        var artifacts = new List<SourceArtifact>();
        string? submodules = null;
        foreach (var node in steps.Children)
        {
            var step = Mapping(node);
            if (Child(step, "uses") is { } uses)
            {
                var action = Scalar(uses);
                if (action == "actions/checkout@v4")
                {
                    Only(step, ["uses", "with"]);
                    Require(submodules is null && programs.Count == 0 && artifacts.Count == 0, "Checkout must occur exactly once before source programs.");
                    var settings = Mapping(Child(step, "with"));
                    Only(settings, ["submodules"]);
                    submodules = Scalar(Child(settings, "submodules"));
                    Require(submodules is "recursive" or "false", "Unknown checkout/submodule semantics.");
                }
                else if (action == "actions/upload-artifact@v4")
                {
                    Only(step, ["name", "if", "uses", "with"]);
                    Require(Condition(Child(step, "if")) == "always()", "Existing artifact collection must keep always() semantics.");
                    var settings = Mapping(Child(step, "with"));
                    Only(settings, ["name", "path", "if-no-files-found", "retention-days"]);
                    var name = Scalar(Child(settings, "name"));
                    Require(Regex.IsMatch(name, @"\A[a-zA-Z0-9_-]{1,100}\z"), "Unsupported artifact name.");
                    var paths = Scalar(Child(settings, "path")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(SourcePath).ToArray();
                    Require(paths.Length is > 0 and <= 20, "Artifact path inventory exceeds its bound.");
                    var missing = Scalar(Child(settings, "if-no-files-found"));
                    Require(missing is "ignore" or "warn" or "error", "Unknown missing-artifact behavior.");
                    artifacts.Add(new(name, paths, "always()", missing, Number(Child(settings, "retention-days"), 1, 30)));
                }
                else throw Refused("Unknown action; migration never runs third-party or repository Actions code as the collector.");
                continue;
            }
            Require(submodules is not null, "Source programs must follow the archive checkout declaration.");
            Only(step, ["name", "id", "run", "if", "env", "timeout-minutes", "shell"]);
            var script = Scalar(Child(step, "run"));
            Require(script.Length is > 0 and <= 40_000 && !script.Contains("${{", StringComparison.Ordinal), "Unsupported dynamic expression inside a source script.");
            Require(Child(step, "shell") is null || Scalar(Child(step, "shell")) is "bash" or "bash --noprofile --norc -e -o pipefail {0}", "Unknown source shell semantics.");
            programs.Add(new(Scalar(Child(step, "name")), Child(step, "id") is { } id ? Scalar(id) : null,
                script, Condition(Child(step, "if")), Environment(Child(step, "env"), jobLevel: false),
                Child(step, "timeout-minutes") is { } bound ? Number(bound, 1, timeout) : timeout));
        }
        Require(submodules is not null && programs.Count is > 0 and <= 50 && artifacts.Count <= 10
            && artifacts.Select(a => a.Name).Distinct().Count() == artifacts.Count, "Incomplete or ambiguous source job inventory.");
        var plan = new JobPlan(1, path, Jobs[path], timeout, submodules!, env, programs.ToArray(), artifacts.ToArray());
        ValidatePlan(plan);
        var triggers = Mapping(Child(root, "on"));
        Only(triggers, ["pull_request", "push"]);
        Require(Child(triggers, "pull_request") is YamlMappingNode { Children.Count: 0 }, "Unknown original pull-request trigger semantics.");
        var push = Mapping(Child(triggers, "push"));
        Only(push, ["branches", "paths-ignore"]);
        Require(Child(push, "branches") is YamlSequenceNode branches && branches.Children.Count == 1
            && Scalar(branches.Children[0]) == defaultBranch, "Source push trigger must name only the actual default branch.");
        triggers.Children[new YamlScalarNode("pull_request")] = new YamlMappingNode { { "branches-ignore", new YamlSequenceNode(new YamlScalarNode(defaultBranch)) } };
        triggers.Add("pull_request_target", new YamlMappingNode { { "branches", new YamlSequenceNode(new YamlScalarNode(defaultBranch)) } });
        root.Children[new YamlScalarNode("permissions")] = new YamlMappingNode { { "contents", "read" }, { "statuses", "write" } };
        jobs.Children[new YamlScalarNode(Jobs[path])] = DispatcherJob(plan, collectorRepository, defaultBranch);
        jobs.Add("factory-source-request", RequestJob(plan, defaultBranch));
        return Save(root);
    }

    public static bool IsMigrated(string path, string yaml, string collectorRepository = DefaultRepository, string defaultBranch = "main")
    {
        try
        {
            if (!Jobs.ContainsKey(path) || !ValidCollector(collectorRepository)) return false;
            var root = Parse(yaml);
            Only(root, ["name", "on", "permissions", "concurrency", "jobs"]);
            var jobs = Mapping(Child(root, "jobs"));
            if (jobs.Children.Count != 2) return false;
            var job = Mapping(Child(jobs, Jobs[path]));
            var steps = (YamlSequenceNode)Child(job, "steps")!;
            var first = Mapping(steps.Children[0]);
            var env = Mapping(Child(first, "env"));
            var bytes = Convert.FromBase64String(Scalar(Child(env, "FACTORY_GUI_SOURCE_PLAN")));
            if (bytes.Length > 100_000) return false;
            var plan = JsonSerializer.Deserialize<JobPlan>(bytes, Json) ?? throw Refused("Invalid source plan.");
            ValidatePlan(plan);
            var triggers = Mapping(Child(root, "on"));
            Only(triggers, ["pull_request", "pull_request_target", "push"]);
            var push = Mapping(Child(triggers, "push"));
            Only(push, ["branches", "paths-ignore"]);
            return plan.Workflow == path
                && Same(Mapping(Child(triggers, "pull_request_target")), new YamlMappingNode { { "branches", new YamlSequenceNode(new YamlScalarNode(defaultBranch)) } })
                && Same(Mapping(Child(triggers, "pull_request")), new YamlMappingNode { { "branches-ignore", new YamlSequenceNode(new YamlScalarNode(defaultBranch)) } })
                && Child(push, "branches") is YamlSequenceNode branches && branches.Children.Count == 1 && Scalar(branches.Children[0]) == defaultBranch
                && Same(Mapping(Child(root, "permissions")), new YamlMappingNode { { "contents", "read" }, { "statuses", "write" } })
                && Same(job, DispatcherJob(plan, collectorRepository, defaultBranch))
                && Same(Mapping(Child(jobs, "factory-source-request")), RequestJob(plan, defaultBranch));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    static YamlMappingNode DispatcherJob(JobPlan plan, string collectorRepository, string defaultBranch)
    {
        var steps = new YamlSequenceNode();
        var environment = new YamlMappingNode {
            { "FACTORY_GUI_SOURCE_PROTOCOL", Protocol },
            { "FACTORY_GUI_SOURCE_PLAN", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(plan, Json)) },
            { "FACTORY_GUI_SOURCE_WORKFLOW", plan.Workflow },
            { "FACTORY_GUI_SOURCE_JOB", plan.JobId },
            { "FACTORY_GUI_COLLECTOR_REPOSITORY", collectorRepository },
            { "FACTORY_GUI_COLLECTOR_WORKFLOW_SHA256", RemoteGuiSourceWorkflow.Sha256 },
            { "FACTORY_GUI_DEFAULT_BRANCH", defaultBranch },
            { "FACTORY_GUI_APP_ID", "${{ vars.FACTORY_GUI_APP_ID }}" },
            { "FACTORY_GUI_APP_PRIVATE_KEY", "${{ secrets.FACTORY_GUI_APP_PRIVATE_KEY }}" },
            { "FACTORY_GUI_CONFIGURATION_SHA256", "${{ vars.FACTORY_GUI_CONFIGURATION_SHA256 }}" },
            { "FACTORY_GUI_SOURCE_TOKEN", "${{ github.token }}" }
        };
        if (plan.Env.Values.Any(v => v == "${var:LW_REQUIRE_REAL}")) environment.Add("FACTORY_GUI_VAR_LW_REQUIRE_REAL", "${{ vars.LW_REQUIRE_REAL }}");
        steps.Add(new YamlMappingNode {
            { "name", "Dispatch source data from the protected default environment, never execute source here" },
            { "id", "factory-source" }, { "env", environment },
            { "run", new YamlScalarNode("python3 -I - <<'FACTORY_GUI_DISPATCH'\n" + RemoteGuiSourceWorkflow.Dispatcher.TrimEnd() + "\nFACTORY_GUI_DISPATCH\n")
                { Style = YamlDotNet.Core.ScalarStyle.Literal } },
            { "shell", "bash --noprofile --norc -e -o pipefail {0}" }
        });
        for (var index = 0; index < plan.Artifacts.Length; index++)
        {
            var artifact = plan.Artifacts[index];
            steps.Add(new YamlMappingNode {
                { "name", "Upload " + artifact.Name + " bounded source evidence" }, { "id", "factory-artifact-" + index }, { "if", "always()" },
                { "continue-on-error", "true" },
                { "uses", "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02" },
                { "with", new YamlMappingNode { { "name", artifact.Name },
                    { "path", "${{ steps.factory-source.outputs.artifact_root }}/" + index },
                    { "if-no-files-found", artifact.IfNoFilesFound }, { "retention-days", artifact.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture) } } }
            });
        }
        if (plan.Artifacts.Length > 0)
        {
            var failed = "${{ " + string.Join(" || ", plan.Artifacts.Select((_, index) => "steps['factory-artifact-" + index + "'].outcome == 'failure'")) + " }}";
            var dispatcher = RemoteGuiSourceWorkflow.Dispatcher;
            var entry = dispatcher.LastIndexOf("if __name__ == '__main__':", StringComparison.Ordinal);
            Require(entry >= 0, "Compiled dispatcher has no final entrypoint.");
            var reporter = dispatcher[..entry] + "if __name__ == '__main__':\n    sys.exit(artifact_delivery_main())\n";
            steps.Add(new YamlMappingNode {
                { "name", "Publish actual source-head outcome after retained evidence delivery" }, { "if", "always()" },
                { "env", new YamlMappingNode {
                    { "FACTORY_GUI_SOURCE_JOB", plan.JobId }, { "FACTORY_GUI_DEFAULT_BRANCH", defaultBranch },
                    { "FACTORY_GUI_SOURCE_TOKEN", "${{ github.token }}" },
                    { "FACTORY_GUI_SOURCE_CONCLUSION", "${{ steps.factory-source.outputs.source_conclusion }}" },
                    { "FACTORY_GUI_SOURCE_TARGET_URL", "${{ steps.factory-source.outputs.source_target_url }}" },
                    { "FACTORY_GUI_ARTIFACT_DELIVERY_FAILED", failed } } },
                { "run", new YamlScalarNode("python3 -I - <<'FACTORY_GUI_REPORT'\n" + reporter.TrimEnd() + "\nFACTORY_GUI_REPORT\n") { Style = YamlDotNet.Core.ScalarStyle.Literal } },
                { "shell", "bash --noprofile --norc -e -o pipefail {0}" }
            });
        }
        return new YamlMappingNode { { "runs-on", "ubuntu-24.04" }, { "environment", RemoteGuiSourceWorkflow.Environment },
            { "if", "github.event_name == 'push' || (github.event_name == 'pull_request_target' && github.event.pull_request.head.repo.full_name == github.repository && github.event.pull_request.base.ref == '" + defaultBranch + "')" },
            { "timeout-minutes", (plan.TimeoutMinutes + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "steps", steps } };
    }

    public const string PublisherPath = ".github/workflows/factory-gui-source-publisher.yml";
    private sealed record PublisherEntry(string WorkflowName, string Path, string JobId, string Plan, string Sha256);

    static YamlScalarNode Bridge(string entry) => new(RemoteGuiSourceWorkflow.Script(entry)) { Style = YamlDotNet.Core.ScalarStyle.Literal };

    static YamlMappingNode RequestJob(JobPlan plan, string defaultBranch) => new() {
        { "runs-on", "ubuntu-24.04" }, { "timeout-minutes", "5" },
        { "if", "github.event_name == 'pull_request' && github.event.pull_request.head.repo.full_name == github.repository && github.event.pull_request.base.ref != '" + defaultBranch + "'" },
        { "permissions", new YamlMappingNode { { "contents", "read" }, { "statuses", "write" } } },
        { "steps", new YamlSequenceNode(new YamlMappingNode {
            { "name", "Register actual-head pending before requesting trusted default publication" },
            { "env", new YamlMappingNode { { "FACTORY_GUI_SOURCE_PROTOCOL", Protocol }, { "FACTORY_GUI_SOURCE_JOB", plan.JobId },
                { "FACTORY_GUI_DEFAULT_BRANCH", defaultBranch }, { "FACTORY_GUI_SOURCE_TOKEN", "${{ github.token }}" } } },
            { "run", Bridge("request_main") }, { "shell", "bash --noprofile --norc -e -o pipefail {0}" }
        }) }
    };

    /// <summary>One separately reviewed DEFAULT-branch workflow handles non-default PR bases. It takes only authenticated
    /// run identity, never request artifacts/programs; its program map is compiled from the exact four reviewed migrations.</summary>
    public static string Publisher(IReadOnlyDictionary<string, string> migrated, string collectorRepository = DefaultRepository, string defaultBranch = "main")
    {
        Require(migrated.Count == Jobs.Count && Jobs.Keys.All(migrated.ContainsKey) && ValidCollector(collectorRepository), "Incomplete trusted publisher migration map.");
        var entries = new List<PublisherEntry>();
        foreach (var item in Jobs)
        {
            Require(IsMigrated(item.Key, migrated[item.Key], collectorRepository, defaultBranch), "Publisher requires exact valid legacy migration wrappers.");
            var root = Parse(migrated[item.Key]);
            var name = Scalar(Child(root, "name"));
            Require(Regex.IsMatch(name, @"\A[A-Za-z0-9 ._-]{1,150}\z"), "Unsupported source workflow display name.");
            var job = Mapping(Child(Mapping(Child(root, "jobs")), item.Value));
            var first = Mapping(((YamlSequenceNode)Child(job, "steps")!).Children[0]);
            var plan = Scalar(Child(Mapping(Child(first, "env")), "FACTORY_GUI_SOURCE_PLAN"));
            entries.Add(new(name, item.Key, item.Value, plan, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(migrated[item.Key])))));
        }
        Require(entries.Select(e => e.WorkflowName).Distinct().Count() == entries.Count, "Source workflow names must be unambiguous.");
        var encodedMap = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(entries, Json));
        Require(encodedMap.Length <= 600_000, "Trusted publisher program map exceeds its protocol bound.");
        var environment = new YamlMappingNode {
            { "FACTORY_GUI_SOURCE_PROTOCOL", Protocol },
            { "FACTORY_GUI_PUBLISHER_PLANS", encodedMap },
            { "FACTORY_GUI_DEFAULT_BRANCH", defaultBranch }, { "FACTORY_GUI_COLLECTOR_REPOSITORY", collectorRepository },
            { "FACTORY_GUI_COLLECTOR_WORKFLOW_SHA256", RemoteGuiSourceWorkflow.Sha256 },
            { "FACTORY_GUI_APP_ID", "${{ vars.FACTORY_GUI_APP_ID }}" },
            { "FACTORY_GUI_APP_PRIVATE_KEY", "${{ secrets.FACTORY_GUI_APP_PRIVATE_KEY }}" },
            { "FACTORY_GUI_CONFIGURATION_SHA256", "${{ vars.FACTORY_GUI_CONFIGURATION_SHA256 }}" },
            { "FACTORY_GUI_VAR_LW_REQUIRE_REAL", "${{ vars.LW_REQUIRE_REAL }}" },
            { "FACTORY_GUI_SOURCE_TOKEN", "${{ github.token }}" }
        };
        var steps = new YamlSequenceNode(new YamlMappingNode {
            { "name", "Validate actual request run, immutable source and compile-pinned plan before dispatch" },
            { "id", "factory-source" }, { "env", environment }, { "run", Bridge("publisher_main") },
            { "shell", "bash --noprofile --norc -e -o pipefail {0}" }
        });
        for (var index = 0; index < 10; index++)
        {
            var output = "steps.factory-source.outputs.";
            steps.Add(new YamlMappingNode {
                { "name", "Retain validated original source artifact " + index }, { "id", "factory-artifact-" + index },
                { "if", "always() && fromJSON(steps.factory-source.outputs.artifact_count || '0') > " + index }, { "continue-on-error", "true" },
                { "uses", "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02" },
                { "with", new YamlMappingNode { { "name", "${{ " + output + "artifact_name_" + index + " }}" },
                    { "path", "${{ " + output + "artifact_root }}/" + index },
                    { "retention-days", "${{ " + output + "artifact_retention_" + index + " }}" },
                    { "if-no-files-found", "${{ " + output + "artifact_missing_" + index + " }}" } } }
            });
        }
        var failed = "${{ " + string.Join(" || ", Enumerable.Range(0, 10).Select(i => "steps['factory-artifact-" + i + "'].outcome == 'failure'")) + " }}";
        var deliveryEnvironment = new YamlMappingNode {
            { "FACTORY_GUI_SOURCE_PROTOCOL", Protocol }, { "FACTORY_GUI_DEFAULT_BRANCH", defaultBranch },
            { "FACTORY_GUI_COLLECTOR_REPOSITORY", collectorRepository },
            { "FACTORY_GUI_PUBLISHER_PLANS", encodedMap },
            { "FACTORY_GUI_SOURCE_TOKEN", "${{ github.token }}" },
            { "FACTORY_GUI_SOURCE_CONCLUSION", "${{ steps.factory-source.outputs.source_conclusion }}" },
            { "FACTORY_GUI_SOURCE_TARGET_URL", "${{ steps.factory-source.outputs.source_target_url }}" },
            { "FACTORY_GUI_ARTIFACT_DELIVERY_FAILED", failed }
        };
        steps.Add(new YamlMappingNode {
            { "name", "Publish actual-head outcome after source evidence delivery" },
            { "if", "always() && fromJSON(steps.factory-source.outputs.artifact_count || '0') > 0" },
            { "env", deliveryEnvironment }, { "run", Bridge("publisher_delivery_main") },
            { "shell", "bash --noprofile --norc -e -o pipefail {0}" }
        });
        var publisher = new YamlMappingNode {
            { "name", "Factory trusted GUI source publisher" },
            { "on", new YamlMappingNode { { "workflow_run", new YamlMappingNode {
                { "workflows", new YamlSequenceNode(entries.Select(e => new YamlScalarNode(e.WorkflowName))) },
                { "types", new YamlSequenceNode(new YamlScalarNode("completed")) } } } } },
            { "permissions", new YamlMappingNode { { "contents", "read" }, { "statuses", "write" } } },
            { "concurrency", new YamlMappingNode { { "group", "factory-gui-publish-${{ github.event.workflow_run.id }}" }, { "cancel-in-progress", "true" } } },
            { "jobs", new YamlMappingNode { { "publish", new YamlMappingNode {
                { "runs-on", "ubuntu-24.04" }, { "environment", RemoteGuiSourceWorkflow.Environment }, { "timeout-minutes", "195" },
                { "if", "github.event.workflow_run.event == 'pull_request'" }, { "steps", steps } } } } }
        };
        return Save(publisher);
    }

    static void ValidatePlan(JobPlan plan)
    {
        Require(plan.Version == 1 && Jobs.TryGetValue(plan.Workflow, out var job) && job == plan.JobId
            && plan.TimeoutMinutes is >= 1 and <= 180 && plan.Submodules is "false" or "recursive"
            && plan.Steps is { Length: > 0 and <= 50 } && plan.Artifacts is { Length: <= 10 }, "Invalid source-job protocol plan.");
        Require(JsonSerializer.SerializeToUtf8Bytes(plan, Json).Length <= 100_000, "Source-job plan exceeds its budget.");
        foreach (var entry in plan.Env) ValidateEnvironment(entry.Key, entry.Value, true);
        foreach (var step in plan.Steps)
        {
            Require(step.Run.Length is > 0 and <= 40_000 && !step.Run.Contains("${{", StringComparison.Ordinal)
                && step.TimeoutMinutes >= 1 && step.TimeoutMinutes <= plan.TimeoutMinutes, "Invalid source-step program or timeout.");
            ValidateCondition(step.Condition);
            foreach (var entry in step.Env) ValidateEnvironment(entry.Key, entry.Value, false);
        }
        foreach (var artifact in plan.Artifacts)
        {
            Require(Regex.IsMatch(artifact.Name, @"\A[a-zA-Z0-9_-]{1,100}\z") && artifact.Condition == "always()"
                && artifact.RetentionDays is >= 1 and <= 30 && artifact.IfNoFilesFound is "ignore" or "warn" or "error"
                && artifact.Paths.Length is > 0 and <= 20, "Invalid source artifact declaration.");
            foreach (var path in artifact.Paths) Require(SourcePath(path) == path, "Invalid source artifact path.");
        }
    }

    static Dictionary<string, string> Environment(YamlNode? node, bool jobLevel)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null) return result;
        foreach (var entry in Mapping(node).Children)
        {
            var name = Scalar(entry.Key);
            var value = Scalar(entry.Value).Replace("${{ github.workspace }}", "${source}", StringComparison.Ordinal)
                .Replace("${{ runner.temp }}", "${temp}", StringComparison.Ordinal)
                .Replace("${{ vars.LW_REQUIRE_REAL }}", "${var:LW_REQUIRE_REAL}", StringComparison.Ordinal);
            ValidateEnvironment(name, value, jobLevel);
            result.Add(name, value);
        }
        return result;
    }
    static void ValidateEnvironment(string name, string value, bool jobLevel)
    {
        Require(Regex.IsMatch(name, @"\A[A-Z][A-Z0-9_]{0,127}\z") && name is not ("PATH" or "HOME" or "TMPDIR" or "USER" or "SHELL")
            && !new[] { "GITHUB_", "ACTIONS_", "RUNNER_", "DYLD_", "LD_", "BASH_", "FACTORY_" }.Any(name.StartsWith), "Source environment may not override principal, authentication or control variables.");
        Require(value.Length <= 8192 && !value.Contains("${{", StringComparison.Ordinal) && !value.Contains('\0')
            && (jobLevel || !value.Contains("${var:", StringComparison.Ordinal)), "Unknown source environment expression.");
    }
    static string SourcePath(string value)
    {
        value = value.Replace("${{ runner.temp }}", "${temp}", StringComparison.Ordinal);
        var relative = value.StartsWith("${temp}/", StringComparison.Ordinal) ? value[8..] : value;
        Require(relative.Length is > 0 and <= 512 && !relative.StartsWith('/') && !relative.Contains('\\')
            && !relative.Split('/').Any(p => p is "" or "." or "..") && !relative.Contains("${", StringComparison.Ordinal)
            && !relative.Any(c => c is '*' or '?' or '[' or ']' or '\0'), "Artifact must name a bounded explicit source/temp path, not a glob, absolute path or traversal.");
        return value;
    }
    static string Condition(YamlNode? node)
    {
        var value = node is null ? "success()" : Scalar(node);
        ValidateCondition(value);
        return value;
    }
    static void ValidateCondition(string value) => Require(value is "success()" or "always()" or "env.LW_INFERENCE_REAL == '1'", "Unsupported source-step condition; never silently drop execution semantics.");
    static int Number(YamlNode? node, int minimum, int maximum)
    {
        Require(int.TryParse(Scalar(node), out var number) && number >= minimum && number <= maximum, "Source timeout/retention is outside the supported bound.");
        return number;
    }
    static YamlMappingNode Parse(string text)
    {
        Require(Encoding.UTF8.GetByteCount(text) <= 1_000_000, "Workflow exceeds its read bound.");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        Require(yaml.Documents.Count == 1, "Expected one workflow document.");
        var root = Mapping(yaml.Documents[0].RootNode);
        Count(root, 0);
        return root;
    }
    static int Count(YamlNode node, int depth)
    {
        Require(depth < 64, "Workflow nesting exceeds its bound.");
        var size = node switch { YamlMappingNode m => 1 + m.Children.Sum(e => Count(e.Key, depth + 1) + Count(e.Value, depth + 1)),
            YamlSequenceNode s => 1 + s.Children.Sum(n => Count(n, depth + 1)), YamlScalarNode => 1, _ => throw Refused("Unsupported workflow node.") };
        Require(size <= 10_000, "Workflow node inventory exceeds its bound.");
        return size;
    }
    static YamlNode? Child(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    static YamlMappingNode Mapping(YamlNode? node) => node as YamlMappingNode ?? throw Refused("Expected a workflow mapping.");
    static string Scalar(YamlNode? node) => node is YamlScalarNode { Value: { } value } ? value : throw Refused("Expected a workflow scalar.");
    static void Only(YamlMappingNode node, string[] allowed) => Require(node.Children.Keys.All(k => allowed.Contains(Scalar(k), StringComparer.Ordinal)), "Unknown workflow field; execution semantics cannot be discarded.");
    static bool Same(YamlNode a, YamlNode b) => a switch {
        YamlScalarNode scalar => b is YamlScalarNode other && scalar.Value == other.Value,
        YamlSequenceNode list => b is YamlSequenceNode other && list.Children.Count == other.Children.Count && list.Children.Zip(other.Children).All(p => Same(p.First, p.Second)),
        YamlMappingNode map => b is YamlMappingNode other && map.Children.Count == other.Children.Count
            && map.Children.All(p => other.Children.TryGetValue(p.Key, out var value) && Same(p.Value, value)), _ => false };
    static string Save(YamlMappingNode root)
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false);
        return output.ToString();
    }
    static bool ValidCollector(string repository) => Regex.IsMatch(repository, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z")
        && !repository.Equals("michaeltrefry/LensWorks", StringComparison.OrdinalIgnoreCase);
    static InvalidOperationException Refused(string reason) => new(reason);
    static void Require(bool condition, string reason) { if (!condition) throw Refused(reason); }
}
