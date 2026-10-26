using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// Real REST transport for the remote iOS check. The supplied client is OutboundHttp.GitHubApi(); gate installation tokens
/// stay here, never in dispatch inputs, archives or runner environments. No client of the owner's gh login is used.
/// </summary>
public sealed class IosWorkflowGitHub(HttpClient http, GitHubApp gateApp, bool gui = false) : IIosWorkflowGitHub
{
    private string WorkflowPath => gui ? RemoteGuiWorkflow.Path : RemoteIosWorkflow.Path;
    public static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string>
        { ["contents"] = "read", ["actions"] = "read" };
    public static readonly IReadOnlyDictionary<string, string> DispatchPermissions = new Dictionary<string, string>
        { ["actions"] = "write" };
    private const int MaxApiBytes = 512 * 1024;
    private const int MaxArchiveBytes = 3 * 1024 * 1024;
    // The sealed transfer contains at most two 100 MiB source ZIPs plus artifact ZIP overhead. It is validated,
    // not downloaded as test evidence; result archives retain their much smaller bound below.
    private const long MaxSourceTransferBytes = 201L * 1024 * 1024;
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
        using var request = GitHubApp.Request(HttpMethod.Get, $"{Root(repo)}/contents/{WorkflowPath}?ref={sha}", "Bearer", token);
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
        var mayHaveSent = false;
        var phase = "validation";
        try
        {
            if (!(gui ? RemoteGuiWorkflow.Trusted(workflow) : RemoteIosWorkflow.Trusted(workflow))) throw new InvalidDataException("Unsealed dispatch.");
            var inputs = new Dictionary<string, string> { ["nonce"] = correlation, ["base"] = input.Base,
                ["head"] = input.Head, ["tests"] = JsonSerializer.Serialize(input.Tests) };
            if (gui) inputs["source_repository"] = input.SourceRepository ?? throw new InvalidDataException("Missing trusted source repository.");
            if (input.Execution is { } execution)
            {
                if (Encoding.UTF8.GetByteCount(execution) > RemoteIosNewTests.MaxExecutionBytes) throw new InvalidDataException("Oversized execution descriptor.");
                inputs["execution"] = execution;
            }
            if (!double.IsFinite(input.ExecutionBudgetSeconds) || input.ExecutionBudgetSeconds < 0)
                throw new InvalidDataException("Invalid iOS execution budget.");
            if (input.ExecutionBudgetSeconds > 0)
            {
                inputs["execution_budget_seconds"] = input.ExecutionBudgetSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                inputs["execution_deadline_unix_seconds"] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + input.ExecutionBudgetSeconds)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            var payload = JsonSerializer.Serialize(new { @ref = workflow.DefaultBranch, inputs });
            // GitHub limits the complete workflow_dispatch input payload, not just the execution field. Count UTF-8
            // after JSON escaping (test ids and replacements may be non-ASCII); reject before minting a write token.
            if (Encoding.UTF8.GetByteCount(payload) > 65535) throw new InvalidDataException("Oversized workflow dispatch inputs.");
            // Only Actions-write, scoped to this repository; never fall back to the owner's credential. The ref is ONLY
            // the authenticated default branch. A movement is rejected by the exact sealed-commit run provenance check.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            phase = "authorization";
            var token = await gateApp.CreateInstallationTokenAsync(repo, ct, DispatchPermissions);
            phase = "preparation";
            if (input.ExecutionBudgetSeconds > 0)
            {
                var remaining = input.ExecutionBudgetSeconds - elapsed.Elapsed.TotalSeconds;
                if (remaining <= 0) throw new OperationCanceledException(ct);
                inputs["execution_budget_seconds"] = remaining.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                payload = JsonSerializer.Serialize(new { @ref = workflow.DefaultBranch, inputs });
                if (Encoding.UTF8.GetByteCount(payload) > 65535) throw new InvalidDataException("Oversized workflow dispatch inputs.");
            }
            using var request = GitHubApp.Request(HttpMethod.Post,
                $"{Root(repo)}/actions/workflows/{System.IO.Path.GetFileName(WorkflowPath)}/dispatches", "Bearer", token.Token);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            ct.ThrowIfCancellationRequested();
            mayHaveSent = true; // From this exact boundary onward, a lost answer never proves an unsent dispatch.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await Success(response, ct);
        }
        catch (Exception ex) when (!mayHaveSent && ex is not OutOfMemoryException)
        {
            var cancelled = ex is OperationCanceledException;
            var reason = cancelled ? "The iOS dispatch was cancelled before the workflow POST; no workflow was sent."
                : phase == "validation" ? "The iOS workflow dispatch inputs failed local validation. Check the sealed workflow, execution descriptor and payload limits; no workflow was sent."
                : phase == "authorization" ? "The gate App could not obtain repository Actions write authorization. Check its installation and permissions; no workflow was sent."
                : "The iOS workflow POST could not be prepared. Check the execution budget and dispatch inputs; no workflow was sent.";
            // Neither arbitrary exception messages nor GitHub token-mint response bodies escape this boundary.
            throw new IosWorkflowNotSentException(reason, cancelled);
        }
    }

    public async Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct)
    {
        if (run.Id <= 0 || run.Event != "workflow_dispatch" || run.Path != WorkflowPath || run.Attempt != 1
            || !RemoteIosWorkflow.Sha(run.HeadSha) || !System.Text.RegularExpressions.Regex.IsMatch(run.Title, "^factory-ios-[0-9a-f]{32}$"))
            throw new InvalidDataException("Uncorrelated iOS cancellation.");
        var token = await gateApp.CreateInstallationTokenAsync(repo, ct, DispatchPermissions);
        using var request = GitHubApp.Request(HttpMethod.Post, $"{Root(repo)}/actions/runs/{run.Id}/cancel", "Bearer", token.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // A completion racing cancellation is reconciled by the caller's subsequent terminal read, never assumed here.
        if (response.StatusCode != HttpStatusCode.Conflict) await Success(response, ct);
    }

    public async Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
    {
        using var json = await JsonAsync($"{Root(repo)}/actions/workflows/{System.IO.Path.GetFileName(WorkflowPath)}/runs?event=workflow_dispatch&per_page=100",
            await ReadToken(repo, ct), ct);
        // Bounded to one page; a very busy repo can miss the correlation, which is error, never an unrelated success.
        return json.RootElement.GetProperty("workflow_runs").Deserialize<IosWorkflowRun[]>() ?? throw new InvalidDataException("No workflow runs.");
    }

    public async Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
    {
        var token = await ReadToken(repo, ct);
        using var json = await JsonAsync($"{Root(repo)}/actions/runs/{run.Id}/artifacts?per_page=100", token, ct);
        var root = json.RootElement;
        RemoteIosNewTests.Unique(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("artifacts", out var listing) || listing.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("total_count", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var count))
            throw new InvalidDataException("Malformed artifacts listing.");
        var inventory = listing.EnumerateArray().ToArray();
        if (count != inventory.Length || inventory.Length > 3) throw new InvalidDataException("Incomplete or oversized artifacts listing.");
        var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<long>();
        foreach (var artifact in inventory)
        {
            if (artifact.ValueKind != JsonValueKind.Object || !artifact.TryGetProperty("name", out var named) || named.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Malformed artifact identity.");
            var name = named.GetString()!;
            var sourceTransfer = name == "factory-ios-sources";
            if (!sourceTransfer && name != "factory-ios-result-" + correlation + "-base" && name != "factory-ios-result-" + correlation + "-head")
                throw new InvalidDataException("Unknown artifact or result correlation.");
            if (!names.Add(name) || !artifact.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.Number
                || !identity.TryGetInt64(out var id) || id <= 0 || !ids.Add(id)
                || !artifact.TryGetProperty("expired", out var expired) || expired.ValueKind != JsonValueKind.False
                || !artifact.TryGetProperty("size_in_bytes", out var sized) || sized.ValueKind != JsonValueKind.Number || !sized.TryGetInt64(out var size)
                || size < 0 || size > (sourceTransfer ? MaxSourceTransferBytes : MaxArchiveBytes)
                || !artifact.TryGetProperty("workflow_run", out var provenance) || provenance.ValueKind != JsonValueKind.Object
                || !provenance.TryGetProperty("id", out var parent) || parent.ValueKind != JsonValueKind.Number
                || !parent.TryGetInt64(out var parentId) || parentId != run.Id || parentId <= 0)
                throw new InvalidDataException("Duplicate or invalid artifact provenance, expiry or size.");
            if (!sourceTransfer) results.Add(name, artifact);
        }
        // The source-transfer artifact is produced by BOTH sealed workflows in the SAME run. Its metadata must be
        // valid, but it is not a result side and must never alter single-side versus two-side evidence selection.
        var artifacts = results.Values.ToArray();
        // General execution dispatches schedule only the requested side's VM. Its one artifact still has the
        // ordinary side provenance checks; ParseExecution additionally binds its side and hash to the original plan.
        if (artifacts.Length == 1)
        {
            var name = artifacts[0].GetProperty("name").GetString();
            var side = name == "factory-ios-result-" + correlation + "-base" ? "base"
                : name == "factory-ios-result-" + correlation + "-head" ? "head"
                : throw new InvalidDataException("Unexpected single-side artifact.");
            using var single = await SideResult(side);
            var result = single.RootElement;
            RemoteIosNewTests.EnvironmentOf(result, gui);
            if (!result.TryGetProperty("execution", out var execution) || execution.ValueKind != JsonValueKind.Object || execution.GetProperty("status").GetString() == "unused")
                throw new InvalidDataException("Missing executed side evidence.");
            return result.GetRawText();
        }
        if (artifacts.Length != 2) throw new InvalidDataException("Unexpected result artifact count.");
        using var baseResult = await SideResult("base");
        using var headResult = await SideResult("head");
        var b = baseResult.RootElement;
        var h = headResult.RootElement;
        var environment = RemoteIosNewTests.EnvironmentOf(b);
        if (environment != RemoteIosNewTests.EnvironmentOf(h))
            throw new InvalidDataException("Different side environments.");
        if (b.GetProperty("base").GetString() != h.GetProperty("base").GetString() || b.GetProperty("head").GetString() != h.GetProperty("head").GetString())
            throw new InvalidDataException("Different side commits.");
        if (b.TryGetProperty("execution", out var baseExecution) || h.TryGetProperty("execution", out _))
        {
            var headExecution = h.GetProperty("execution");
            var baseUnused = baseExecution.GetProperty("status").GetString() == "unused";
            var headUnused = headExecution.GetProperty("status").GetString() == "unused";
            if (baseUnused == headUnused || b.GetProperty("executionHash").GetString() != h.GetProperty("executionHash").GetString())
                throw new InvalidDataException("Different or ambiguous side execution plans.");
            return (baseUnused ? h : b).GetRawText();
        }
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
            head = b.GetProperty("head").GetString(), environment, tests = rows });

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
        if (!response.IsSuccessStatusCode) throw new IosWorkflowException("remote-github-http",
            $"GitHub rejected the iOS workflow request (HTTP {(int)response.StatusCode}). Check the gate App installation and its repository Actions permissions; no usable executed evidence.");
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
