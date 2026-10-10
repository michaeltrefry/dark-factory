using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.GitHub;

/// <summary>A merge attempt's result. <see cref="HeadMoved"/>: GitHub refused because the head is no longer the gated commit.</summary>
public sealed record MergeResult(bool Merged, string? CommitSha, bool HeadMoved, string? Message);

/// <summary>What Review, CI and the merge gate read from GitHub, and the gate's one write: the merge.</summary>
public interface IGateGitHub
{
    Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct);

    /// <summary>The unified diff of <paramref name="headSha"/> against its merge base with <paramref name="baseSha"/>.</summary>
    Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct);

    /// <summary>Every file path in the tree of commit <paramref name="sha"/> (what the review panel compares new files with).</summary>
    Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct);

    /// <summary>
    /// The text of <see cref="GatePolicy.Path"/> at <paramref name="baseRef"/> (a branch or a commit SHA; the gate passes the
    /// PR's base commit), or null when it does not exist.
    /// </summary>
    Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct);

    /// <summary>Every check run, commit status and check suite of <paramref name="sha"/>.</summary>
    Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct);

    /// <summary>
    /// The log of a failing check (sc-25383): for a check run of GitHub Actions its job's log (at most the last
    /// <see cref="GitHubGate.MaxLogBytes"/>, unprocessed: the caller cleans, redacts and bounds it, <see cref="CiHeal.Excerpt"/>);
    /// when that cannot be read, the check run's own output and annotations, saying why. A commit status has no log.
    /// </summary>
    Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct);

    /// <summary>
    /// The current tip of <paramref name="baseRef"/> (the PR's base branch) and how many of its commits <paramref name="headSha"/>
    /// does not contain (sc-25384: the merge queue updates a head that is behind before CI and the merge).
    /// </summary>
    Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct);

    /// <summary>Merges PR <paramref name="number"/> only if its head is still <paramref name="headSha"/> (GitHub enforces it).</summary>
    Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct);
}

/// <summary>
/// The gate's GitHub access, as the factory's separate gate App: the merge-capable credential only the gate holds. Workers'
/// pushes and PRs use the other App (<see cref="GitHubApp"/> in <see cref="GitHubPullRequests"/>), which cannot merge: only
/// the gate App may bypass the "only admins write outside factory/**" ruleset, and only by merging a pull request
/// (<see cref="RepoProtection"/>). Reads use a read-only token; only <see cref="MergeAsync"/> mints a write token. Tokens are
/// minted per call and never cached.
/// </summary>
public sealed class GitHubGate(HttpClient http, GitHubApp gateApp) : IGateGitHub
{
    public static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string>
    {
        ["contents"] = "read",
        ["pull_requests"] = "read",
        ["checks"] = "read",
        ["statuses"] = "read",
    };

    /// <summary>
    /// What reading a job's log needs (GitHub: <c>GET /repos/{owner}/{repo}/actions/jobs/{job_id}/logs</c> requires the App's
    /// "Actions: read" permission). Requested only for that call; without it the check run's output is read instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LogPermissions = new Dictionary<string, string>
    {
        ["actions"] = "read",
    };

    /// <summary>At most this many bytes of a job's log are kept (its end, where the failure is).</summary>
    public const int MaxLogBytes = 256 * 1024;

    /// <summary>
    /// How long reading a job's log may take, its body included (HttpClient's own timeout ends when the headers arrive, and a
    /// log is streamed after that). Past it the check run's output is read instead.
    /// </summary>
    public TimeSpan LogReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private const int MaxAnnotations = 50;

    public static readonly IReadOnlyDictionary<string, string> MergePermissions = new Dictionary<string, string>
    {
        ["contents"] = "write",
        ["pull_requests"] = "write",
    };

    private const int PageSize = 100;

    public async Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/pulls/{number}", "Bearer", await ReadTokenAsync(repo, ct));
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"read pull request #{number}", ct);
        var pull = (await response.Content.ReadFromJsonAsync<PullDto>(ct))!;
        return new PullFacts(pull.Number, pull.HtmlUrl, pull.State == "open", pull.Merged, pull.Draft, pull.Head.Sha, pull.Base.Ref, pull.Base.Sha,
            pull.MergeCommitSha, pull.ChangedFiles);
    }

    public async Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/compare/{baseSha}...{headSha}", "Bearer",
            await ReadTokenAsync(repo, ct));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.diff"));
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"read the diff of {headSha}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/git/trees/{sha}?recursive=1", "Bearer",
            await ReadTokenAsync(repo, ct));
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"read the file tree of {sha}", ct);
        var tree = (await response.Content.ReadFromJsonAsync<TreeDto>(ct))!;
        return new RepoFiles(tree.Tree.Where(e => e.Type == "blob").Select(e => e.Path).ToList(), tree.Truncated);
    }

    public async Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/contents/{GatePolicy.Path}?ref={Uri.EscapeDataString(baseRef)}", "Bearer", await ReadTokenAsync(repo, ct));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await GitHubApp.EnsureSuccess(response, $"read {GatePolicy.Path} on {baseRef}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct)
    {
        var token = await ReadTokenAsync(repo, ct);
        using var runsRequest = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/commits/{sha}/check-runs?per_page={PageSize}", "Bearer", token);
        using var runsResponse = await http.SendAsync(runsRequest, ct);
        await GitHubApp.EnsureSuccess(runsResponse, $"read the check runs of {sha}", ct);
        var runs = (await runsResponse.Content.ReadFromJsonAsync<CheckRunsDto>(ct))!;

        using var statusRequest = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/commits/{sha}/status?per_page={PageSize}", "Bearer", token);
        using var statusResponse = await http.SendAsync(statusRequest, ct);
        await GitHubApp.EnsureSuccess(statusResponse, $"read the commit statuses of {sha}", ct);
        var status = (await statusResponse.Content.ReadFromJsonAsync<CombinedStatusDto>(ct))!;

        using var suitesRequest = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/commits/{sha}/check-suites?per_page={PageSize}", "Bearer", token);
        using var suitesResponse = await http.SendAsync(suitesRequest, ct);
        await GitHubApp.EnsureSuccess(suitesResponse, $"read the check suites of {sha}", ct);
        var suites = (await suitesResponse.Content.ReadFromJsonAsync<CheckSuitesDto>(ct))!;

        var checks = runs.CheckRuns.Select(r => new CheckFact(r.Name, r.Status == "completed", r.Conclusion, r.Id))
            .Concat(status.Statuses.Select(s => new CheckFact(s.Context, s.State != "pending", s.State is "error" ? "failure" : s.State)))
            .ToList();
        var complete = runs.TotalCount <= runs.CheckRuns.Count && status.TotalCount <= status.Statuses.Count
            && suites.TotalCount <= suites.CheckSuites.Count;
        return new CiFacts(sha, checks, complete,
            suites.CheckSuites.Select(s => new CheckSuiteFact(s.App?.Slug ?? "unknown", s.Status == "completed", s.Conclusion, s.LatestCheckRunsCount)).ToList());
    }

    public async Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct)
    {
        if (check.Id is not { } id)
        {
            return "(a commit status: GitHub keeps no log for it)";
        }
        string why;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LogReadTimeout);
            var token = (await gateApp.CreateInstallationTokenAsync(repo, timeout.Token, LogPermissions)).Token;
            // GitHub answers with a redirect to a signed download URL, which HttpClient follows without the Authorization header.
            using var request = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/actions/jobs/{id}/logs", "Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return await TailAsync(response.Content, MaxLogBytes, timeout.Token);
            }
            why = $"GitHub answered {(int)response.StatusCode}";
        }
        catch (InvalidOperationException ex)
        {
            // E.g. the gate App lacks "Actions: read", so no token with it can be minted.
            why = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            why = ex.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout, or LogReadTimeout while the body streamed; the caller's cancellation propagates.
            why = "reading it timed out";
        }
        return $"(the job log could not be read: {why}; the check run's output follows)\n{await CheckRunOutputAsync(repo, id, ct)}";
    }

    /// <summary>A check run's output (title, summary, text) and its first annotations, readable with "Checks: read".</summary>
    private async Task<string> CheckRunOutputAsync(RepoRef repo, long id, CancellationToken ct)
    {
        var token = await ReadTokenAsync(repo, ct);
        using var runRequest = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/check-runs/{id}", "Bearer", token);
        using var runResponse = await http.SendAsync(runRequest, ct);
        await GitHubApp.EnsureSuccess(runResponse, $"read check run {id}", ct);
        var run = (await runResponse.Content.ReadFromJsonAsync<CheckRunDto>(ct))!;
        using var notesRequest = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/check-runs/{id}/annotations?per_page={MaxAnnotations}", "Bearer", token);
        using var notesResponse = await http.SendAsync(notesRequest, ct);
        await GitHubApp.EnsureSuccess(notesResponse, $"read the annotations of check run {id}", ct);
        var notes = (await notesResponse.Content.ReadFromJsonAsync<List<AnnotationDto>>(ct))!;
        var text = new StringBuilder();
        foreach (var part in new[] { run.Output?.Title, run.Output?.Summary, run.Output?.Text }.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            text.AppendLine(part);
        }
        foreach (var note in notes)
        {
            text.AppendLine($"{note.Level ?? "notice"}: {note.Path}{(note.StartLine is { } line ? $":{line}" : "")}: {note.Message}");
        }
        return text.Length == 0 ? "(the check run reported no output)" : text.ToString().TrimEnd();
    }

    /// <summary>The last <paramref name="maxBytes"/> bytes of <paramref name="content"/> as UTF-8 text (a log can be very large).</summary>
    private static async Task<string> TailAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var kept = new MemoryStream();
        var buffer = new byte[81920];
        var trimmed = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            kept.Write(buffer, 0, read);
            if (kept.Length > 2L * maxBytes)
            {
                var tail = kept.ToArray()[^maxBytes..];
                kept = new MemoryStream();
                kept.Write(tail);
                trimmed = true;
            }
        }
        var bytes = kept.ToArray();
        if (bytes.Length > maxBytes)
        {
            (bytes, trimmed) = (bytes[^maxBytes..], true);
        }
        var text = Encoding.UTF8.GetString(bytes);
        return trimmed ? $"[earlier log omitted]\n{text}" : text;
    }

    public async Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct)
    {
        var range = $"{Uri.EscapeDataString(baseRef).Replace("%2F", "/", StringComparison.Ordinal)}...{Uri.EscapeDataString(headSha)}";
        using var request = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/compare/{range}?per_page=1", "Bearer",
            await ReadTokenAsync(repo, ct));
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"compare {headSha} with {baseRef}", ct);
        var compare = (await response.Content.ReadFromJsonAsync<CompareDto>(ct))!;
        return new BaseComparison(compare.BaseCommit.Sha, compare.BehindBy);
    }

    public async Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct)
    {
        var token = (await gateApp.CreateInstallationTokenAsync(repo, ct, MergePermissions)).Token;
        using var request = GitHubApp.Request(HttpMethod.Put, $"repos/{repo.Owner}/{repo.Name}/pulls/{number}/merge", "Bearer", token);
        request.Content = JsonContent.Create(new { sha = headSha, merge_method = "merge" });
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // "Head branch was modified. Review and try the merge again."
            return new MergeResult(false, null, true, await response.Content.ReadAsStringAsync(ct));
        }
        await GitHubApp.EnsureSuccess(response, $"merge pull request #{number}", ct);
        var merged = (await response.Content.ReadFromJsonAsync<MergeDto>(ct))!;
        return new MergeResult(merged.Merged, merged.Sha, false, merged.Message);
    }

    private async Task<string> ReadTokenAsync(RepoRef repo, CancellationToken ct) =>
        (await gateApp.CreateInstallationTokenAsync(repo, ct, ReadPermissions)).Token;

    private static readonly Regex PullUrl = new(@"^https://github\.com/(?<owner>[^/]+)/(?<name>[^/]+)/pull/(?<number>\d+)/?$");

    /// <summary>The repository and number of a PR's html URL.</summary>
    public static (RepoRef Repo, int Number) ParsePullUrl(string url) =>
        PullUrl.Match(url) is { Success: true } m
            ? (new RepoRef(m.Groups["owner"].Value, m.Groups["name"].Value), int.Parse(m.Groups["number"].Value))
            : throw new FormatException($"'{url}' is not a GitHub pull request URL.");

    private sealed record PullDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("merged")] bool Merged,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha,
        [property: JsonPropertyName("changed_files")] int? ChangedFiles,
        [property: JsonPropertyName("head")] RefDto Head,
        [property: JsonPropertyName("base")] RefDto Base);

    private sealed record RefDto(
        [property: JsonPropertyName("ref")] string Ref,
        [property: JsonPropertyName("sha")] string Sha);

    private sealed record CheckRunsDto(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("check_runs")] List<CheckRunDto> CheckRuns);

    private sealed record CheckRunDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("conclusion")] string? Conclusion,
        [property: JsonPropertyName("id")] long? Id = null,
        [property: JsonPropertyName("output")] CheckOutputDto? Output = null);

    private sealed record CheckOutputDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record AnnotationDto(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("start_line")] int? StartLine,
        [property: JsonPropertyName("annotation_level")] string? Level,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record CheckSuitesDto(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("check_suites")] List<CheckSuiteDto> CheckSuites);

    private sealed record CheckSuiteDto(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("conclusion")] string? Conclusion,
        [property: JsonPropertyName("latest_check_runs_count")] int LatestCheckRunsCount,
        [property: JsonPropertyName("app")] AppDto? App);

    private sealed record AppDto([property: JsonPropertyName("slug")] string? Slug);

    private sealed record CombinedStatusDto(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("statuses")] List<StatusDto> Statuses);

    private sealed record StatusDto(
        [property: JsonPropertyName("context")] string Context,
        [property: JsonPropertyName("state")] string State);

    private sealed record TreeDto(
        [property: JsonPropertyName("tree")] List<TreeEntryDto> Tree,
        [property: JsonPropertyName("truncated")] bool Truncated);

    private sealed record TreeEntryDto(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type);

    private sealed record CompareDto(
        [property: JsonPropertyName("base_commit")] CommitDto BaseCommit,
        [property: JsonPropertyName("behind_by")] int BehindBy);

    private sealed record CommitDto([property: JsonPropertyName("sha")] string Sha);

    private sealed record MergeDto(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("merged")] bool Merged,
        [property: JsonPropertyName("message")] string? Message);
}
