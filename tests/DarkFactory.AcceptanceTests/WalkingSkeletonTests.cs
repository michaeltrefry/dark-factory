using System.Net.Http.Json;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

[assembly: CaptureConsole]

namespace DarkFactory.AcceptanceTests;

/// <summary>Epic acceptance harness, v1: the sc-25172 walking skeleton, end to end.</summary>
public class WalkingSkeletonTests
{
    /// <summary>
    /// E1 + cost AC: a worker launched the way the factory launches it is billed by the
    /// router under the session id parsed from its stream-json output.
    /// </summary>
    [Fact]
    public async Task Worker_session_is_routed_and_costed_by_the_router()
    {
        Harness.RequireOptIn();
        var routerKey = Harness.RequireSecret(o => o.RouterKey);
        Harness.RequireClaude();
        await Harness.RequireRouterAsync();
        var ct = TestContext.Current.CancellationToken;

        var dir = Directory.CreateTempSubdirectory("df-e2e-worker-").FullName;
        var worker = new ClaudeWorker(Harness.Options.ClaudePath, Harness.Options.RouterBaseUrl, routerKey, Harness.Options.WorkerAuth, TimeSpan.FromMinutes(5));
        var result = await worker.RunAsync(dir, "Reply with the single word OK. Do not use any tools.", ct);

        Assert.True(result.Succeeded, $"worker failed: exit {result.ExitCode}: {result.ResultText} {result.StderrTail}");
        var cost = await Harness.WaitForCostAsync(result.SessionId!, routerKey, ct);
        Assert.NotNull(cost);
        Assert.True(cost!.ActualCostUsdMicros > 0, $"router cost for {result.SessionId} was {cost.ActualCostUsdMicros}");
    }

    /// <summary>
    /// The three story ACs: `factory run` on a To Do bug story targeting the sandbox opens a
    /// PR from factory/sc-&lt;id&gt; linking the story; the ledger holds Intake → Implement →
    /// Review with timestamps and the session id; the router reports non-zero cost for it.
    /// </summary>
    [Fact]
    public async Task Factory_run_opens_pr_records_ledger_and_is_costed()
    {
        Harness.RequireOptIn();
        var storyArg = Harness.RequireEnv("FACTORY_E2E_STORY",
            "a To Do bug story (sc-<id>) whose fix lands in the sandbox repo");
        Assert.True(StoryId.TryParse(storyArg, out var storyId), $"FACTORY_E2E_STORY='{storyArg}' is not a story id");
        Harness.RequireSecret(o => o.ShortcutApiToken);
        var routerKey = Harness.RequireSecret(o => o.RouterKey);
        var appId = Harness.RequireSecret(o => o.GitHubAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        Harness.RequireClaude();
        await Harness.RequireRouterAsync();
        await using var db = await Harness.RequireLedgerAsync();
        var ct = TestContext.Current.CancellationToken;
        var startedAt = DateTimeOffset.UtcNow;

        var outcome = await FactoryRunner.RunAsync(Harness.Options, storyId, Console.Out, ct);
        Assert.True(outcome.Succeeded, outcome.Error);

        // Ledger: Intake → Implement → Review for this run, timestamped, carrying the session id.
        var item = await db.WorkItems.SingleAsync(w => w.Source == RunPipeline.Source && w.ExternalId == StoryId.Format(storyId), ct);
        var rows = await db.LedgerEntries.Where(e => e.WorkItemId == item.Id && e.RecordedAt >= startedAt)
            .OrderBy(e => e.Id).ToListAsync(ct);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], rows.Select(r => r.State));
        Assert.All(rows, r => Assert.True(r.RecordedAt >= startedAt));
        Assert.Equal(outcome.SessionId, rows[1].ClaudeSessionId);
        Assert.Equal(outcome.SessionId, rows[2].ClaudeSessionId);

        // GitHub: an open PR from factory/sc-<id> whose body links the story.
        var repo = RepoRef.Parse(item.Repo);
        using var github = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        var token = (await new GitHubApp(github, appId, appKey, TimeProvider.System).CreateInstallationTokenAsync(repo, ct)).Token;
        using var request = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/pulls?state=open&head={Uri.EscapeDataString($"{repo.Owner}:{StoryId.BranchName(storyId)}")}",
            "Bearer", token);
        using var response = await github.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var pr = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        Assert.Equal(outcome.PullRequestUrl, pr.GetProperty("html_url").GetString());
        Assert.Contains($"/story/{storyId}", pr.GetProperty("body").GetString());

        // Router: non-zero cost recorded for the session id in the ledger.
        var cost = await Harness.WaitForCostAsync(rows[1].ClaudeSessionId!, routerKey, ct);
        Assert.True(cost is { ActualCostUsdMicros: > 0 }, $"no router cost for session {rows[1].ClaudeSessionId}");
    }
}
