using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// sc-25385 against GitHub and real models (docs/acceptance.md P2-AT-issues). Live: skipped unless FACTORY_E2E=1 and
/// FACTORY_E2E_ISSUES=1. It opens real issues on the sandbox repo (<c>Factory:DefaultRepo</c>) as the owner (GH_TOKEN) and, for
/// the outsider case, as a second account without write access (FACTORY_E2E_OUTSIDER_TOKEN); the factory's App needs the Issues
/// read and write permission and must be installed there. Every issue it opens is closed at the end.
/// </summary>
public class IssueTriageTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(60);

    private const string ConfidentBug = """
        [dark-factory fixture] `WordCounter.Count("  ")` returns 1; text that is only whitespace has no words, so it must return 0.
        The fix is in the word counter's split; add a test for whitespace-only input.
        """;

    private static (E2e E2e, FactoryOptions Options, RepoRef Repo) Options(E2e e2e)
    {
        var repo = Harness.Options.DefaultRepo;
        return (e2e, e2e.Options(new Dictionary<string, string?> { ["GitHub:Watch:Repos"] = repo.FullName }), repo);
    }

    private static async Task<E2e> StartAsync(string prefix)
    {
        Harness.RequireOptIn();
        Harness.RequireEnv("FACTORY_E2E_ISSUES", "set to 1 to open, triage and build real issues on the sandbox repo");
        Harness.RequireEnv("GH_TOKEN", "the owner's GitHub token: it opens and approves the test issues as a collaborator");
        return await E2e.StartAsync(prefix);
    }

    /// <summary>A collaborator's issue with a confident fix becomes a work item, and its merged PR closes the issue.</summary>
    [Fact]
    public async Task A_collaborators_issue_with_a_confident_fix_is_built_and_its_merged_pr_closes_the_issue()
    {
        await using var e2e = await StartAsync("df_e2e_issue_build");
        var (_, options, repo) = Options(e2e);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var status = new IntakeStatus(TimeProvider.System);
        await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct); // starts watching from now
        using var owner = UserClient(Environment.GetEnvironmentVariable("GH_TOKEN")!);
        var number = await OpenIssueAsync(owner, repo, "[dark-factory fixture] whitespace-only text counts as one word", ConfidentBug, ct);
        try
        {
            await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct);

            var key = await KeyAsync(e2e, repo, number, ct);
            var history = await HistoryAsync(e2e, key, ct);
            var triage = IssueIntake.Latest(history);
            Assert.True(triage is { Route: IssueRoute.Build }, $"the triage routed it {triage?.Route}: {triage?.Why}");
            Assert.EndsWith(" auto", history.Single(e => e.Step == IssueSteps.Released).Detail);

            var outcome = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.GitHubIssue, key), ignoreScope: false, Console.Out, ct);
            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal(WorkState.Watch, outcome.State);
            var pr = await GetAsync(owner, $"repos/{repo}/pulls/{outcome.PullRequestUrl!.Split('/')[^1]}", ct);
            Assert.Contains($"Closes {repo}#{number}", pr.GetProperty("body").GetString());
            Assert.True(pr.GetProperty("merged").GetBoolean());
            var issue = await GetAsync(owner, $"repos/{repo}/issues/{number}", ct);
            Assert.Equal("closed", issue.GetProperty("state").GetString());
        }
        finally
        {
            await CloseAsync(owner, repo, number);
        }
    }

    /// <summary>An outsider's issue gets the triage comment and awaiting-approval; the outsider's Approved is ignored, the owner's releases it.</summary>
    [Fact]
    public async Task An_outsiders_issue_waits_for_a_collaborators_approval()
    {
        await using var e2e = await StartAsync("df_e2e_issue_outsider");
        var outsiderToken = Harness.RequireEnv("FACTORY_E2E_OUTSIDER_TOKEN", "a GitHub token of an account with no write access to the sandbox repo");
        var (_, options, repo) = Options(e2e);
        var ct = TestContext.Current.CancellationToken;
        var status = new IntakeStatus(TimeProvider.System);
        await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct);
        using var owner = UserClient(Environment.GetEnvironmentVariable("GH_TOKEN")!);
        using var outsider = UserClient(outsiderToken);
        var number = await OpenIssueAsync(outsider, repo, "[dark-factory fixture] whitespace-only text counts as one word", ConfidentBug, ct);
        try
        {
            await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct);
            var key = await KeyAsync(e2e, repo, number, ct);
            Assert.Equal(IssueRoute.AwaitingApproval, IssueIntake.Latest(await HistoryAsync(e2e, key, ct))!.Route);
            var issue = await GetAsync(owner, $"repos/{repo}/issues/{number}", ct);
            Assert.Contains(issue.GetProperty("labels").EnumerateArray(), l => l.GetProperty("name").GetString() == IssueLabels.AwaitingApproval);

            await CommentAsync(outsider, repo, number, "Approved", ct);
            await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct);
            Assert.DoesNotContain(await HistoryAsync(e2e, key, ct), e => e.Step == IssueSteps.Released);
            Assert.Contains(await HistoryAsync(e2e, key, ct), e => e.Step == IssueSteps.ApprovalIgnored);

            await CommentAsync(owner, repo, number, "Approved", ct);
            await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct);
            using var github = new HttpClient { BaseAddress = Orchestrator.GitHub.GitHubApp.DefaultBaseAddress };
            Assert.Contains(key, await FactoryRunner.CreateIssueSource(options, github).ListReadyAsync(ct));
        }
        finally
        {
            await CloseAsync(owner, repo, number);
        }
    }

    private static HttpClient UserClient(string token)
    {
        var http = new HttpClient { BaseAddress = Orchestrator.GitHub.GitHubApp.DefaultBaseAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dark-factory-e2e", "0.1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static async Task<int> OpenIssueAsync(HttpClient user, RepoRef repo, string title, string body, CancellationToken ct)
    {
        using var response = await user.PostAsJsonAsync($"repos/{repo}/issues", new { title, body }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("number").GetInt32();
    }

    private static async Task CommentAsync(HttpClient user, RepoRef repo, int number, string body, CancellationToken ct)
    {
        using var response = await user.PostAsJsonAsync($"repos/{repo}/issues/{number}/comments", new { body }, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> GetAsync(HttpClient user, string path, CancellationToken ct)
    {
        using var response = await user.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task CloseAsync(HttpClient owner, RepoRef repo, int number)
    {
        using var response = await owner.PatchAsJsonAsync($"repos/{repo}/issues/{number}", new { state = "closed", state_reason = "not_planned" });
    }

    private static async Task<int> KeyAsync(E2e e2e, RepoRef repo, int number, CancellationToken ct)
    {
        await using var db = e2e.Db();
        return (await db.GitHubIssues.AsNoTracking().SingleAsync(i => i.Repo == repo.FullName && i.Number == number, ct)).Id;
    }

    private static async Task<List<LedgerEntry>> HistoryAsync(E2e e2e, int key, CancellationToken ct)
    {
        await using var db = e2e.Db();
        var externalId = ItemNaming.GitHubIssue.Format(key);
        var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Source == "github" && i.ExternalId == externalId, ct);
        return await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
    }
}
