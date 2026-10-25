using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>Runs only the installed collector's fixed recovery exercise. No caller-supplied command, path, receipt or
/// owner credential is accepted. A valid unclean journal is crash-equivalent evidence, not a literal SIGKILL test.</summary>
public sealed class GuiCollectorAcceptance(HttpClient http, GitHubApp gateApp)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
    public const int MaxReceiptBytes = 64 * 1024;
    const int MaxApiBytes = 512 * 1024;
    const int MaxArchiveBytes = 128 * 1024;
    static readonly RepoRef Repository = RepoRef.Parse(RemoteGuiSourceWorkflow.Repository);
    static string Root => $"repos/{Repository.Owner}/{Repository.Name}";
    static string Workflow => Root + "/actions/workflows/" + Path.GetFileName(RemoteGuiSourceWorkflow.Path);
    internal TimeSpan Deadline { get; init; } = Timeout;
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public sealed record Lease(string WorkspaceSha256, double StartedUtcSeconds, double CompletedUtcSeconds, bool Cleaned, bool Reconciled);
    public sealed record Receipt(int Version, bool LiteralCrash, bool ValidUncleanJournalReconciled, bool FailedCleanupBlockedReuse,
        bool SourceStartedWhileBlocked, bool RestoredOnlyOwnFixture, bool NormalReconciliationRestoredReuse,
        bool FreshWorkspace, string ConfigurationSha256, Lease Lease);
    public sealed record Result(long RunId, string Nonce, string WorkflowCommit, Receipt Receipt);

    public async Task<Result> RunAsync(GuiWorkflowSettings settings, CancellationToken ct)
    {
        if (!settings.Collector.Is(Repository) || !Hash(settings.ConfigurationSha256))
            throw new InvalidDataException("Recovery acceptance requires the exact private GUI control repository and installed configuration pin.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        var token = deadline.Token;
        var nonce = Guid.NewGuid().ToString("N");
        long? identified = null;
        try
        {
            var read = await TokenAsync(false, token);
            using var metadata = await GetAsync(Root, read, token);
            var repo = metadata.RootElement;
            if (repo.GetProperty("private").ValueKind != JsonValueKind.True || repo.GetProperty("fork").ValueKind != JsonValueKind.False
                || !string.Equals(repo.GetProperty("full_name").GetString(), Repository.FullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery acceptance requires the exact private non-fork control repository.");
            var branch = repo.GetProperty("default_branch").GetString();
            if (string.IsNullOrWhiteSpace(branch)) throw new InvalidDataException("Missing control default branch.");
            using var commit = await GetAsync(Root + "/commits/" + Uri.EscapeDataString(branch), read, token);
            var sha = commit.RootElement.GetProperty("sha").GetString();
            if (!RemoteIosWorkflow.Sha(sha)) throw new InvalidDataException("Missing immutable control default commit.");
            using var definition = await GetAsync(Root + "/contents/" + RemoteGuiSourceWorkflow.Path + "?ref=" + sha, read, token);
            var file = definition.RootElement;
            if (file.GetProperty("type").GetString() != "file" || file.GetProperty("encoding").GetString() != "base64"
                || new UTF8Encoding(false, true).GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!)) != RemoteGuiSourceWorkflow.Text)
                throw new InvalidDataException("Recovery acceptance refuses an unsealed control workflow.");
            var write = await TokenAsync(true, token);
            using (var request = GitHubApp.Request(HttpMethod.Post, Workflow + "/dispatches", "Bearer", write))
            {
                // Required source fields are fixed placeholders; the sealed recovery branch never consumes them or fetches source.
                request.Content = JsonContent.Create(new { @ref = branch, inputs = new {
                    acceptance_probe = "recovery", nonce, source_repository = "michaeltrefry/LensWorks", source_sha = sha,
                    plan = "e30=", source_run_id = "1", source_run_attempt = "1" } });
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                Success(response);
            }
            while (true)
            {
                using var runs = await GetAsync(Workflow + "/runs?event=workflow_dispatch&per_page=100", read, token);
                var matches = runs.RootElement.GetProperty("workflow_runs").EnumerateArray()
                    .Where(r => r.GetProperty("display_title").GetString() == "factory-gui-source-" + nonce).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("Ambiguous recovery workflow correlation.");
                if (matches.Length == 1)
                {
                    var run = matches[0];
                    var id = run.GetProperty("id").GetInt64();
                    if (id <= 0 || run.GetProperty("event").GetString() != "workflow_dispatch"
                        || run.GetProperty("path").GetString() != RemoteGuiSourceWorkflow.Path
                        || run.GetProperty("head_sha").GetString() != sha || run.GetProperty("head_branch").GetString() != branch
                        || run.GetProperty("run_attempt").GetInt32() != 1 || identified is { } previous && id != previous)
                        throw new InvalidDataException("Recovery workflow has wrong run provenance.");
                    identified = id;
                    if (run.GetProperty("status").GetString() == "completed")
                    {
                        if (run.GetProperty("conclusion").GetString() != "success") throw new InvalidDataException("Collector recovery exercise did not succeed.");
                        return new(id, nonce, sha!, await ReceiptAsync(id, nonce, read, settings.ConfigurationSha256, token));
                    }
                }
                await Task.Delay(PollInterval, token);
            }
        }
        catch (OperationCanceledException) when (identified is { })
        {
            // Cancel only an actually identified, provenance-checked run. A missing correlation is never guessed.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                using var cancel = GitHubApp.Request(HttpMethod.Post, Root + $"/actions/runs/{identified}/cancel", "Bearer", await TokenAsync(true, cleanup.Token));
                using var response = await http.SendAsync(cancel, HttpCompletionOption.ResponseHeadersRead, cleanup.Token);
                if (response.StatusCode != System.Net.HttpStatusCode.Conflict) Success(response);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { throw new InvalidOperationException($"Recovery acceptance was cancelled; cancellation of exact run {identified} could not be confirmed. Check that run before retrying."); }
            throw;
        }
    }

    async Task<Receipt> ReceiptAsync(long run, string nonce, string token, string hash, CancellationToken ct)
    {
        using var listing = await GetAsync(Root + $"/actions/runs/{run}/artifacts?per_page=100", token, ct);
        var artifacts = listing.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        if (listing.RootElement.GetProperty("total_count").GetInt32() != 1 || artifacts.Length != 1)
            throw new InvalidDataException("Recovery acceptance requires exactly one artifact.");
        var artifact = artifacts[0];
        var id = artifact.GetProperty("id").GetInt64();
        if (id <= 0 || artifact.GetProperty("name").GetString() != "factory-gui-source-" + nonce
            || artifact.GetProperty("expired").GetBoolean() || artifact.GetProperty("size_in_bytes").GetInt64() is <= 0 or > MaxArchiveBytes
            || artifact.GetProperty("workflow_run").GetProperty("id").GetInt64() != run)
            throw new InvalidDataException("Wrong recovery artifact provenance or size.");
        using var request = GitHubApp.Request(HttpMethod.Get, Root + $"/actions/artifacts/{id}/zip", "Bearer", token);
        // OutboundHttp.GitHubApi strips Authorization on GitHub's signed storage redirect, as for gate artifacts.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Success(response);
        using var memory = new MemoryStream(await BoundedAsync(response.Content, MaxArchiveBytes, ct));
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        if (archive.Entries.Count != 1) throw new InvalidDataException("Recovery archive must contain only its receipt.");
        var entry = archive.Entries[0];
        var kind = (entry.ExternalAttributes >> 16) & 0xf000;
        if (entry.FullName != "recovery-probe.json" || entry.Length is <= 0 or > MaxReceiptBytes || kind is not (0 or 0x8000)
            || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0)
            throw new InvalidDataException("Recovery receipt must be one bounded regular file.");
        await using var stream = entry.Open();
        using var content = new StreamContent(stream);
        using var json = JsonDocument.Parse(await BoundedAsync(content, MaxReceiptBytes, ct));
        return Validate(json.RootElement, hash);
    }

    internal static Receipt Validate(JsonElement root, string hash)
    {
        RemoteIosNewTests.Unique(root);
        Exact(root, "version", "literalCrash", "validUncleanJournalReconciled", "failedCleanupBlockedReuse", "sourceStartedWhileBlocked",
            "restoredOnlyOwnFixture", "normalReconciliationRestoredReuse", "freshWorkspace", "configurationSha256", "lease");
        var lease = root.GetProperty("lease");
        Exact(lease, "workspaceSha256", "startedUtcSeconds", "completedUtcSeconds", "cleaned", "reconciled");
        var value = JsonSerializer.Deserialize<Receipt>(root.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Missing recovery receipt.");
        if (value.Version != 1 || value.LiteralCrash || !value.ValidUncleanJournalReconciled || !value.FailedCleanupBlockedReuse
            || value.SourceStartedWhileBlocked || !value.RestoredOnlyOwnFixture || !value.NormalReconciliationRestoredReuse
            || !value.FreshWorkspace || !Hash(value.ConfigurationSha256) || value.ConfigurationSha256 != hash
            || value.Lease is null || !Hash(value.Lease.WorkspaceSha256) || !value.Lease.Cleaned || !value.Lease.Reconciled
            || !double.IsFinite(value.Lease.StartedUtcSeconds) || !double.IsFinite(value.Lease.CompletedUtcSeconds)
            || value.Lease.StartedUtcSeconds <= 0 || value.Lease.CompletedUtcSeconds < value.Lease.StartedUtcSeconds)
            throw new InvalidDataException("Collector recovery receipt does not prove cleanup blocking and restored fresh reuse for the pinned configuration.");
        return value;
    }

    static bool Hash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static void Exact(JsonElement node, params string[] fields)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()))
            throw new InvalidDataException("Unexpected recovery receipt fields.");
    }
    async Task<string> TokenAsync(bool write, CancellationToken ct) => (await gateApp.CreateInstallationTokenAsync(Repository, ct,
        write ? IosWorkflowGitHub.DispatchPermissions : IosWorkflowGitHub.ReadPermissions)).Token;
    async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, path, "Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Success(response);
        return JsonDocument.Parse(await BoundedAsync(response.Content, MaxApiBytes, ct));
    }
    static void Success(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GUI recovery GitHub request refused (HTTP {(int)response.StatusCode}); check the Gate App installation and Actions permissions.");
    }
    static async Task<byte[]> BoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Oversized recovery response.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("Oversized recovery response.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
