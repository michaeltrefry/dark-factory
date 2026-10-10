using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.GitHub;

public interface IPullRequests
{
    /// <summary>Opens a PR from <paramref name="head"/> into <paramref name="baseBranch"/>, or returns the already-open one.</summary>
    Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct);

    /// <summary>
    /// Turns every open, ready-for-review PR from <paramref name="head"/> back into a draft (a stopped item).
    /// Nothing is closed, merged or deleted. Returns the URLs of the head's open PRs, drafts already or not.
    /// </summary>
    Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct);

    /// <summary>Replaces the description of the PR at <paramref name="pullUrl"/> (the factory's ledger report, as the item moves on).</summary>
    Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct);
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
            if ((await ListOpenAsync(repo, head, token, ct)).FirstOrDefault() is { } existing)
            {
                return existing.HtmlUrl;
            }
        }
        await GitHubApp.EnsureSuccess(response, "create pull request", ct);
        return (await response.Content.ReadFromJsonAsync<PullDto>(ct))!.HtmlUrl;
    }

    public async Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct)
    {
        var (pullRepo, number) = GitHubGate.ParsePullUrl(pullUrl);
        if (!string.Equals(pullRepo.FullName, repo.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{pullUrl} is not a pull request of {repo}.");
        }
        var token = (await app.CreateInstallationTokenAsync(repo, ct)).Token;
        using var update = GitHubApp.Request(HttpMethod.Patch, $"repos/{repo.Owner}/{repo.Name}/pulls/{number}", "Bearer", token);
        update.Content = JsonContent.Create(new { body });
        using var response = await http.SendAsync(update, ct);
        await GitHubApp.EnsureSuccess(response, "update pull request description", ct);
    }

    public async Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct)
    {
        var token = (await app.CreateInstallationTokenAsync(repo, ct)).Token;
        var pulls = await ListOpenAsync(repo, head, token, ct);
        foreach (var pull in pulls.Where(p => !p.Draft))
        {
            // REST has no way back to draft; GraphQL's convertPullRequestToDraft needs pull_requests:write (the App token's).
            using var mutation = GitHubApp.Request(HttpMethod.Post, "graphql", "Bearer", token);
            mutation.Content = JsonContent.Create(new
            {
                query = "mutation($id: ID!) { convertPullRequestToDraft(input: {pullRequestId: $id}) { pullRequest { isDraft } } }",
                variables = new { id = pull.NodeId },
            });
            using var response = await http.SendAsync(mutation, ct);
            await GitHubApp.EnsureSuccess(response, "convert pull request to draft", ct);
            // GraphQL reports failures in a 200 body.
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (result.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                throw new InvalidOperationException($"GitHub convert pull request to draft failed for {pull.HtmlUrl}: {errors}");
            }
        }
        return pulls.Select(p => p.HtmlUrl).ToList();
    }

    private async Task<PullDto[]> ListOpenAsync(RepoRef repo, string head, string token, CancellationToken ct)
    {
        using var list = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/pulls?state=open&head={Uri.EscapeDataString($"{repo.Owner}:{head}")}", "Bearer", token);
        using var response = await http.SendAsync(list, ct);
        await GitHubApp.EnsureSuccess(response, "list pull requests", ct);
        return await response.Content.ReadFromJsonAsync<PullDto[]>(ct) ?? [];
    }

    private sealed record PullDto(
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("node_id")] string? NodeId = null,
        [property: JsonPropertyName("draft")] bool Draft = false);
}
