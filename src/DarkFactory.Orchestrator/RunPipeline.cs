using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator;

public sealed record RunOutcome(long WorkItemId, string? SessionId, string? PullRequestUrl, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// <c>factory run</c>: Intake → Implement → Review for one Shortcut story. Fixed,
/// deterministic sequence (E2); each state is a committed ledger row before the
/// next step starts (E3).
/// </summary>
public sealed class RunPipeline(
    IStorySource stories,
    WorkLedger ledger,
    IRepoWorkspace workspaces,
    IWorker worker,
    IPullRequests pullRequests,
    RepoRef defaultRepo,
    TextWriter log)
{
    public const string Source = "shortcut";

    public async Task<RunOutcome> RunAsync(int storyId, CancellationToken ct)
    {
        var story = await stories.GetStoryAsync(storyId, ct);
        var repo = RepoResolver.Resolve(story.Description, defaultRepo);
        var item = await ledger.GetOrCreateAsync(Source, StoryId.Format(storyId), story.Name, repo.FullName, ct);
        await ledger.RecordAsync(item, WorkState.Intake, null, $"{story.StoryType}: {story.AppUrl}", ct);
        log.WriteLine($"[intake] {StoryId.Format(storyId)} \"{story.Name}\" -> {repo}");

        string? sessionId = null;
        Workspace? workspace = null;
        try
        {
            workspace = await workspaces.PrepareAsync(repo, StoryId.BranchName(storyId), ct);
            log.WriteLine($"[implement] worktree {workspace.Path} on {workspace.Branch}");

            var result = await worker.RunAsync(workspace.Path, BuildPrompt(story, repo), ct);
            sessionId = result.SessionId;
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Worker failed (exit {result.ExitCode}, result {result.ResultSubtype ?? "none"}): {result.ResultText} {result.StderrTail}".Trim());
            }
            await ledger.RecordAsync(item, WorkState.Implement, sessionId, $"worker exit {result.ExitCode}", ct);
            log.WriteLine($"[implement] claude session {sessionId}");

            if (!await workspaces.CommitAndPushAsync(repo, workspace, $"{StoryId.Format(storyId)}: {story.Name}", ct))
            {
                throw new InvalidOperationException("Worker finished without changing the repository; nothing to review.");
            }
            var prUrl = await pullRequests.OpenAsync(repo, workspace.Branch, workspace.BaseBranch,
                $"{StoryId.Format(storyId)}: {story.Name}", BuildPrBody(story, sessionId!), ct);
            await ledger.RecordAsync(item, WorkState.Review, sessionId, prUrl, ct);
            log.WriteLine($"[review] {prUrl}");
            return new RunOutcome(item.Id, sessionId, prUrl, null);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C: still leave a terminal row and no worktree behind, then let the cancellation propagate.
            await ledger.RecordAsync(item, WorkState.Failed, sessionId, "cancelled", CancellationToken.None);
            log.WriteLine("[failed] cancelled");
            if (workspace is not null)
            {
                try
                {
                    await workspaces.RemoveAsync(repo, workspace, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    log.WriteLine($"[failed] could not remove worktree {workspace.Path}: {cleanup.Message}");
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            await ledger.RecordAsync(item, WorkState.Failed, sessionId, ex.Message, CancellationToken.None);
            log.WriteLine($"[failed] {ex.Message}");
            return new RunOutcome(item.Id, sessionId, null, ex.Message);
        }
    }

    public static string BuildPrompt(ShortcutStory story, RepoRef repo) =>
        $"""
        You are a Dark Factory worker. The current directory is a git worktree of {repo}.
        Implement Shortcut story {StoryId.Format(story.Id)} ({story.StoryType}): {story.Name}

        Story description:
        {story.Description}

        Make the smallest change that satisfies the story, including a test when the
        project has tests, and make sure `dotnet build` and `dotnet test` pass.
        Do not commit, push, or open pull requests; the orchestrator does that.
        """;

    public static string BuildPrBody(ShortcutStory story, string sessionId) =>
        $"""
        Implements Shortcut story [{StoryId.Format(story.Id)}]({story.AppUrl}): {story.Name}

        Opened by Dark Factory. Claude session: `{sessionId}`
        """;
}
