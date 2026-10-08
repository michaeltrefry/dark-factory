using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.GitHub;

/// <summary>An issue as GitHub reports it. Its title and body are untrusted text (E4): they go to the triage worker only.</summary>
public sealed record IssueFacts(int Number, string Title, string? Body, string Author, bool AuthorIsBot, bool Open, IReadOnlyList<string> Labels,
    string HtmlUrl, DateTimeOffset UpdatedAt);

/// <summary>One comment on an issue. <see cref="AppId"/> is the GitHub App whose token wrote it, if any.</summary>
public sealed record IssueComment(long Id, string Author, bool AuthorIsBot, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    long? AppId)
{
    /// <summary>Whether the comment was changed after it was posted (GitHub moves <c>updated_at</c> on an edit).</summary>
    public bool Edited => UpdatedAt != CreatedAt;
}

/// <summary>
/// A user's access to a repo, from the collaborator permission API: <see cref="Permission"/> is <c>admin</c>, <c>write</c>,
/// <c>read</c> or <c>none</c> (GitHub folds <c>maintain</c> into write and <c>triage</c> into read); <see cref="Role"/> is the
/// role's own name.
/// </summary>
public sealed record RepoPermission(string Permission, string Role)
{
    public static readonly RepoPermission None = new("none", "none");

    /// <summary>
    /// Whether the user is a collaborator in the factory's sense: may push to the repo (write, maintain or admin). Read and triage
    /// may comment and label but not change code, so their issues and approvals count as an outsider's.
    /// </summary>
    public bool IsCollaborator => Permission is "admin" or "write";

    public override string ToString() => Role == Permission ? Permission : $"{Role} ({Permission})";
}

/// <summary>What the GitHub issue work source reads from and writes to GitHub (polling only; the Mac exposes no webhook).</summary>
public interface IGitHubIssues
{
    /// <summary>The id of the App whose tokens write the factory's comments (<see cref="IssueComment.AppId"/>).</summary>
    long AppId { get; }

    /// <summary>Open issues (never pull requests) of <paramref name="repo"/> updated at or after <paramref name="since"/>, least recently updated first.</summary>
    Task<IReadOnlyList<IssueFacts>> ListUpdatedAsync(RepoRef repo, DateTimeOffset? since, CancellationToken ct);

    Task<IssueFacts> GetAsync(RepoRef repo, int number, CancellationToken ct);

    /// <summary>Every comment of the issue, oldest first.</summary>
    Task<IReadOnlyList<IssueComment>> ListCommentsAsync(RepoRef repo, int number, CancellationToken ct);

    /// <summary>The user's permission on the repo; <see cref="RepoPermission.None"/> when GitHub knows no access for them.</summary>
    Task<RepoPermission> PermissionAsync(RepoRef repo, string login, CancellationToken ct);

    /// <summary>The text of <paramref name="path"/> on the repo's default branch, or null when it does not exist.</summary>
    Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct);

    /// <summary>Posts a comment; returns its id.</summary>
    Task<long> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct);

    Task AddLabelsAsync(RepoRef repo, int number, IReadOnlyList<string> labels, CancellationToken ct);

    /// <summary>Removes a label; one the issue does not carry is not an error.</summary>
    Task RemoveLabelAsync(RepoRef repo, int number, string label, CancellationToken ct);

    /// <summary>Closes the issue as completed.</summary>
    Task CloseAsync(RepoRef repo, int number, CancellationToken ct);
}

/// <summary>
/// <see cref="IGitHubIssues"/> as the factory's (workers') GitHub App, never the gate App. Every call mints a fresh token for one
/// repo with only what that call needs: reads <c>issues: read</c> (the permission API <c>metadata: read</c>, a file
/// <c>contents: read</c>), and the writes — comments, labels, closing — <c>issues: write</c> and nothing else, so no token this
/// class holds can push or merge (E4). The App needs the Issues read and write permission (<c>app-manifest.json</c>).
/// </summary>
public sealed class GitHubIssuesClient(HttpClient http, GitHubApp app) : IGitHubIssues
{
    public static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string> { ["issues"] = "read" };

    public static readonly IReadOnlyDictionary<string, string> WritePermissions = new Dictionary<string, string> { ["issues"] = "write" };

    public static readonly IReadOnlyDictionary<string, string> MetadataPermissions = new Dictionary<string, string> { ["metadata"] = "read" };

    public static readonly IReadOnlyDictionary<string, string> ContentsReadPermissions = new Dictionary<string, string> { ["contents"] = "read" };

    private const int PageSize = 100;

    public long AppId => long.Parse(app.AppId, System.Globalization.CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<IssueFacts>> ListUpdatedAsync(RepoRef repo, DateTimeOffset? since, CancellationToken ct)
    {
        var token = await TokenAsync(repo, ReadPermissions, ct);
        var issues = new List<IssueFacts>();
        var filter = since is { } s ? $"&since={Uri.EscapeDataString(s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))}" : "";
        for (var page = 1; ; page++)
        {
            var batch = await GetJsonAsync<List<IssueDto>>(
                $"repos/{repo.Owner}/{repo.Name}/issues?state=open&sort=updated&direction=asc&per_page={PageSize}&page={page}{filter}", token,
                $"list the issues of {repo}", ct);
            issues.AddRange(batch.Where(i => i.PullRequest is null).Select(Facts));
            if (batch.Count < PageSize)
            {
                return issues;
            }
        }
    }

    public async Task<IssueFacts> GetAsync(RepoRef repo, int number, CancellationToken ct) =>
        Facts(await GetJsonAsync<IssueDto>($"repos/{repo.Owner}/{repo.Name}/issues/{number}", await TokenAsync(repo, ReadPermissions, ct),
            $"read issue {repo}#{number}", ct));

    public async Task<IReadOnlyList<IssueComment>> ListCommentsAsync(RepoRef repo, int number, CancellationToken ct)
    {
        var token = await TokenAsync(repo, ReadPermissions, ct);
        var comments = new List<IssueComment>();
        for (var page = 1; ; page++)
        {
            var batch = await GetJsonAsync<List<CommentDto>>($"repos/{repo.Owner}/{repo.Name}/issues/{number}/comments?per_page={PageSize}&page={page}",
                token, $"list the comments of {repo}#{number}", ct);
            comments.AddRange(batch.Select(c => new IssueComment(c.Id, c.User?.Login ?? "", c.User?.Type == "Bot", c.Body ?? "", c.CreatedAt, c.UpdatedAt,
                c.App?.Id)));
            if (batch.Count < PageSize)
            {
                return comments;
            }
        }
    }

    public async Task<RepoPermission> PermissionAsync(RepoRef repo, string login, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/collaborators/{Uri.EscapeDataString(login)}/permission", "Bearer", await TokenAsync(repo, MetadataPermissions, ct));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return RepoPermission.None;
        }
        await GitHubApp.EnsureSuccess(response, $"read {login}'s permission on {repo}", ct);
        var dto = (await response.Content.ReadFromJsonAsync<PermissionDto>(ct))!;
        var permission = dto.Permission ?? "none";
        return new RepoPermission(permission, dto.RoleName ?? permission);
    }

    public async Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/contents/{path}", "Bearer",
            await TokenAsync(repo, ContentsReadPermissions, ct));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await GitHubApp.EnsureSuccess(response, $"read {path} of {repo}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<long> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Post, $"repos/{repo.Owner}/{repo.Name}/issues/{number}/comments", "Bearer",
            await TokenAsync(repo, WritePermissions, ct));
        request.Content = JsonContent.Create(new { body });
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"comment on {repo}#{number}", ct);
        return (await response.Content.ReadFromJsonAsync<CommentDto>(ct))!.Id;
    }

    public async Task AddLabelsAsync(RepoRef repo, int number, IReadOnlyList<string> labels, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Post, $"repos/{repo.Owner}/{repo.Name}/issues/{number}/labels", "Bearer",
            await TokenAsync(repo, WritePermissions, ct));
        request.Content = JsonContent.Create(new { labels });
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"label {repo}#{number}", ct);
    }

    public async Task RemoveLabelAsync(RepoRef repo, int number, string label, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Delete, $"repos/{repo.Owner}/{repo.Name}/issues/{number}/labels/{Uri.EscapeDataString(label)}",
            "Bearer", await TokenAsync(repo, WritePermissions, ct));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        await GitHubApp.EnsureSuccess(response, $"remove label {label} from {repo}#{number}", ct);
    }

    public async Task CloseAsync(RepoRef repo, int number, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Patch, $"repos/{repo.Owner}/{repo.Name}/issues/{number}", "Bearer",
            await TokenAsync(repo, WritePermissions, ct));
        request.Content = JsonContent.Create(new { state = "closed", state_reason = "completed" });
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, $"close {repo}#{number}", ct);
    }

    private async Task<string> TokenAsync(RepoRef repo, IReadOnlyDictionary<string, string> permissions, CancellationToken ct) =>
        (await app.CreateInstallationTokenAsync(repo, ct, permissions)).Token;

    private async Task<T> GetJsonAsync<T>(string path, string token, string what, CancellationToken ct)
    {
        using var request = GitHubApp.Request(HttpMethod.Get, path, "Bearer", token);
        using var response = await http.SendAsync(request, ct);
        await GitHubApp.EnsureSuccess(response, what, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))
            ?? throw new InvalidOperationException($"GitHub {what} returned an empty body.");
    }

    private static IssueFacts Facts(IssueDto i) =>
        new(i.Number, i.Title ?? "", i.Body, i.User?.Login ?? "", i.User?.Type == "Bot", i.State == "open",
            i.Labels?.Select(l => l.Name).ToList() ?? [], i.HtmlUrl, i.UpdatedAt);

    private sealed record UserDto([property: JsonPropertyName("login")] string Login, [property: JsonPropertyName("type")] string? Type);

    private sealed record LabelDto([property: JsonPropertyName("name")] string Name);

    private sealed record AppDto([property: JsonPropertyName("id")] long Id);

    private sealed record IssueDto(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("labels")] List<LabelDto>? Labels,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);

    private sealed record CommentDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("user")] UserDto? User,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("performed_via_github_app")] AppDto? App);

    private sealed record PermissionDto(
        [property: JsonPropertyName("permission")] string? Permission,
        [property: JsonPropertyName("role_name")] string? RoleName);
}
