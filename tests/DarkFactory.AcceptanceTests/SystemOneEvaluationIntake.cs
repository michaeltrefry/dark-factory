using System.Diagnostics;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

public static class EvaluationIntake
{
    public static async Task<EvaluationObservation> RunAsync(EvaluationInput input, string mode, string repositorySha,
        string policy, IDbContextFactory<LedgerDbContext> contexts, IRunLocks locks, IControls controls,
        ITriageRunner triage, Func<IssueClassifier?> classifier, CancellationToken ct,
        string? classifierSessionId = null, ISessionCostSource? classifierCosts = null)
    {
        var repo = RepoRef.Parse(input.PermissionContext.IssueRepository);
        var watched = input.PermissionContext.WatchedRepositories.Select(RepoRef.Parse).ToList();
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            // Fresh ledger per case/arm; no watching from "now" race and no second poll/retry that spends more inference.
            db.GitHubIssueCursors.Add(new GitHubIssueCursor { Repo = repo.FullName, Since = DateTimeOffset.UnixEpoch });
            await db.SaveChangesAsync(ct);
        }
        var board = new EvaluationBoard(input, policy);
        var source = new GitHubIssueWorkSource(board, contexts, watched);
        var capture = new PromptCapture(triage);
        var started = Stopwatch.GetTimestamp();
        string? failure = null;
        try
        {
            // The caller supplies no classifier in the disabled arm: not even client construction is needed there.
            var intake = new IssueIntake(source, watched, contexts, locks, controls, capture,
                new IntakeStatus(TimeProvider.System), 1, TimeProvider.System, TextWriter.Null, classifier());
            await intake.PollAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { failure = "intake_exception"; } // Never export exception text (it can quote a prompt or answer).
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await using var resultDb = await contexts.CreateDbContextAsync(ct);
        var rows = await resultDb.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct);
        var record = IssueIntake.Latest(rows);
        var classification = await resultDb.IssueClassifications.AsNoTracking().SingleOrDefaultAsync(ct);
        var sessions = await resultDb.WorkerSessions.AsNoTracking().ToListAsync(ct);
        var events = await resultDb.SessionEvents.AsNoTracking().OrderBy(e => e.Sequence).Select(e => e.Payload).ToListAsync(ct);
        var models = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in events)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                { models.Add(EvaluationCorpus.Hash(model.GetString()!)); }
            }
            catch (JsonException) { }
        }
        var failed = failure is not null || record is null || record.Error is not null || sessions.Any(s => s.ExitStatus != "succeeded");
        var classifierCost = mode == "advisory" && classification?.Status != "skipped_oversize" && classifierCosts is not null && classifierSessionId is not null
            ? await ReadClassifierCostAsync(classifierCosts, classifierSessionId, ct) : null;
        return new(input.CaseId, mode, duration, classification?.ElapsedMs ?? 0, record?.Route.ToString(), failed,
            classification?.Status, classification?.Label, classification?.InputTokens, classification?.OutputTokens,
            sessions.Count > 0 && sessions.All(s => s.CostUsd.HasValue) ? sessions.Sum(s => s.CostUsd!.Value) : null,
            models.ToArray(), capture.PromptSha ?? EvaluationCorpus.Hash("no-triage-prompt"), repositorySha,
            failure ?? (failed ? "triage_failed" : null), classification?.Status == "skipped_oversize" || mode == "disabled" ? 0m
                : classifierCost?.ActualCostUsdMicros / 1_000_000m,
            sessions.Count > 0 && sessions.All(s => s.RouterRequestCount.HasValue) ? sessions.Sum(s => s.RouterRequestCount!.Value) : null,
            classification?.Model is { } classifierModel ? EvaluationCorpus.Hash(classifierModel) : null, classification?.PromptVersion, classification?.ClassifierVersion,
            classifierCost?.RequestCount, classifierSessionId);
    }

    internal static async Task<SessionCost?> ReadClassifierCostAsync(ISessionCostSource source, string sessionId, CancellationToken ct,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (var delay in retryDelays ?? new[] { 0, 1, 2, 4 }.Select(s => TimeSpan.FromSeconds(s)).ToArray())
        {
            try
            {
                await Task.Delay(delay, deadline.Token);
                if (await source.GetSessionCostAsync(sessionId, deadline.Token) is { RequestCount: > 0, ActualCostUsdMicros: >= 0 } cost
                    && cost.SessionId == sessionId) { return cost; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return null; }
            catch (Exception) { } // Accounting unavailable: unknown, not guessed zero; no response/error text exported.
        }
        return null;
    }

    private sealed class PromptCapture(ITriageRunner inner) : ITriageRunner
    {
        public string? PromptSha { get; private set; }
        public Task<WorkerResult> RunAsync(WorkItem item, RepoRef repo, string prompt, Func<string, CancellationToken, Task> onSession,
            Func<string, string, CancellationToken, Task> onTaint, CancellationToken ct)
        {
            PromptSha = EvaluationCorpus.Hash(prompt);
            return inner.RunAsync(item, repo, prompt, onSession, onTaint, ct);
        }
    }
}

// Only a synthetic board. There is deliberately no HTTP client, token or real-board write implementation.
internal sealed class EvaluationBoard(EvaluationInput input, string policy) : IGitHubIssues
{
    public long AppId => 4242;
    private IssueFacts Issue => new(1, input.Title, input.Body, "synthetic-author", false, true, [], "https://example.test/issues/1", DateTimeOffset.UnixEpoch.AddDays(1));
    public Task<IReadOnlyList<IssueFacts>> ListUpdatedAsync(RepoRef repo, DateTimeOffset? since, CancellationToken ct) => Task.FromResult<IReadOnlyList<IssueFacts>>([Issue]);
    public Task<IssueFacts> GetAsync(RepoRef repo, int number, CancellationToken ct) => Task.FromResult(Issue);
    public Task<IReadOnlyList<IssueComment>> ListCommentsAsync(RepoRef repo, int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<IssueComment>>([]);
    public Task<RepoPermission> PermissionAsync(RepoRef repo, string login, CancellationToken ct)
    {
        var role = repo.FullName == input.PermissionContext.IssueRepository ? input.PermissionContext.AuthorPermission : input.PermissionContext.AuthorPermissionOnTarget;
        return Task.FromResult(new RepoPermission(role is "maintain" ? "write" : role is "triage" ? "read" : role, role));
    }
    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct) => Task.FromResult<string?>(path == "factory/gate.yaml" ? policy : null);
    public Task<long> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct) => Task.FromResult(1L);
    public Task AddLabelsAsync(RepoRef repo, int number, IReadOnlyList<string> labels, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveLabelAsync(RepoRef repo, int number, string label, CancellationToken ct) => Task.CompletedTask;
    public Task CloseAsync(RepoRef repo, int number, CancellationToken ct) => throw new InvalidOperationException("The evaluation never builds or closes an issue.");
}
