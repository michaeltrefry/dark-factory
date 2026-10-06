using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.GitHub;

public interface IPullRequests
{
    /// <summary>Opens a PR from <paramref name="head"/> into <paramref name="baseBranch"/>, or returns the already-open one.</summary>
    Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct);
}

/// <summary>Opens pull requests with a repo-scoped installation token.</summary>
public sealed class GitHubPullRequests(HttpClient http, GitHubApp app) : IPullRequests
{
    public async Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
    {
        var token = (await app.CreateInstallationTokenAsync(repo, ct)).Token;

        using var create = GitHubApp.Request(HttpMethod.Post, $"repos/{repo.Owner}/{repo.Name}/pulls", "Bearer", token);
        create.Content = JsonContent.Create(new { title, head, @base = baseBranch, body });
        using var response = await http.SendAsync(create, ct);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // A re-run of the same story: the branch was force-pushed and its PR is still open.
            var existing = await FindOpenAsync(repo, head, token, ct);
            if (existing is not null)
            {
                return existing;
            }
        }
        await GitHubApp.EnsureSuccess(response, "create pull request", ct);
        return (await response.Content.ReadFromJsonAsync<PullDto>(ct))!.HtmlUrl;
    }

    private async Task<string?> FindOpenAsync(RepoRef repo, string head, string token, CancellationToken ct)
    {
        using var list = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/pulls?state=open&head={Uri.EscapeDataString($"{repo.Owner}:{head}")}", "Bearer", token);
        using var response = await http.SendAsync(list, ct);
        await GitHubApp.EnsureSuccess(response, "list pull requests", ct);
        var pulls = await response.Content.ReadFromJsonAsync<PullDto[]>(ct);
        return pulls?.FirstOrDefault()?.HtmlUrl;
    }

    private sealed record PullDto([property: JsonPropertyName("html_url")] string HtmlUrl);
}
