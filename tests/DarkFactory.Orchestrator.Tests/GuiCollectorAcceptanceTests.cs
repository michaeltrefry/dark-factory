using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorAcceptanceTests
{
    const string Sha = "1111111111111111111111111111111111111111";
    const string Config = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Root = "/repos/" + RemoteGuiSourceWorkflow.Repository;
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
    static GuiWorkflowSettings Settings => new(RepoRef.Parse(RemoteGuiSourceWorkflow.Repository), [new("michaeltrefry", "LensWorks")], Config);
    static JsonObject Receipt() => JsonNode.Parse($$$"""
        {"version":1,"literalCrash":false,"validUncleanJournalReconciled":true,"failedCleanupBlockedReuse":true,
         "sourceStartedWhileBlocked":false,"restoredOnlyOwnFixture":true,"normalReconciliationRestoredReuse":true,
         "freshWorkspace":true,"configurationSha256":"{{{Config}}}","lease":{"workspaceSha256":"{{{Config}}}",
         "startedUtcSeconds":1700000000,"completedUtcSeconds":1700000001,"cleaned":true,"reconciled":true}}
        """)!.AsObject();

    [Fact]
    public async Task Recovery_uses_only_gate_app_scoped_permissions_and_sealed_default_then_nonce_artifact()
    {
        var api = new Api();
        var result = await Run(api);
        Assert.Equal(42, result.RunId);
        Assert.Equal(Sha, result.WorkflowCommit);
        Assert.False(result.Receipt.LiteralCrash);
        Assert.True(result.Receipt.FailedCleanupBlockedReuse);
        Assert.False(result.Receipt.SourceStartedWhileBlocked);
        Assert.Equal(Config, result.Receipt.ConfigurationSha256);
        Assert.Equal(new[] { "read", "write" }, api.Mints);
        Assert.Single(api.Calls.Where(c => c.Method == "POST" && c.Path.EndsWith("/dispatches")));
        Assert.Contains(api.Calls, c => c.Path.EndsWith("?ref=" + Sha));
        Assert.All(api.Calls.Where(c => !c.Path.EndsWith("/installation") && !c.Path.Contains("/access_tokens")),
            c => Assert.Equal("Bearer GATE_" + (c.Method == "POST" ? "write" : "read"), c.Auth));
        using var payload = JsonDocument.Parse(api.Dispatch!);
        Assert.Equal("trunk", payload.RootElement.GetProperty("ref").GetString());
        Assert.Equal("recovery", payload.RootElement.GetProperty("inputs").GetProperty("acceptance_probe").GetString());
        Assert.Equal(result.Nonce, payload.RootElement.GetProperty("inputs").GetProperty("nonce").GetString());
        Assert.DoesNotContain("program", api.Dispatch!);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("fork")]
    [InlineData("wrong-repo")]
    [InlineData("unsealed")]
    [InlineData("mutable-commit")]
    public async Task Unsealed_or_wrong_control_repository_is_refused_before_dispatch(string fault)
    {
        var api = new Api { Fault = fault };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(api));
        Assert.Null(api.Dispatch);
        Assert.DoesNotContain("write", api.Mints);
    }

    [Fact]
    public async Task Gate_app_authorization_refusal_never_uses_owner_fallback_or_dispatches()
    {
        var api = new Api { Fault = "authorization" };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(api));
        Assert.Null(api.Dispatch);
        Assert.DoesNotContain(api.Calls, c => c.Auth?.Contains("OWNER") == true);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("path")]
    [InlineData("head")]
    [InlineData("branch")]
    [InlineData("attempt")]
    [InlineData("duplicate")]
    public async Task Correlated_title_cannot_vouch_for_wrong_run_provenance(string fault)
    {
        var api = new Api { Fault = fault };
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(api));
        Assert.DoesNotContain(api.Calls, c => c.Path.Contains("/artifacts"));
        Assert.DoesNotContain(api.Calls, c => c.Path.EndsWith("/cancel"));
    }

    [Theory]
    [InlineData("missing-artifact")]
    [InlineData("artifact-name")]
    [InlineData("artifact-run")]
    [InlineData("expired")]
    [InlineData("symlink")]
    [InlineData("extra-file")]
    [InlineData("oversized")]
    public async Task Only_one_bounded_regular_receipt_of_the_exact_nonce_and_run_counts(string fault)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(new Api { Fault = fault }));
    }

    [Theory]
    [InlineData("literalCrash", true)]
    [InlineData("validUncleanJournalReconciled", false)]
    [InlineData("failedCleanupBlockedReuse", false)]
    [InlineData("sourceStartedWhileBlocked", true)]
    [InlineData("restoredOnlyOwnFixture", false)]
    [InlineData("normalReconciliationRestoredReuse", false)]
    [InlineData("freshWorkspace", false)]
    public async Task Every_actual_cleanup_blocking_receipt_claim_is_required(string field, bool value)
    {
        var receipt = Receipt();
        receipt[field] = value;
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(new Api { Body = receipt.ToJsonString() }));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("unclean")]
    [InlineData("unreconciled")]
    [InlineData("time")]
    [InlineData("workspace")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task Receipt_configuration_lease_and_strict_shape_are_not_self_authorizing(string fault)
    {
        var receipt = Receipt();
        switch (fault)
        {
            case "configuration": receipt["configurationSha256"] = new string('b', 64); break;
            case "unclean": receipt["lease"]!["cleaned"] = false; break;
            case "unreconciled": receipt["lease"]!["reconciled"] = false; break;
            case "time": receipt["lease"]!["completedUtcSeconds"] = 1; break;
            case "workspace": receipt["lease"]!["workspaceSha256"] = "wrong"; break;
            case "unknown": receipt["handWrittenResult"] = true; break;
        }
        var body = receipt.ToJsonString();
        if (fault == "duplicate") body = body.Replace("\"version\":1", "\"version\":1,\"version\":1");
        await Assert.ThrowsAnyAsync<Exception>(() => Run(new Api { Body = body }));
    }

    [Fact]
    public async Task Timeout_cancels_only_the_identified_exact_run_and_never_guesses_an_unrelated_run()
    {
        var api = new Api { Fault = "pending" };
        using var http = new HttpClient(api) { BaseAddress = OutboundHttp.GitHubApiBase };
        var client = new GuiCollectorAcceptance(http, new GitHubApp(http, "12", Pem, TimeProvider.System)) {
            Deadline = TimeSpan.FromSeconds(2), PollInterval = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunAsync(Settings, CancellationToken.None));
        Assert.Contains(api.Calls, c => c.Path == Root + "/actions/runs/42/cancel");
        var unrelated = new Api { Fault = "unrelated" };
        using var otherHttp = new HttpClient(unrelated) { BaseAddress = OutboundHttp.GitHubApiBase };
        var other = new GuiCollectorAcceptance(otherHttp, new GitHubApp(otherHttp, "12", Pem, TimeProvider.System)) {
            Deadline = TimeSpan.FromSeconds(2), PollInterval = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.RunAsync(Settings, CancellationToken.None));
        Assert.DoesNotContain(unrelated.Calls, c => c.Path.EndsWith("/cancel"));
    }

    static async Task<GuiCollectorAcceptance.Result> Run(Api api)
    {
        using var http = new HttpClient(api) { BaseAddress = OutboundHttp.GitHubApiBase };
        return await new GuiCollectorAcceptance(http, new GitHubApp(http, "12", Pem, TimeProvider.System)).RunAsync(Settings, CancellationToken.None);
    }

    sealed class Api : HttpMessageHandler
    {
        public string? Fault { get; init; }
        public string? Body { get; init; }
        public string? Dispatch { get; private set; }
        string nonce = "";
        public List<string> Mints { get; } = [];
        public List<(string Method, string Path, string? Auth)> Calls { get; } = [];
        static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent(value) };
        static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add((request.Method.Method, path, request.Headers.Authorization?.ToString()));
            if (path.EndsWith("/installation")) return Json(new { id = 7 });
            if (path == "/app/installations/7/access_tokens")
            {
                Assert.StartsWith("Bearer ey", request.Headers.Authorization!.ToString());
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("dark-factory-gui-control", body.RootElement.GetProperty("repositories")[0].GetString());
                var permissions = body.RootElement.GetProperty("permissions");
                var level = permissions.GetProperty("actions").GetString()!;
                Mints.Add(level);
                Assert.Equal(level == "write" ? 1 : 2, permissions.EnumerateObject().Count());
                if (Fault == "authorization" && level == "write") return new(HttpStatusCode.Forbidden);
                return Json(new { token = "GATE_" + level, expires_at = DateTimeOffset.UtcNow.AddMinutes(50) });
            }
            if (path == Root) return Json(new { @private = Fault != "public", fork = Fault == "fork",
                full_name = Fault == "wrong-repo" ? "outsider/control" : RemoteGuiSourceWorkflow.Repository, default_branch = "trunk" });
            if (path == Root + "/commits/trunk") return Json(new { sha = Fault == "mutable-commit" ? "trunk" : Sha });
            if (path.StartsWith(Root + "/contents/")) return Json(new { type = "file", encoding = "base64",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteGuiSourceWorkflow.Text + (Fault == "unsealed" ? "#changed" : ""))) });
            if (path.EndsWith("/dispatches"))
            {
                Dispatch = await request.Content!.ReadAsStringAsync(ct);
                using var body = JsonDocument.Parse(Dispatch);
                nonce = body.RootElement.GetProperty("inputs").GetProperty("nonce").GetString()!;
                return new(HttpStatusCode.NoContent);
            }
            if (path.Contains("/runs?"))
            {
                var run = new { id = 42, display_title = "factory-gui-source-" + (Fault == "unrelated" ? "different" : nonce),
                    @event = Fault == "event" ? "push" : "workflow_dispatch", path = Fault == "path" ? "other.yml" : RemoteGuiSourceWorkflow.Path,
                    head_sha = Fault == "head" ? new string('2', 40) : Sha, head_branch = Fault == "branch" ? "feature" : "trunk",
                    run_attempt = Fault == "attempt" ? 2 : 1, status = Fault == "pending" ? "in_progress" : "completed", conclusion = "success" };
                return Json(new { workflow_runs = Fault == "duplicate" ? new[] { run, run } : new[] { run } });
            }
            if (path.EndsWith("/cancel")) return new(HttpStatusCode.Accepted);
            if (path.Contains("/runs/42/artifacts"))
            {
                var artifact = new { id = 8, name = "factory-gui-source-" + (Fault == "artifact-name" ? "other" : nonce),
                    expired = Fault == "expired", size_in_bytes = 100, workflow_run = new { id = Fault == "artifact-run" ? 99 : 42 } };
                return Json(new { total_count = Fault == "missing-artifact" ? 0 : 1,
                    artifacts = Fault == "missing-artifact" ? Array.Empty<object>() : new object[] { artifact } });
            }
            if (path.EndsWith("/artifacts/8/zip"))
            {
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("recovery-probe.json");
                    if (Fault == "symlink") entry.ExternalAttributes = 0xa1ff << 16;
                    using (var writer = new StreamWriter(entry.Open())) writer.Write(Fault == "oversized" ? new string('x', GuiCollectorAcceptance.MaxReceiptBytes + 1) : Body ?? Receipt().ToJsonString());
                    if (Fault == "extra-file") zip.CreateEntry("planted.py");
                }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) };
            }
            throw new InvalidOperationException("Unexpected fake request " + path);
        }
    }
}
