using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public sealed class IosArtifactInventoryTests
{
    const string Root = "/repos/acme/ios";
    const string Base = "1111111111111111111111111111111111111111";
    const string Head = "2222222222222222222222222222222222222222";
    const string WorkflowCommit = "3333333333333333333333333333333333333333";
    const string Nonce = "0123456789abcdef0123456789abcdef";
    static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");

    [Theory]
    [InlineData(false, "base")]
    [InlineData(false, "head")]
    [InlineData(true, "base")]
    [InlineData(true, "head")]
    public async Task Same_run_source_transfer_is_not_counted_as_the_opposite_execution_VM(bool gui, string side)
    {
        var entries = new JsonArray(Source(), Result(side));
        var api = Api(entries);
        string? answer = null;
        var error = await Record.ExceptionAsync(async () => answer = await Client(api, gui).ResultAsync(new("acme", "ios"), Run(gui), Nonce, CancellationToken.None));
        Assert.Null(error);
        Assert.NotNull(answer);
        using var json = JsonDocument.Parse(answer);
        Assert.Equal(side, json.RootElement.GetProperty("side").GetString());
        Assert.Equal("ran", json.RootElement.GetProperty("execution").GetProperty("status").GetString());
        Assert.Single(api.Requests.Where(r => r.PathAndQuery.EndsWith("/zip")));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("/artifacts/77/zip"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_legacy_result_sides_are_combined_with_the_same_run_source_transfer_present(bool gui)
    {
        var api = Api(new JsonArray(Result("head"), Source(), Result("base")), legacy: true);
        string? answer = null;
        var error = await Record.ExceptionAsync(async () => answer = await Client(api, gui).ResultAsync(new("acme", "ios"), Run(gui), Nonce, CancellationToken.None));
        Assert.Null(error);
        Assert.NotNull(answer);
        using var json = JsonDocument.Parse(answer);
        var test = Assert.Single(json.RootElement.GetProperty("tests").EnumerateArray());
        Assert.Equal("failed", test.GetProperty("base").GetString());
        Assert.Equal("passed", test.GetProperty("head").GetString());
        Assert.Equal(2, api.Requests.Count(r => r.PathAndQuery.EndsWith("/zip")));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.Contains("/artifacts/77/zip"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong-nonce")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-result")]
    [InlineData("duplicate-id")]
    [InlineData("wrong-source-run")]
    [InlineData("wrong-result-run")]
    [InlineData("expired-source")]
    [InlineData("negative-source-size")]
    [InlineData("oversized-source")]
    [InlineData("malformed-source-id")]
    [InlineData("missing-source-provenance")]
    [InlineData("incomplete-listing")]
    public async Task The_complete_inventory_rejects_unknown_duplicate_malformed_and_wrong_run_metadata_before_any_download(string mutation)
    {
        var source = Source();
        var result = Result("head");
        var entries = new JsonArray(source, result);
        switch (mutation)
        {
            case "unknown": source["name"] = "unrelated-artifact"; break;
            case "wrong-nonce": result["name"] = "factory-ios-result-another-nonce-head"; break;
            case "duplicate-source": entries.Add(Source()); break;
            case "duplicate-result": entries.Add(Result("head")); break;
            case "duplicate-id": source["id"] = result["id"]!.GetValue<int>(); break;
            case "wrong-source-run": source["workflow_run"]!["id"] = 13; break;
            case "wrong-result-run": result["workflow_run"]!["id"] = 13; break;
            case "expired-source": source["expired"] = true; break;
            case "negative-source-size": source["size_in_bytes"] = -1; break;
            case "oversized-source": source["size_in_bytes"] = 202L * 1024 * 1024; break;
            case "malformed-source-id": source["id"] = "77"; break;
            case "missing-source-provenance": source.Remove("workflow_run"); break;
        }
        var api = Api(entries, count: entries.Count + (mutation == "incomplete-listing" ? 1 : 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(api, false).ResultAsync(new("acme", "ios"), Run(false), Nonce, CancellationToken.None));
        Assert.DoesNotContain(api.Requests, r => r.PathAndQuery.EndsWith("/zip"));
    }

    static JsonObject Source() => Artifact(77, "factory-ios-sources", 12 * 1024 * 1024);
    static JsonObject Result(string side) => Artifact(side == "base" ? 8 : 9, "factory-ios-result-" + Nonce + "-" + side, 300);
    static JsonObject Artifact(int id, string name, long size) => new() {
        ["id"] = id, ["name"] = name, ["size_in_bytes"] = size, ["expired"] = false, ["workflow_run"] = new JsonObject { ["id"] = 12 }
    };
    static IosWorkflowRun Run(bool gui) => new(12, "factory-ios-" + Nonce, WorkflowCommit, "workflow_dispatch", gui ? RemoteGuiWorkflow.Path : RemoteIosWorkflow.Path, 1, "completed", "success");
    static FakeApi Api(JsonArray entries, bool legacy = false, int? count = null)
    {
        var listing = new JsonObject { ["total_count"] = count ?? entries.Count, ["artifacts"] = entries };
        return new FakeApi()
            .On("GET " + Root + "/installation", HttpStatusCode.OK, """{"id":5}""")
            .On("POST /app/installations/5/access_tokens", HttpStatusCode.Created,
                JsonSerializer.Serialize(new { token = "fake-gate-token", expires_at = Now.AddMinutes(30) }))
            .On("GET " + Root + "/actions/runs/12/artifacts", HttpStatusCode.OK, listing.ToJsonString())
            .On("GET " + Root + "/actions/artifacts/8/zip", _ => Zip(Side("base", legacy)))
            .On("GET " + Root + "/actions/artifacts/9/zip", _ => Zip(Side("head", legacy)));
    }
    static string Side(string side, bool legacy) => RemoteIosNewTestsTests.WithEnvironment(legacy
        ? JsonSerializer.Serialize(new { version = 1, nonce = Nonce, @base = Base, head = Head, side,
            tests = new[] { new { test = "TallyTests/TallyTests/testNew", @base = "failed", head = "passed" } } })
        : JsonSerializer.Serialize(new { version = 1, nonce = Nonce, @base = Base, head = Head, side,
            execution = new { status = "ran", files = Array.Empty<object>() } }));
    static IosWorkflowGitHub Client(FakeApi api, bool gui) => new(api.Client("https://api.github.com/"),
        new GitHubApp(api.Client("https://api.github.com/"), "123", Pem, new FixedTime(Now)), gui);
    static HttpResponseMessage Zip(string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("result.json").Open())) writer.Write(text);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
    }
}
