using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    /// <summary>The text of <see cref="GatePolicy.Path"/> on <paramref name="baseRef"/>, or null when it does not exist.</summary>
    Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct);

    /// <summary>Every check run, commit status and check suite of <paramref name="sha"/>.</summary>
    Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct);

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
            pull.MergeCommitSha);
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

        var checks = runs.CheckRuns.Select(r => new CheckFact(r.Name, r.Status == "completed", r.Conclusion))
            .Concat(status.Statuses.Select(s => new CheckFact(s.Context, s.State != "pending", s.State is "error" ? "failure" : s.State)))
            .ToList();
        var complete = runs.TotalCount <= runs.CheckRuns.Count && status.TotalCount <= status.Statuses.Count
            && suites.TotalCount <= suites.CheckSuites.Count;
        return new CiFacts(sha, checks, complete,
            suites.CheckSuites.Select(s => new CheckSuiteFact(s.App?.Slug ?? "unknown", s.Status == "completed", s.Conclusion, s.LatestCheckRunsCount)).ToList());
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
        [property: JsonPropertyName("conclusion")] string? Conclusion);

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

    private sealed record MergeDto(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("merged")] bool Merged,
        [property: JsonPropertyName("message")] string? Message);
}
