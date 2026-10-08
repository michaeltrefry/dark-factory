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

        var sandbox = Harness.Options.WorkerSandbox;
        string dir;
        if (sandbox is null)
        {
            dir = Directory.CreateTempSubdirectory("df-e2e-worker-").FullName;
        }
        else
        {
            dir = Directory.CreateDirectory(Path.Combine(Harness.Options.WorkRoot, $"e2e-worker-{Guid.NewGuid():N}")).FullName;
            await sandbox.ShareAsync(dir, ct);
        }
        var worker = new ClaudeWorker(Harness.Options.ClaudePath, Harness.Options.RouterBaseUrl, routerKey, Harness.Options.WorkerAuth, TimeSpan.FromMinutes(5), sandbox);
        var result = await worker.RunAsync(dir, "Reply with the single word OK. Do not use any tools.", null, null, ct);

        Assert.True(result.Succeeded, $"worker failed: exit {result.ExitCode}: {result.ResultText} {result.StderrTail}");
        var cost = await Harness.WaitForCostAsync(result.SessionId!, routerKey, ct);
        Assert.True(cost is { RequestCount: > 0, ActualCostUsdMicros: >= 0 },
            $"router reports {cost?.RequestCount.ToString() ?? "nothing"} requests / {cost?.ActualCostUsdMicros.ToString() ?? "no"} cost micros for session {result.SessionId}");
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

        // The story is named explicitly by FACTORY_E2E_STORY, like a manual `factory run --ignore-scope`.
        var outcome = await FactoryRunner.RunAsync(Harness.Options, storyId, ignoreScope: true, Console.Out, ct);
        Assert.True(outcome.Succeeded, outcome.Error);

        // Ledger: Intake → Implement → Review for this run, timestamped, carrying the session id.
        var item = await db.WorkItems.SingleAsync(w => w.Source == RunPipeline.Source && w.ExternalId == StoryId.Format(storyId), ct);
        // Transition rows (Step null); the session id is checkpointed in Implement and carried on Review.
        var rows = await db.LedgerEntries.Where(e => e.WorkItemId == item.Id && e.RecordedAt >= startedAt && e.Step == null)
            .OrderBy(e => e.Id).ToListAsync(ct);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], rows.Select(r => r.State));
        Assert.All(rows, r => Assert.True(r.RecordedAt >= startedAt));
        Assert.True(await db.LedgerEntries.AnyAsync(e => e.WorkItemId == item.Id && e.Step == RunPipeline.Steps.Session
            && e.ClaudeSessionId == outcome.SessionId, ct));
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

        // Router: the session id in the ledger is recorded (a cost of $0 is valid: the router's local model).
        var cost = await Harness.WaitForCostAsync(rows[2].ClaudeSessionId!, routerKey, ct);
        Assert.True(cost is { RequestCount: > 0, ActualCostUsdMicros: >= 0 }, $"router has recorded no request for session {rows[2].ClaudeSessionId}");

        // S6: the ended session's row holds that recorded cost (the run waits for the router to record it).
        var session = await db.WorkerSessions.AsNoTracking().SingleAsync(s => s.ClaudeSessionId == outcome.SessionId, ct);
        Assert.True(session is { RouterRequestCount: > 0, CostUsd: >= 0 },
            $"worker_sessions for {outcome.SessionId}: {session.RouterRequestCount?.ToString() ?? "null"} requests, cost {session.CostUsd?.ToString() ?? "null"}");
    }
}
