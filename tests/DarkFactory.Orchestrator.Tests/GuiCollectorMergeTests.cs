using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorMergeTests
{
    const string Base = "1111111111111111111111111111111111111111";
    const string Head = "2222222222222222222222222222222222222222";
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
    static readonly string[] Jobs = ["inference", "live-raw", "cargo-test", "deterministic-fixtures-and-tour"];

    [Theory]
    [InlineData("none")]
    [InlineData("passed")]
    public async Task Explicit_owner_merge_accepts_only_exact_compiled_source_payload_and_pins_the_actual_PR_head(string ci)
    {
        var fixture = new Fixture(ci);
        var result = await fixture.Client.MergeGeneratedPullAsync(fixture.Original, fixture.Desired, 4, true, CancellationToken.None);
        Assert.True(result.Merged);
        Assert.Equal(Head, result.Head);
        var merge = Assert.Single(fixture.Api.Calls, c => c.Method == "PUT" && c.Path.EndsWith("/merge"));
        Assert.Equal(Head, JsonNode.Parse(merge.Body!)!["sha"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Path.Contains("/reviews"));
        if (ci == "none") Assert.Contains("not a CI pass", result.Reason);
        else Assert.Contains("checks passed", result.Reason);
    }

    [Fact]
    public async Task Pending_CI_is_bounded_without_any_merge_or_manual_approval_flag()
    {
        var fixture = new Fixture("pending");
        var result = await fixture.Client.MergeGeneratedPullAsync(fixture.Original, fixture.Desired, 4, true, CancellationToken.None, wait: TimeSpan.Zero);
        Assert.False(result.Merged);
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "PUT");
        Assert.Contains("head CI", result.Reason);
    }

    [Theory]
    [InlineData("failed", false, false)]
    [InlineData("passed", true, false)]
    [InlineData("passed", false, true)]
    public async Task Failed_checks_extra_files_and_changed_payload_bytes_never_get_an_owner_merge(string ci, bool unrelated, bool changed)
    {
        var fixture = new Fixture(ci) { ExtraFile = unrelated, ChangedContent = changed };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.MergeGeneratedPullAsync(fixture.Original, fixture.Desired, 4, true, CancellationToken.None));
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "PUT");
    }

    [Fact]
    public async Task A_changed_requested_program_is_not_treated_as_a_reviewed_compiler_template()
    {
        var fixture = new Fixture("passed");
        var changed = fixture.Desired.Select((w, index) => index == 0 ? w with { Content = w.Content + "\n# replacement", Sha256 = GuiCollectorDeployment.Hash(w.Content + "\n# replacement") } : w).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.MergeGeneratedPullAsync(fixture.Original, changed, 4, true, CancellationToken.None));
        Assert.DoesNotContain(fixture.Api.Calls, c => c.Method == "PUT");
    }

    [Fact]
    public async Task A_moved_head_refuses_the_SHA_conditional_owner_merge_without_a_blind_repeat()
    {
        var fixture = new Fixture("passed") { RejectMerge = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.MergeGeneratedPullAsync(fixture.Original, fixture.Desired, 4, true, CancellationToken.None));
        Assert.Single(fixture.Api.Calls, c => c.Method == "PUT");
        Assert.False(fixture.Merged);
    }

    [Fact]
    public async Task Compiled_bootstrap_is_private_and_never_reads_a_malicious_caller_checkout()
    {
        if (OperatingSystem.IsWindows()) return;
        var parent = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "df-gui-compiled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var checkout = Path.Combine(parent, "source-checkout", "scripts");
            Directory.CreateDirectory(checkout);
            var malicious = Path.Combine(checkout, "setup-gui-collector.py");
            await File.WriteAllTextAsync(malicious, "raise RuntimeError('source-controlled bootstrap must never execute')");
            var closeCalls = new List<string>();
            using var stage = await GuiCollectorDeployment.StageBootstrapAsync(CancellationToken.None, parent,
                (path, _) => { closeCalls.Add(path); return Task.CompletedTask; });
            Assert.Single(closeCalls);
            Assert.Equal(GuiCollectorDeployment.BootstrapBytes(), await File.ReadAllBytesAsync(stage.Script));
            Assert.NotEqual(await File.ReadAllTextAsync(malicious), await File.ReadAllTextAsync(stage.Script));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stage.Script));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(stage.Directory));
            var start = GuiCollectorDeployment.InstallerStartInfo(stage.Script, "/private/plan.json", "/private/authority.json");
            Assert.Contains(stage.Script, start.ArgumentList);
            Assert.DoesNotContain(malicious, start.ArgumentList);
            Assert.Contains("-I", start.ArgumentList);
            Assert.Equal("/", start.WorkingDirectory);
        }
        finally { Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task Bootstrap_staging_refuses_symlinked_owner_parents_without_executing_any_code()
    {
        if (OperatingSystem.IsWindows()) return;
        var parent = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "df-gui-root-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var target = Path.Combine(parent, "target");
            Directory.CreateDirectory(target);
            var link = Path.Combine(parent, "source-writable-alias");
            Directory.CreateSymbolicLink(link, target);
            await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorDeployment.StageBootstrapAsync(CancellationToken.None, link, (_, _) => Task.CompletedTask));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { Directory.Delete(parent, true); }
    }

    sealed class Fixture
    {
        public readonly GuiCollectorDeployment.Evidence Original;
        public readonly GuiCollectorDeployment.Workflow[] Desired;
        public readonly Api Api;
        public readonly GuiCollectorDeployment Client;
        public bool ExtraFile, ChangedContent, RejectMerge, Merged;
        readonly Dictionary<string, string> source;
        readonly Dictionary<string, string> desired;
        readonly string ci;

        public Fixture(string ci)
        {
            this.ci = ci;
            source = GuiCollectorDeployment.WorkflowPaths.Select((path, index) => new { path, content = OriginalText(Jobs[index]) }).ToDictionary(x => x.path, x => x.content);
            var workflows = source.Select(w => new GuiCollectorDeployment.Workflow(w.Key, GuiCollectorDeployment.Hash(w.Value), w.Value)).ToArray();
            Original = new(new string('a', 64), GuiCollectorDeployment.ControlRepository.FullName, Base, workflows, []) { DefaultBranch = "main" };
            Desired = workflows.Select(w => { var content = GuiSourceWorkflow.Migrate(w.Path, w.Content); return new GuiCollectorDeployment.Workflow(w.Path, GuiCollectorDeployment.Hash(content), content); }).ToArray();
            desired = Desired.ToDictionary(w => w.Path, w => w.Content);
            desired[GuiSourceWorkflow.PublisherPath] = GuiSourceWorkflow.Publisher(desired);
            desired[RemoteIosWorkflow.Path] = RemoteIosWorkflow.Text;
            Api = new Api(Answer);
            var http = new HttpClient(Api) { BaseAddress = new Uri("https://api.github.com/") };
            Client = new(http, new GitHubApp(http, "12", Pem, TimeProvider.System), "OWNER_ONLY", TimeProvider.System);
        }
        HttpResponseMessage Answer(string method, string path, string? body)
        {
            const string prefix = "/repos/michaeltrefry/LensWorks";
            if (path == "/user") return Json(new { login = "michaeltrefry", type = "User" });
            if (path == prefix) return Json(new { default_branch = "main", permissions = new { admin = true } });
            if (path == prefix + "/commits/main") return Json(new { sha = Base });
            if (path == prefix + "/pulls/4") return Json(new { state = Merged ? "closed" : "open", merged = Merged, changed_files = desired.Count + (ExtraFile ? 1 : 0),
                head = new { sha = Head, @ref = "factory/gui-collector-" + Original.PlanSha256[..16], repo = new { full_name = GuiCollectorDeployment.SourceRepository.FullName } },
                @base = new { @ref = "main", repo = new { full_name = GuiCollectorDeployment.SourceRepository.FullName } },
                user = new { login = "michaeltrefry", id = 1 }, merged_by = new { login = "michaeltrefry", type = "User" } });
            if (path.Contains("/pulls/4/files?")) return Json(desired.Keys.Select(filename => new { filename, status = source.ContainsKey(filename) ? "modified" : "added" })
                .Concat(ExtraFile ? [new { filename = "production.cs", status = "modified" }] : []));
            if (path.StartsWith(prefix + "/contents/", StringComparison.Ordinal))
            {
                var file = path[(prefix.Length + "/contents/".Length)..].Split('?')[0];
                var atHead = path.EndsWith("ref=" + Head, StringComparison.Ordinal);
                if (!atHead && !source.ContainsKey(file)) return new(HttpStatusCode.NotFound);
                var text = atHead ? desired[file] + (ChangedContent ? "\n# changed" : "") : source[file];
                return Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });
            }
            if (path.Contains("/check-runs?")) return ci == "none" ? Json(new { total_count = 0, check_runs = Array.Empty<object>() })
                : Json(new { total_count = 1, check_runs = new[] { new { head_sha = Head, status = ci == "pending" ? "in_progress" : "completed", conclusion = ci == "failed" ? "failure" : "success" } } });
            if (path.Contains("/status?")) return Json(new { total_count = 0, statuses = Array.Empty<object>() });
            if (path.Contains("/check-suites?")) return Json(new { total_count = 0, check_suites = Array.Empty<object>() });
            if (path.EndsWith("/pulls/4/merge", StringComparison.Ordinal))
            {
                Assert.Equal("PUT", method);
                if (RejectMerge) return new(HttpStatusCode.Conflict) { Content = new StringContent("head moved") };
                Merged = true;
                return Json(new { merged = true });
            }
            throw new InvalidOperationException("Unexpected fake route " + method + " " + path);
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
    static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    sealed class Api(Func<string, string, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public sealed record Call(string Method, string Path, string? Body);
        public readonly List<Call> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return answer(request.Method.Method, request.RequestUri!.PathAndQuery, body);
        }
    }
}
