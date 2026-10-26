using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiSourceWorkflowTests
{
    [Theory]
    [InlineData("inference-adapter.yml", "inference", 120, "recursive")]
    [InlineData("live-raw-acceptance.yml", "live-raw", 90, "recursive")]
    [InlineData("rust-catalog-tests.yml", "cargo-test", 90, "false")]
    [InlineData("s47-procedural-ui-tour.yml", "deterministic-fixtures-and-tour", 75, "recursive")]
    public void Each_existing_job_migrates_to_only_the_fixed_installed_client_and_preserves_source_programs_conditions_env_timeouts_and_artifacts(
        string file, string job, int timeout, string submodules)
    {
        var path = ".github/workflows/" + file;
        var original = Original(job, timeout, submodules);
        var migrated = GuiSourceWorkflow.Migrate(path, original);
        Assert.True(GuiSourceWorkflow.IsMigrated(path, migrated));
        Assert.Equal(migrated, GuiSourceWorkflow.Migrate(path, migrated));
        Assert.DoesNotContain("actions/checkout", migrated);
        Assert.DoesNotContain("self-hosted", migrated);
        Assert.DoesNotContain("runs-on: [macOS", migrated);
        var root = Read(migrated);
        var actualJob = Map(Map(root, "jobs"), job);
        Assert.Equal("ubuntu-24.04", Scalar(actualJob, "runs-on"));
        Assert.Equal(RemoteGuiSourceWorkflow.Environment, Scalar(actualJob, "environment"));
        Assert.Equal(2, Map(root, "jobs").Children.Count);
        var steps = ((YamlSequenceNode)actualJob.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToArray();
        Assert.Equal(3, steps.Length);
        Assert.Contains(RemoteGuiSourceWorkflow.Dispatcher.TrimEnd(), Scalar(steps[0], "run"));
        Assert.DoesNotContain(GuiSourceWorkflow.Client, Scalar(steps[0], "run"));
        Assert.False(steps[1].Children.ContainsKey(new YamlScalarNode("run")));
        Assert.Equal("actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02", Scalar(steps[1], "uses"));
        Assert.Equal("true", Scalar(steps[1], "continue-on-error"));
        Assert.Contains("artifact_delivery_main()", Scalar(steps[2], "run"));
        Assert.Equal("always()", Scalar(steps[2], "if"));
        Assert.Contains("factory-artifact-0", Scalar(Map(steps[2], "env"), "FACTORY_GUI_ARTIFACT_DELIVERY_FAILED"));
        Assert.Equal("always()", Scalar(steps[1], "if"));
        Assert.Equal("${{ steps.factory-source.outputs.artifact_root }}/0", Scalar(Map(steps[1], "with"), "path"));
        Assert.Equal(GuiSourceWorkflow.DefaultRepository, Scalar(Map(steps[0], "env"), "FACTORY_GUI_COLLECTOR_REPOSITORY"));
        Assert.Equal("${{ github.token }}", Scalar(Map(steps[0], "env"), "FACTORY_GUI_SOURCE_TOKEN"));
        Assert.Equal("${{ secrets.FACTORY_GUI_APP_PRIVATE_KEY }}", Scalar(Map(steps[0], "env"), "FACTORY_GUI_APP_PRIVATE_KEY"));
        Assert.Equal("${{ vars.LW_REQUIRE_REAL }}", Scalar(Map(steps[0], "env"), "FACTORY_GUI_VAR_LW_REQUIRE_REAL"));
        var plan = Decode(steps[0]);
        Assert.Equal(path, plan.Workflow);
        Assert.Equal(job, plan.JobId);
        Assert.Equal(timeout, plan.TimeoutMinutes);
        Assert.Equal(submodules, plan.Submodules);
        Assert.Equal("4", plan.Env["CARGO_BUILD_JOBS"]);
        Assert.Equal("${var:LW_REQUIRE_REAL}", plan.Env["LW_REQUIRE_REAL"]);
        Assert.Equal("${source}/.build/photocatalog", plan.Steps[0].Env["TEST_RUNNER_LW_CATALOG_TEST_WORKER"]);
        Assert.Equal("${temp}/s47-fixtures", plan.Steps[0].Env["TEST_RUNNER_LW_S47_FIXTURE_ROOT"]);
        Assert.Equal("echo \"LW_INFERENCE_REAL=1\" >> \"$GITHUB_ENV\"\necho /opt/python/bin >> \"$GITHUB_PATH\"\n", plan.Steps[0].Run);
        Assert.Equal("env.LW_INFERENCE_REAL == '1'", plan.Steps[1].Condition);
        Assert.Equal(30, plan.Steps[1].TimeoutMinutes);
        Assert.Equal("always()", plan.Steps[2].Condition);
        Assert.Equal(["Evidence.xcresult", "${temp}/s47-captures"], plan.Artifacts.Single().Paths);
        Assert.Equal("true", Scalar(Map(root, "concurrency"), "cancel-in-progress"));
        Assert.True(Map(root, "on").Children.ContainsKey(new YamlScalarNode("pull_request")));
    }

    [Theory]
    [InlineData("actions/checkout@v4", "evil/custom-action@main")]
    [InlineData("with: {submodules: recursive}", "with: {submodules: recursive, token: '${{ secrets.PAT }}'}")]
    [InlineData("CARGO_BUILD_JOBS: 4", "GITHUB_TOKEN: secret")]
    [InlineData("CARGO_BUILD_JOBS: 4", "HOME: /private/collector")]
    [InlineData("${{ vars.LW_REQUIRE_REAL }}", "${{ secrets.PAT }}")]
    [InlineData("env.LW_INFERENCE_REAL == '1'", "steps.previous.outputs.ok == 'yes'")]
    [InlineData("Evidence.xcresult", "../.credentials")]
    [InlineData("Evidence.xcresult", "/Users/collector/.credentials")]
    [InlineData("Evidence.xcresult", "**/*")]
    [InlineData("timeout-minutes: 120", "container: unknown\n    timeout-minutes: 120")]
    [InlineData("run: cargo test --locked", "run: echo '${{ secrets.PAT }}'")]
    public void Unknown_execution_or_artifact_or_credential_semantics_refuse_the_whole_migration(string before, string after)
    {
        var original = Original("inference", 120, "recursive").Replace(before, after, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => GuiSourceWorkflow.Migrate(".github/workflows/inference-adapter.yml", original));
        Assert.False(GuiSourceWorkflow.IsMigrated(".github/workflows/inference-adapter.yml", original));
    }

    [Fact]
    public void A_marker_or_base64_plan_cannot_hide_direct_collector_execution_extra_steps_or_a_changed_label()
    {
        const string path = ".github/workflows/inference-adapter.yml";
        var migrated = GuiSourceWorkflow.Migrate(path, Original("inference", 120, "recursive"));
        Assert.False(GuiSourceWorkflow.IsMigrated(path, migrated.Replace("python3 -I -", "/bin/bash ./unreviewed-source.sh", StringComparison.Ordinal)));
        Assert.False(GuiSourceWorkflow.IsMigrated(path, migrated.Replace("ubuntu-24.04", "self-hosted", StringComparison.Ordinal)));
        var root = Read(migrated);
        var job = Map(Map(root, "jobs"), "inference");
        ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Add(new YamlMappingNode { { "run", "./untrusted.sh" } });
        using var output = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(output, false);
        Assert.False(GuiSourceWorkflow.IsMigrated(path, output.ToString()));
    }

    [Fact]
    public void Unknown_workflow_job_inventory_and_cross_repository_runner_reuse_are_not_implicitly_authorized()
    {
        var yaml = Original("inference", 120, "recursive");
        Assert.Throws<InvalidOperationException>(() => GuiSourceWorkflow.Migrate(".github/workflows/other.yml", yaml));
        Assert.Throws<InvalidOperationException>(() => GuiSourceWorkflow.Migrate(".github/workflows/inference-adapter.yml", yaml.Replace("  inference:", "  another-job:")));
        Assert.Throws<InvalidOperationException>(() => GuiSourceWorkflow.Migrate(".github/workflows/inference-adapter.yml", yaml, "michaeltrefry/LensWorks"));
        var configured = GuiSourceWorkflow.Migrate(".github/workflows/inference-adapter.yml", yaml, "other-owner/private-controller");
        Assert.True(GuiSourceWorkflow.IsMigrated(".github/workflows/inference-adapter.yml", configured, "other-owner/private-controller"));
        Assert.False(GuiSourceWorkflow.IsMigrated(".github/workflows/inference-adapter.yml", configured));
        Assert.Contains("other-owner/private-controller", configured);
    }

    [Fact]
    public void Changeset_request_runs_no_source_and_has_no_protected_environment_or_app_key()
    {
        const string path = ".github/workflows/inference-adapter.yml";
        var migrated = GuiSourceWorkflow.Migrate(path, Original("inference", 120, "recursive"));
        var root = Read(migrated);
        var request = Map(Map(root, "jobs"), "factory-source-request");
        Assert.Equal("ubuntu-24.04", Scalar(request, "runs-on"));
        Assert.False(request.Children.ContainsKey(new YamlScalarNode("environment")));
        Assert.Contains("base.ref != 'main'", Scalar(request, "if"));
        var steps = (YamlSequenceNode)request.Children[new YamlScalarNode("steps")];
        var step = (YamlMappingNode)Assert.Single(steps.Children);
        var env = Map(step, "env");
        Assert.Equal(new[] { "FACTORY_GUI_DEFAULT_BRANCH", "FACTORY_GUI_SOURCE_JOB", "FACTORY_GUI_SOURCE_PROTOCOL", "FACTORY_GUI_SOURCE_TOKEN" },
            env.Children.Keys.Cast<YamlScalarNode>().Select(n => n.Value).Order());
        Assert.DoesNotContain("${{ secrets.", Save(request));
        Assert.Contains("request_main()", Scalar(step, "run"));
        Assert.Contains("_source_status(github, environ, head, job, 'pending')", Scalar(step, "run"));
        Assert.DoesNotContain("cargo test --locked", Scalar(step, "run"));
        Assert.False(env.Children.ContainsKey(new YamlScalarNode("FACTORY_GUI_SOURCE_PLAN")));
        Assert.Equal("main", ((YamlScalarNode)((YamlSequenceNode)Map(Map(root, "on"), "pull_request").Children[new YamlScalarNode("branches-ignore")]).Children.Single()).Value);
        Assert.Equal("main", ((YamlScalarNode)((YamlSequenceNode)Map(Map(root, "on"), "pull_request_target").Children[new YamlScalarNode("branches")]).Children.Single()).Value);
    }

    [Theory]
    [InlineData("factory-source-request", "environment")]
    [InlineData("factory-source-request", "run")]
    [InlineData("inference", "environment")]
    public void Tampering_either_standalone_request_or_credentialed_source_wrapper_is_rejected(string jobId, string field)
    {
        const string path = ".github/workflows/inference-adapter.yml";
        var migrated = GuiSourceWorkflow.Migrate(path, Original("inference", 120, "recursive"));
        Assert.True(GuiSourceWorkflow.IsMigrated(path, migrated));
        var root = Read(migrated);
        var job = Map(Map(root, "jobs"), jobId);
        if (field == "environment") job.Children[new YamlScalarNode("environment")] = new YamlScalarNode("unprotected");
        else ((YamlMappingNode)((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children[0]).Children[new YamlScalarNode("run")] = new YamlScalarNode("./source-script.sh");
        Assert.False(GuiSourceWorkflow.IsMigrated(path, Save(root)));
    }

    [Fact]
    public void Trusted_default_publisher_contains_only_four_compile_pinned_plan_entries_and_workflow_run_trigger()
    {
        var migrated = Migrated();
        var text = GuiSourceWorkflow.Publisher(migrated);
        var root = Read(text);
        Assert.Equal(new[] { "workflow_run" }, Map(root, "on").Children.Keys.Cast<YamlScalarNode>().Select(n => n.Value));
        var job = Map(Map(root, "jobs"), "publish");
        Assert.Equal(RemoteGuiSourceWorkflow.Environment, Scalar(job, "environment"));
        var first = (YamlMappingNode)((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children[0];
        var env = Map(first, "env");
        var map = JsonDocument.Parse(Convert.FromBase64String(Scalar(env, "FACTORY_GUI_PUBLISHER_PLANS")));
        using (map)
        {
            Assert.Equal(4, map.RootElement.GetArrayLength());
            foreach (var entry in map.RootElement.EnumerateArray())
            {
                var path = entry.GetProperty("path").GetString()!;
                Assert.Equal(DarkFactory.Orchestrator.Gateway.GuiCollectorDeployment.Hash(migrated[path]), entry.GetProperty("sha256").GetString());
                var plan = JsonSerializer.Deserialize<GuiSourceWorkflow.JobPlan>(Convert.FromBase64String(entry.GetProperty("plan").GetString()!), GuiSourceWorkflow.Json)!;
                Assert.Equal(path, plan.Workflow);
                Assert.Equal(entry.GetProperty("jobId").GetString(), plan.JobId);
            }
        }
        Assert.Contains("publisher_main()", Scalar(first, "run"));
        Assert.Contains("_workflow_hash(github, token, row['path'], head) == row['sha256']", Scalar(first, "run"));
        Assert.Contains("request PR modified the migrated workflow", Scalar(first, "run"));
        Assert.DoesNotContain("actions/checkout", text);
        var tampered = new Dictionary<string, string>(migrated);
        var path0 = tampered.Keys.First();
        tampered[path0] = tampered[path0].Replace("ubuntu-24.04", "self-hosted", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => GuiSourceWorkflow.Publisher(tampered));
        tampered.Remove(path0);
        Assert.Throws<InvalidOperationException>(() => GuiSourceWorkflow.Publisher(tampered));
    }

    static Dictionary<string, string> Migrated() => DarkFactory.Orchestrator.Gateway.GuiCollectorDeployment.WorkflowPaths.ToDictionary(path => path, path => {
        var job = path.Split('/').Last() switch { "inference-adapter.yml" => "inference", "live-raw-acceptance.yml" => "live-raw",
            "rust-catalog-tests.yml" => "cargo-test", _ => "deterministic-fixtures-and-tour" };
        return GuiSourceWorkflow.Migrate(path, Original(job, 120, "recursive").Replace("name: Existing source work", "name: " + job, StringComparison.Ordinal));
    });
    static string Save(YamlMappingNode root) { using var output = new StringWriter(); new YamlStream(new YamlDocument(root)).Save(output, false); return output.ToString(); }

    internal static string Original(string job, int timeout, string submodules) => $$$"""
        name: Existing source work
        on:
          pull_request: {}
          push: {branches: [main]}
        permissions: {contents: read}
        concurrency:
          group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.sha }}
          cancel-in-progress: true
        jobs:
          {{{job}}}:
            runs-on: [self-hosted, macOS, ARM64]
            timeout-minutes: {{{timeout}}}
            env:
              CARGO_BUILD_JOBS: 4
              MACOSX_DEPLOYMENT_TARGET: "26.2"
              LW_REQUIRE_REAL: ${{ vars.LW_REQUIRE_REAL }}
            steps:
              - uses: actions/checkout@v4
                with: {submodules: {{{submodules}}}}
              - name: Pin native toolchain and persisted source environment
                id: native-toolchain
                env:
                  TEST_RUNNER_LW_CATALOG_TEST_WORKER: ${{ github.workspace }}/.build/photocatalog
                  TEST_RUNNER_LW_S47_FIXTURE_ROOT: ${{ runner.temp }}/s47-fixtures
                run: |
                  echo "LW_INFERENCE_REAL=1" >> "$GITHUB_ENV"
                  echo /opt/python/bin >> "$GITHUB_PATH"
              - name: Real-weight adapter tests
                if: env.LW_INFERENCE_REAL == '1'
                timeout-minutes: 30
                run: cargo test --locked
              - name: Refuse an empty bundle even after a failed source step
                if: always()
                run: test -d Evidence.xcresult
              - name: Retain evidence
                if: always()
                uses: actions/upload-artifact@v4
                with:
                  name: evidence
                  path: |
                    Evidence.xcresult
                    ${{ runner.temp }}/s47-captures
                  if-no-files-found: ignore
                  retention-days: 7
        """;

    static YamlMappingNode Read(string yaml) { var stream = new YamlStream(); stream.Load(new StringReader(yaml)); return (YamlMappingNode)stream.Documents[0].RootNode; }
    static YamlMappingNode Map(YamlMappingNode map, string name) => (YamlMappingNode)map.Children[new YamlScalarNode(name)];
    static string Scalar(YamlMappingNode map, string name) => ((YamlScalarNode)map.Children[new YamlScalarNode(name)]).Value!;
    static GuiSourceWorkflow.JobPlan Decode(YamlMappingNode step) => JsonSerializer.Deserialize<GuiSourceWorkflow.JobPlan>(Convert.FromBase64String(
        Scalar(Map(step, "env"), "FACTORY_GUI_SOURCE_PLAN")), GuiSourceWorkflow.Json)!;
}
