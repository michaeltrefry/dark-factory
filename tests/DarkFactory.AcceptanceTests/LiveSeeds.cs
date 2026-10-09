using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Writes the live acceptance tests seed on the sandbox repo (sc-25391), through the gateway's GitHub client: files on an item's
/// <c>factory/*</c> branch and closing its PR as the workers' App (which may write only there), and a throwaway non-factory branch
/// as the owner (<c>GH_TOKEN</c>, an admin). Never the sandbox's main.
/// </summary>
internal sealed class SandboxRepo(FactoryOptions options, RepoRef repo) : IDisposable
{
    private readonly HttpClient _github = OutboundHttp.GitHubApi();

    public RepoRef Repo => repo;

    public void Dispose() => _github.Dispose();

    /// <summary>A fresh installation token of the workers' App on the sandbox.</summary>
    public async Task<string> AppTokenAsync(CancellationToken ct) =>
        (await new GitHubApp(_github, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System).CreateInstallationTokenAsync(repo, ct)).Token;

    /// <summary>The owner's token (an admin of the sandbox), for a branch outside <c>factory/**</c>.</summary>
    public static string OwnerToken() => Harness.RequireEnv("GH_TOKEN", "the owner's GitHub token (an admin of the sandbox): it creates and deletes the throwaway branch");

    private async Task<JsonElement?> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = GitHubApp.Request(method, $"repos/{repo.Owner}/{repo.Name}/{path}", "Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await _github.SendAsync(request, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default(JsonElement) : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The commit <paramref name="branch"/> points at.</summary>
    public async Task<string> BranchShaAsync(string token, string branch, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, $"git/ref/heads/{branch}", token, null, ct))!.Value.GetProperty("object").GetProperty("sha").GetString()!;

    /// <summary>Creates or updates <paramref name="path"/> on <paramref name="branch"/> in one commit; returns the commit.</summary>
    public async Task<string> PutFileAsync(string token, string branch, string path, string content, string message, CancellationToken ct)
    {
        var existing = await SendAsync(HttpMethod.Get, $"contents/{path}?ref={Uri.EscapeDataString(branch)}", token, null, ct, allowNotFound: true);
        var body = new Dictionary<string, string>
        {
            ["message"] = message,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = branch,
        };
        if (existing is { } file)
        {
            body["sha"] = file.GetProperty("sha").GetString()!;
        }
        return (await SendAsync(HttpMethod.Put, $"contents/{path}", token, body, ct))!.Value.GetProperty("commit").GetProperty("sha").GetString()!;
    }

    /// <summary>Creates <paramref name="branch"/> at <paramref name="sha"/>.</summary>
    public Task CreateBranchAsync(string token, string branch, string sha, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "git/refs", token, new { @ref = $"refs/heads/{branch}", sha }, ct);

    /// <summary>Deletes <paramref name="branch"/> if it exists (best effort: cleanup never fails a test).</summary>
    public async Task DeleteBranchAsync(string token, string branch)
    {
        try
        {
            using var request = GitHubApp.Request(HttpMethod.Delete, $"repos/{repo.Owner}/{repo.Name}/git/refs/heads/{branch}", "Bearer", token);
            using var response = await _github.SendAsync(request, CancellationToken.None);
            Console.WriteLine($"[cleanup] delete {branch}: {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[cleanup] could not delete {branch}: {ex.Message}");
        }
    }

    /// <summary>
    /// The directory of the first xUnit test project (<c>*Tests.csproj</c>) in <paramref name="branch"/>'s tree, where a seeded test
    /// file builds and runs.
    /// </summary>
    public async Task<string> TestProjectDirAsync(string token, string branch, CancellationToken ct)
    {
        var tree = (await SendAsync(HttpMethod.Get, $"git/trees/{Uri.EscapeDataString(branch)}?recursive=1", token, null, ct))!.Value;
        var project = tree.GetProperty("tree").EnumerateArray().Select(e => e.GetProperty("path").GetString()!)
            .Where(p => p.EndsWith("Tests.csproj", StringComparison.Ordinal)).Order(StringComparer.Ordinal).FirstOrDefault();
        Assert.True(project is not null, $"{repo} has no *Tests.csproj on {branch} to seed a test into");
        return Path.GetDirectoryName(project)!.Replace('\\', '/');
    }

    /// <summary>
    /// Closes every open PR from the item's <c>factory/sc-&lt;id&gt;</c> branch and deletes the branch, as the workers' App (best
    /// effort): what a gate-negative test leaves behind on the sandbox.
    /// </summary>
    public async Task CloseItemAsync(int storyId)
    {
        try
        {
            var token = await AppTokenAsync(CancellationToken.None);
            foreach (var pr in (await E2e.PullRequestsAsync(repo, storyId, CancellationToken.None)).Where(p => p.GetProperty("state").GetString() == "open"))
            {
                await SendAsync(HttpMethod.Patch, $"pulls/{pr.GetProperty("number").GetInt32()}", token, new { state = "closed" }, CancellationToken.None);
                Console.WriteLine($"[cleanup] closed {pr.GetProperty("html_url").GetString()}");
            }
            await DeleteBranchAsync(token, StoryId.BranchName(storyId));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Xunit.Sdk.XunitException)
        {
            Console.WriteLine($"[cleanup] could not close sc-{storyId}'s PR and branch: {ex.Message}");
        }
    }
}

/// <summary>
/// A stand-in for the review panel in the gate-negative acceptance tests (sc-25391): it never calls the router; it answers every role
/// as the pinned model with no finding — or, <paramref name="blocking"/>, the correctness role with one blocking finding no fix
/// clears, which the second model confirms — so what decides each test is the gate's own rule, deterministically. Before its first
/// review it runs <paramref name="beforeFirst"/> (e.g. a seeding push to the PR branch, which moves the head the gate judges).
/// The real panel is AT2's (<see cref="ReviewGateTests"/>).
/// </summary>
internal sealed class SeededReviewer(bool blocking = false, Func<ReviewRequest, CancellationToken, Task>? beforeFirst = null) : IReviewer
{
    public const string FindingTitle = "dark-factory e2e seeded finding that no fix clears";

    private int _reviews;
    private readonly SemaphoreSlim _first = new(1, 1);
    private bool _seeded;

    /// <summary>How many role reviews it gave.</summary>
    public int Reviews => Volatile.Read(ref _reviews);

    public async Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        if (beforeFirst is not null)
        {
            await _first.WaitAsync(ct);
            try
            {
                if (!_seeded)
                {
                    await beforeFirst(request, ct);
                    _seeded = true;
                }
            }
            finally
            {
                _first.Release();
            }
        }
        Interlocked.Increment(ref _reviews);
        IReadOnlyList<Finding> findings = blocking && request.Role == ReviewRoles.Correctness
            ? [new Finding(Finding.Blocking, FindingTitle, null, null,
                "Seeded by the acceptance test so that every review round fails. Make one small, safe improvement and push it.")]
            : [];
        return new RoleReview(request.Role, request.Model, request.Model, request.Session, request.Prompt.Id, findings, "seeded review (acceptance test)");
    }

    public Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct) =>
        Task.FromResult(new Confirmation(Confirmation.Confirmed, request.Model, request.Model, request.Session, request.Prompt.Id,
            "seeded confirmation (acceptance test)"));
}

/// <summary>
/// The gate's GitHub reads, except that once <paramref name="redirect"/> holds the policy is read from <paramref name="policyRef"/>
/// (a throwaway branch with a corrupted <c>factory/gate.yaml</c>) instead of the PR's base commit: the real GitHub read of a really
/// corrupted file, without touching the sandbox's main. Every other call goes to <paramref name="inner"/>.
/// </summary>
internal sealed class PolicyFromRef(IGateGitHub inner, string policyRef, Func<bool> redirect) : IGateGitHub
{
    private int _redirected;

    /// <summary>How many policy reads went to <paramref name="policyRef"/>.</summary>
    public int Redirected => Volatile.Read(ref _redirected);

    public Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct)
    {
        if (!redirect())
        {
            return inner.GetPolicyAsync(repo, baseRef, ct);
        }
        Interlocked.Increment(ref _redirected);
        return inner.GetPolicyAsync(repo, policyRef, ct);
    }

    public Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct) => inner.GetPullAsync(repo, number, ct);

    public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) => inner.GetDiffAsync(repo, baseSha, headSha, ct);

    public Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct) => inner.GetFilesAsync(repo, sha, ct);

    public Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct) => inner.GetCiAsync(repo, sha, ct);

    public Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct) => inner.GetCheckLogAsync(repo, check, ct);

    public Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct) => inner.CompareAsync(repo, baseRef, headSha, ct);

    public Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct) => inner.MergeAsync(repo, number, headSha, ct);
}
