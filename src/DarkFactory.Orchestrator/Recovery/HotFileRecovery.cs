using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>A generation and its acknowledgement live in the same row: resolving never writes a human control.</summary>
public sealed class HotFileIncident
{
    public long Id { get; set; }
    public required string Repo { get; set; }
    public int Generation { get; set; }
    public required string State { get; set; }
    public required string Snapshot { get; set; }
    public required string EvidenceHash { get; set; }
    public string? Session { get; set; }
    public string? Prompt { get; set; }
    public string? Answer { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public long Version { get; set; }
}

public sealed record HotFileMerge(long Row, long Item, string Commit, string Base, IReadOnlyList<string> Files);
public sealed record HotFileSnapshot(string Id, string Repo, int Generation, IReadOnlyList<HotFileMerge> Merges,
    string EvidenceHash)
{
    public static HotFileSnapshot Parse(string json) => JsonSerializer.Deserialize<HotFileSnapshot>(json)
        ?? throw new InvalidOperationException("The incident snapshot is unreadable.");
}

public sealed record HotFileAssessment(string Snapshot, int Generation, string Repo, string? ServedClass, string Decision,
    string EvidenceHash)
{
    public string? Material { get; init; }
    public string? MaterialHash { get; init; }
    public decimal? CostUsd { get; init; }
}

public interface IHotFileAssessor
{
    Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct);
}

/// <summary>
/// Repo-local durable safe-path recovery. Detection uses the merge ledger, never a control's ChangedAt. A resolved snapshot
/// acknowledges exact (base,file,merge-row) tuples; the next merge can qualify using the overlapping rolling window.
/// Repository advisory locks serialize creation, dispatch and acceptance across processes. No worker slot is involved.
/// </summary>
public sealed class HotFileRecovery(IDbContextFactory<LedgerDbContext> contexts, IRunLocks locks, FreezeOptions options,
    TimeProvider time, IHotFileAssessor assessor, IReadOnlyList<IDisposable>? owned = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        Task[] tasks;
        lock (_tasksGate) tasks = _tasks.Values.ToArray();
        await Task.WhenAll(tasks);
        foreach (var resource in owned ?? []) resource.Dispose();
        _shutdown.Dispose();
    }

    private static readonly SemaphoreSlim Assessments = new(2);
    private readonly Lock _tasksGate = new();
    private readonly Dictionary<string, Task> _tasks = new(StringComparer.Ordinal);

    internal static long LockKey(string repo) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("hot-file:" + repo)), 0) | long.MinValue;
    private static string Key(string repo) => RepoRef.Parse(repo).Key;
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private async Task<List<HotFileMerge>> EvidenceAsync(LedgerDbContext db, string repo, CancellationToken ct)
    {
        var from = time.GetUtcNow() - options.HotFileWindow;
        var merges = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && e.State == WorkState.Merge && e.RecordedAt >= from)
            .OrderBy(e => e.Id).ToListAsync(ct);
        var ids = merges.Select(e => e.WorkItemId).Distinct().ToArray();
        var items = await db.WorkItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var files = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == RunPipeline.Steps.MergeFiles && ids.Contains(e.WorkItemId))
            .OrderBy(e => e.Id).ToListAsync(ct);
        var result = new List<HotFileMerge>();
        foreach (var merge in merges)
        {
            var paths = MergeFiles.FromDetail(files.LastOrDefault(e => e.WorkItemId == merge.WorkItemId && e.Id < merge.Id)?.Detail);
            if (paths is null || ItemNaming.IsChangesetBranch(paths.Base)) continue;
            if (!RepoRef.Parse(paths.Repo ?? items[merge.WorkItemId].Repo).Is(RepoRef.Parse(repo))) continue;
            var normalized = paths.Files.Select(p => RepoPath.Normalize(p)
                ?? throw new InvalidOperationException("A merge contains an invalid repository path.")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            result.Add(new(merge.Id, merge.WorkItemId, merge.Detail ?? throw new InvalidOperationException("A merge has no commit."), paths.Base, normalized));
        }
        return result;
    }

    private async Task<HotFileIncident?> DetectLockedAsync(LedgerDbContext db, string repo, CancellationToken ct)
    {
        var incidents = await db.HotFileIncidents.Where(i => i.Repo == repo).OrderBy(i => i.Generation).ToListAsync(ct);
        if (incidents.LastOrDefault() is { State: not "Resolved" } active) return active;
        var evidence = await EvidenceAsync(db, repo, ct);
        var acknowledged = incidents.Where(i => i.State == "Resolved").SelectMany(i => HotFileSnapshot.Parse(i.Snapshot).Merges)
            .SelectMany(m => m.Files.Select(f => (m.Row, m.Base, File: f))).ToHashSet();
        var hot = evidence.SelectMany(m => m.Files.Select(f => (Merge: m, File: f)))
            .GroupBy(x => (x.Merge.Base, x.File))
            .Where(g => g.Select(x => x.Merge.Row).Distinct().Count() >= options.HotFileMerges
                && g.Any(x => !acknowledged.Contains((x.Merge.Row, x.Merge.Base, x.File))))
            .Select(g => g.Key).ToHashSet();
        if (hot.Count == 0) return null;
        var contributing = evidence.Select(m => m with { Files = m.Files.Where(f => hot.Contains((m.Base, f))).ToArray() })
            .Where(m => m.Files.Count > 0).ToArray();
        var generation = (incidents.LastOrDefault()?.Generation ?? 0) + 1;
        var snapshot = new HotFileSnapshot(Guid.NewGuid().ToString("N"), repo, generation, contributing, Hash(contributing));
        var incident = new HotFileIncident { Repo = repo, Generation = generation, State = "Held", Snapshot = JsonSerializer.Serialize(snapshot),
            EvidenceHash = snapshot.EvidenceHash, CreatedAt = time.GetUtcNow(), Version = 1 };
        db.HotFileIncidents.Add(incident);
        await db.SaveChangesAsync(ct);
        return incident;
    }

    public async Task<HotFileIncident?> HeldAsync(string repo, CancellationToken ct)
    {
        repo = Key(repo);
        await using var held = await locks.TryAcquireAsync(LockKey(repo), ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (held is not null) return await DetectLockedAsync(db, repo, ct);
        // An assessor holds the repo lock. Its durable incident remains authoritative until its atomic safe save.
        return await db.HotFileIncidents.AsNoTracking().Where(i => i.Repo == repo && i.State != "Resolved").OrderByDescending(i => i.Generation).FirstOrDefaultAsync(ct)
            ?? new HotFileIncident { Repo = repo, State = "Checking", Snapshot = "", EvidenceHash = "", Error = "Another process is deciding this repository's hold." };
    }

    public async Task AdvanceAsync(string repo, CancellationToken ct)
    {
        repo = Key(repo);
        await using var held = await locks.TryAcquireAsync(LockKey(repo), ct);
        if (held is null) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var incident = await DetectLockedAsync(db, repo, ct);
        if (incident is null || incident.State == "Escalated" || incident.NextAttemptAt > time.GetUtcNow()) return;
        if (incident.Attempts >= 3)
        {
            incident.State = "Escalated";
            incident.Error = "Assessment budget exhausted; repository remains held.";
            incident.Version++;
            await db.SaveChangesAsync(ct);
            return;
        }
        await Assessments.WaitAsync(ct);
        try
        {
            incident.State = "Assessing";
            incident.Attempts++;
            incident.Session = Guid.NewGuid().ToString();
            incident.Prompt = ReviewPrompts.For("hot-file-assessment").Id;
            incident.Version++;
            // Reserved before dispatch: a crash cannot replay an uncertain call for free.
            await db.SaveChangesAsync(ct);
            var snapshot = HotFileSnapshot.Parse(incident.Snapshot);
            try
            {
                if (snapshot.Merges.Count > 50) throw new InvalidOperationException("Required evidence exceeds 50 contributing merges.");
                var answer = await assessor.AssessAsync(snapshot, incident.Session, ct);
                incident.Answer = JsonSerializer.Serialize(answer);
                var current = await EvidenceAsync(db, repo, ct);
                // Any new merge touching a reviewed file invalidates the assessment, including overlapping evidence.
                var paths = snapshot.Merges.SelectMany(m => m.Files.Select(f => (m.Base, File: f))).ToHashSet();
                var changed = current.Any(m => m.Files.Any(f => paths.Contains((m.Base, f))) && !snapshot.Merges.Any(old => old.Row == m.Row));
                if (answer.Snapshot == snapshot.Id && answer.Generation == incident.Generation && answer.Repo == repo
                    && answer.ServedClass == "high" && answer.Decision == "safe" && answer.EvidenceHash == incident.EvidenceHash && !changed)
                {
                    incident.State = "Resolved";
                    incident.ResolvedAt = time.GetUtcNow();
                    incident.Error = null;
                }
                else
                {
                    incident.State = "Held";
                    incident.Error = changed ? "Merge evidence changed during assessment; repository remains held." : "No exact high-class safe acknowledgement; repository remains held.";
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                incident.State = "Held";
                incident.Error = ex.Message;
            }
            incident.NextAttemptAt = time.GetUtcNow() + TimeSpan.FromSeconds(incident.Attempts == 1 ? 30 : 120);
            incident.Version++;
            // State and exact acknowledgement (the immutable snapshot) commit together, never a control write.
            await db.SaveChangesAsync(ct);
        }
        finally { Assessments.Release(); }
    }

    /// <summary>Kick independent repo tasks without making intake wait for a model. Faults are observed and logged.</summary>
    public async Task ScheduleAsync(TextWriter log, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var repos = (await db.WorkItems.AsNoTracking().Select(i => i.Repo).Distinct().ToListAsync(ct)).Select(Key).Distinct().ToArray();
        foreach (var repo in repos)
        {
            if (await HeldAsync(repo, ct) is null) continue;
            Schedule(repo, log, ct);
        }
    }

    internal void Schedule(string repo, TextWriter log, CancellationToken ct)
    {
        repo = Key(repo);
        lock (_tasksGate)
        {
            if (_tasks.TryGetValue(repo, out var task) && !task.IsCompleted) return;
            _tasks[repo] = Task.Run(async () =>
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
                try { await AdvanceAsync(repo, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) { log.WriteLine($"[recovery] {repo}: {ex.GetType().Name}: {ex.Message}"); }
            }, CancellationToken.None);
        }
    }
}
