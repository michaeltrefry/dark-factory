using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// Real REST transport for the remote iOS check. The supplied client is OutboundHttp.GitHubApi(); gate installation tokens
/// stay here, never in dispatch inputs, archives or runner environments. No client of the owner's gh login is used.
/// </summary>
public sealed class IosWorkflowGitHub(HttpClient http, GitHubApp gateApp) : IIosWorkflowGitHub
{
    public static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string>
        { ["contents"] = "read", ["actions"] = "read" };
    public static readonly IReadOnlyDictionary<string, string> DispatchPermissions = new Dictionary<string, string>
        { ["actions"] = "write" };
    private const int MaxApiBytes = 512 * 1024;
    private const int MaxArchiveBytes = 2 * 1024 * 1024;
    private static string Root(RepoRef repo) => $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

    public async Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct)
    {
        var token = await ReadToken(repo, ct);
        using var repository = await JsonAsync($"{Root(repo)}", token, ct);
        var branch = repository.RootElement.GetProperty("default_branch").GetString();
        if (string.IsNullOrWhiteSpace(branch)) throw new InvalidDataException("No default branch.");
        using var commit = await JsonAsync($"{Root(repo)}/commits/{Uri.EscapeDataString(branch)}", token, ct);
        var sha = commit.RootElement.GetProperty("sha").GetString()!;
        if (!RemoteIosWorkflow.Sha(sha)) throw new InvalidDataException("No immutable default commit.");
        using var request = GitHubApp.Request(HttpMethod.Get, $"{Root(repo)}/contents/{RemoteIosWorkflow.Path}?ref={sha}", "Bearer", token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await Success(response, ct);
        var text = Encoding.UTF8.GetString(await Bounded(response.Content, MaxApiBytes, ct));
        return new IosSealedWorkflow(branch, sha, text);
    }

    public async Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest input, string correlation, CancellationToken ct)
    {
        if (!RemoteIosWorkflow.Trusted(workflow)) throw new InvalidDataException("Unsealed dispatch.");
        // Obtain only Actions-write, scoped by GitHubApp to this repository, before the sole external write. An App
        // still installed with Actions-read refuses at token creation; never fall back to the owner's credential.
        var token = await gateApp.CreateInstallationTokenAsync(repo, ct, DispatchPermissions);
        using var request = GitHubApp.Request(HttpMethod.Post,
            $"{Root(repo)}/actions/workflows/{System.IO.Path.GetFileName(RemoteIosWorkflow.Path)}/dispatches", "Bearer", token.Token);
        // GitHub's workflow_dispatch ref is a branch/tag, not a PR SHA. It comes ONLY from the authenticated
        // default-branch read. The caller accepts results only from the exact default commit it verified; a branch
        // movement (including an owner replacing the workflow) invalidates the run, never adopts another definition.
        request.Content = JsonContent.Create(new { @ref = workflow.DefaultBranch, inputs = new {
            nonce = correlation, @base = input.Base, head = input.Head, tests = JsonSerializer.Serialize(input.Tests) } });
        using var response = await http.SendAsync(request, ct);
        await Success(response, ct);
    }

    public async Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
    {
        using var json = await JsonAsync($"{Root(repo)}/actions/workflows/{System.IO.Path.GetFileName(RemoteIosWorkflow.Path)}/runs?event=workflow_dispatch&per_page=100",
            await ReadToken(repo, ct), ct);
        // Bounded to one page; a very busy repo can miss the correlation, which is error, never an unrelated success.
        return json.RootElement.GetProperty("workflow_runs").Deserialize<IosWorkflowRun[]>() ?? throw new InvalidDataException("No workflow runs.");
    }

    public async Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
    {
        var token = await ReadToken(repo, ct);
        using var json = await JsonAsync($"{Root(repo)}/actions/runs/{run.Id}/artifacts?per_page=100", token, ct);
        var root = json.RootElement;
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
        if (root.GetProperty("total_count").GetInt32() != artifacts.Length) throw new InvalidDataException("Incomplete artifacts listing.");
        using var baseResult = await SideResult("base");
        using var headResult = await SideResult("head");
        var b = baseResult.RootElement;
        var h = headResult.RootElement;
        if (b.GetProperty("base").GetString() != h.GetProperty("base").GetString() || b.GetProperty("head").GetString() != h.GetProperty("head").GetString())
            throw new InvalidDataException("Different side commits.");
        var baseRows = b.GetProperty("tests").EnumerateArray().ToArray();
        var headRows = h.GetProperty("tests").EnumerateArray().ToArray();
        if (baseRows.Length != headRows.Length) throw new InvalidDataException("Different side test sets.");
        var rows = baseRows.Zip(headRows).Select(pair => {
            if (pair.First.GetProperty("test").GetString() != pair.Second.GetProperty("test").GetString())
                throw new InvalidDataException("Different side test identities.");
            return new { test = pair.First.GetProperty("test").GetString(), @base = pair.First.GetProperty("base").GetString(),
                head = pair.Second.GetProperty("head").GetString() };
        }).ToArray();
        return JsonSerializer.Serialize(new { version = 1, nonce = correlation, @base = b.GetProperty("base").GetString(),
            head = b.GetProperty("head").GetString(), tests = rows });

        async Task<JsonDocument> SideResult(string side)
        {
            var artifact = artifacts.Single(a => a.GetProperty("name").GetString() == "factory-ios-result-" + correlation + "-" + side);
            if (artifact.GetProperty("expired").GetBoolean() || artifact.GetProperty("size_in_bytes").GetInt64() > MaxArchiveBytes
                || artifact.GetProperty("workflow_run").GetProperty("id").GetInt64() != run.Id)
                throw new InvalidDataException("Invalid result artifact provenance or size.");
            var id = artifact.GetProperty("id").GetInt64();
            using var request = GitHubApp.Request(HttpMethod.Get, $"{Root(repo)}/actions/artifacts/{id}/zip", "Bearer", token);
            // OutboundHttp's standard handler strips Authorization on the signed blob-storage redirect.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await Success(response, ct);
            using var memory = new MemoryStream(await Bounded(response.Content, MaxArchiveBytes, ct));
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "result.json"
                || archive.Entries[0].Length > RemoteIosNewTests.MaxResultBytes)
                throw new InvalidDataException("Invalid result archive.");
            await using var stream = archive.Entries[0].Open();
            using var content = new StreamContent(stream);
            var document = JsonDocument.Parse(await Bounded(content, RemoteIosNewTests.MaxResultBytes, ct));
            try
            {
                var result = document.RootElement;
                RemoteIosNewTests.Unique(result);
                if (result.GetProperty("version").GetInt32() != 1 || result.GetProperty("nonce").GetString() != correlation
                    || result.GetProperty("side").GetString() != side || !RemoteIosWorkflow.Sha(result.GetProperty("base").GetString())
                    || !RemoteIosWorkflow.Sha(result.GetProperty("head").GetString()))
                    throw new InvalidDataException("Wrong side result identity.");
                return document;
            }
            catch { document.Dispose(); throw; }
        }
    }

    private async Task<string> ReadToken(RepoRef repo, CancellationToken ct) =>
        (await gateApp.CreateInstallationTokenAsync(repo, ct, ReadPermissions)).Token;
    private async Task<JsonDocument> JsonAsync(string path, string token, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, path, "Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await Success(response, ct);
        return JsonDocument.Parse(await Bounded(response.Content, MaxApiBytes, ct));
    }
    private static Task Success(HttpResponseMessage response, CancellationToken ct)
    {
        // Do not put GitHub's arbitrary response text, or a token embedded in it, into a step error.
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub iOS workflow request failed: {(int)response.StatusCode}.");
        return Task.CompletedTask;
    }
    private static async Task<byte[]> Bounded(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Oversized workflow response.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Oversized workflow response.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
