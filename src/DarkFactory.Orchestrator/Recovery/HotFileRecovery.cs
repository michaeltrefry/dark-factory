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
    public int Refreshes { get; set; }
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
    public const string Step = "recovery-assessment";
    public static HotFileAssessment? FromDetail(string? detail)
    {
        try { return detail is null ? null : JsonSerializer.Deserialize<HotFileAssessment>(detail); }
        catch (JsonException) { return null; }
    }
    public string? Material { get; init; }
    public string? MaterialHash { get; init; }
    public decimal? CostUsd { get; init; }
    public string? Session { get; init; }
    public string? Prompt { get; init; }
    public string? MaterialSnapshot { get; init; }
    public string? Problem { get; init; }
    public bool EvidenceChanged { get; init; }
    public IReadOnlyList<string> Citations { get; init; } = [];
    public IReadOnlyList<RecoveryFinding> Findings { get; init; } = [];
    public IReadOnlyList<RecoveryFinding> CorrectionFindings { get; init; } = [];
    public IReadOnlyList<PendingRecoveryDisposition> PendingDispositions { get; init; } = [];
    public IReadOnlyList<ToolCall> Tools { get; init; } = [];
    public IReadOnlyList<ServedTurn> Turns { get; init; } = [];
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
public sealed partial class HotFileRecovery(IDbContextFactory<LedgerDbContext> contexts, IRunLocks locks, FreezeOptions options,
    TimeProvider time, IHotFileAssessor assessor, IReadOnlyList<IDisposable>? owned = null,
    Func<HotFileSnapshot, CancellationToken, Task<string>>? capture = null,
    Func<RepoRef, string, CancellationToken, Task<string?>>? policy = null) : IAsyncDisposable
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
        if (incident is null) return;
        // Terminal escalation is sticky. Only a failed delivery awaiting its first observation may leave the legacy
        // Escalated state; no spent assessment/refresh budget can be reset by observing an old merge again.
        await ReconcileCorrectionAsync(db, incident, ct);
        if (incident.State == "Escalated" && incident.Error?.StartsWith("Correction delivery failed;", StringComparison.Ordinal) != true) return;
        await RefreshAfterCorrectionAsync(db, incident, ct);
        if (incident.State == "Refreshing") await RefreshStaleAsync(db, incident, ct);
        if (incident.State is "Escalated" or "Correcting" or "Resolved") return;
        if (incident.Attempts >= AssessmentLimit)
        {
            Escalate(incident, "Assessment budget exhausted");
            await db.SaveChangesAsync(ct);
            return;
        }
        if (incident.NextAttemptAt > time.GetUtcNow()) return;
        var abandoned = await db.RecoveryAssessmentAttempts.Where(a => a.IncidentId == incident.Id && a.State == "Reserved")
            .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        if (abandoned is not null)
        {
            if (abandoned.LeaseUntil > time.GetUtcNow()) return;
            abandoned.State = "Uncertain"; abandoned.Version++;
            await db.SaveChangesAsync(ct);
        }
        // The process-local limiter and two database advisory seats bound assessments across processes, not just hosts.
        await Assessments.WaitAsync(ct);
        IAsyncDisposable? seat = null;
        try
        {
            seat = await locks.TryAcquireAsync(LockKey("assessment-seat-1"), ct)
                ?? await locks.TryAcquireAsync(LockKey("assessment-seat-2"), ct);
            if (seat is null) return;
            incident.State = "Assessing";
            incident.Attempts++;
            incident.Session = Guid.NewGuid().ToString();
            incident.Answer = null;
            incident.Prompt = ReviewPrompts.For("hot-file-assessment").Id;
            incident.NextAttemptAt = time.GetUtcNow() + RetryDelay(incident.Attempts);
            incident.Version++;
            var reservation = new RecoveryAssessmentAttempt { IncidentId = incident.Id,
                Snapshot = HotFileSnapshot.Parse(incident.Snapshot).Id, Session = incident.Session,
                Number = incident.Attempts, State = "Reserved", LeaseUntil = time.GetUtcNow() + StageTimeout, Version = 1 };
            db.RecoveryAssessmentAttempts.Add(reservation);
            // Claim, retry time and semantic attempt commit together BEFORE dispatch. A crash spends the uncertain call.
            await db.SaveChangesAsync(ct);
            using var deadline = new CancellationTokenSource(StageTimeout);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var snapshot = HotFileSnapshot.Parse(incident.Snapshot);
            try
            {
                if (snapshot.Merges.Count > 50) throw new InvalidOperationException("Required evidence exceeds 50 contributing merges.");
                var answer = await assessor.AssessAsync(snapshot, incident.Session, bounded.Token).WaitAsync(bounded.Token);
                if (reservation.LeaseUntil <= time.GetUtcNow())
                    throw new InvalidOperationException("Assessment lease expired; its late result has no authority.");
                incident.Answer = JsonSerializer.Serialize(answer);
                // Keep each session's result even when a subsequent attempt overwrites the incident's latest answer.
                var anchorId = snapshot.Merges.FirstOrDefault()?.Item;
                var anchor = anchorId is { } id ? await db.WorkItems.SingleAsync(i => i.Id == id, ct)
                    : await db.WorkItems.OrderBy(i => i.Id).FirstOrDefaultAsync(i => i.Repo.ToLower() == repo, ct);
                if (anchor is not null)
                    db.LedgerEntries.Add(new LedgerEntry { WorkItemId = anchor.Id, State = anchor.State,
                        Step = HotFileAssessment.Step, Detail = incident.Answer, RecordedAt = time.GetUtcNow(),
                        Outcome = StepOutcomes.Of(anchor.State, anchor.State, HotFileAssessment.Step, incident.Answer) });
                if (answer.Decision is "safe" or "unsafe" && capture is not null)
                {
                    RecoveryCorrections.ValidateAssessment(incident, answer, snapshot, correction: false);
                    if (await capture(snapshot, bounded.Token).WaitAsync(bounded.Token) != answer.Material)
                        throw new RecoveryEvidenceChangedException("Snapshot evidence changed before accepting assessment authority.");
                }
                var current = await EvidenceAsync(db, repo, ct);
                // Any new merge touching a reviewed file invalidates the assessment, including overlapping evidence.
                var paths = snapshot.Merges.SelectMany(m => m.Files.Select(f => (m.Base, File: f))).ToHashSet();
                var changed = current.Any(m => m.Files.Any(f => paths.Contains((m.Base, f))) && !snapshot.Merges.Any(old => old.Row == m.Row));
                if (answer.Snapshot == snapshot.Id && answer.Generation == incident.Generation && answer.Repo == repo
                    && answer.ServedClass == "high" && answer.Decision == "safe" && answer.EvidenceHash == incident.EvidenceHash && !changed)
                {
                    await RecordRedirectsAsync(db, incident, answer, time.GetUtcNow(), ct);
                    incident.State = "Resolved";
                    incident.ResolvedAt = time.GetUtcNow();
                    incident.Error = null;
                }
                else if (!changed && answer.Decision == "unsafe" && answer.CorrectionFindings.Count > 0 && policy is not null && capture is not null)
                {
                    try
                    {
                        incident.State = "Unsafe";
                        var material = RecoveryCorrections.ValidateAssessment(incident, answer, snapshot);
                        if (await capture(snapshot, bounded.Token).WaitAsync(bounded.Token) != answer.Material)
                            throw new RecoveryEvidenceChangedException("Snapshot evidence changed before correction authorization.");
                        var bases = snapshot.Merges.Select(m => m.Base).Distinct().ToArray();
                        if (bases.Length != 1) throw new InvalidOperationException("Correction spans more than one delivery base.");
                        var yaml = await policy(RepoRef.Parse(repo), material.BaseTips[bases[0]], ct)
                            ?? throw new InvalidOperationException("Correction base has no gate policy.");
                        var next = (await db.RecoveryCorrections.Where(c => c.IncidentId == incident.Id).MaxAsync(c => (int?)c.Attempt, ct) ?? 0) + 1;
                        await RecoveryCorrections.MaterializeAsync(db, incident, answer, GatePolicy.Parse(yaml), time.GetUtcNow(), ct, next);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        incident.State = ex is RecoveryEvidenceChangedException ? "Refreshing" : "Escalated";
                        incident.Error = "Correction refused; repository remains held: " + ex.Message;
                        await RecoveryCorrections.RecordRefusalAsync(db, incident, incident.Error, time.GetUtcNow(), ct);
                    }
                }
                else if (!changed && answer.Decision == "unsafe" && answer.PendingDispositions.Count > 0 && capture is not null)
                {
                    await RecordRedirectsAsync(db, incident, answer, time.GetUtcNow(), ct);
                    incident.State = "Refreshing";
                    incident.Error = "Pending work redirected; awaiting fresh assessment.";
                }
                else
                {
                    incident.State = changed || answer.EvidenceChanged ? "Refreshing" : "Held";
                    incident.Error = changed ? "Merge evidence changed during assessment; repository remains held." : answer.Problem ?? "No exact high-class safe acknowledgement; repository remains held.";
                }
            }
            catch (RouterUsageLimitedException ex)
            {
                // Usage waits and orderly shutdown are not semantic failures. Keep their session record for accounting.
                incident.Attempts--;
                incident.State = "Held";
                incident.Error = "Assessment waiting for router usage: " + ex.Message;
                reservation.State = "UsageWait";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                incident.Attempts--;
                incident.State = "Held";
                incident.Error = "Assessment interrupted by shutdown; no semantic attempt spent.";
                incident.NextAttemptAt = null;
                incident.Version++;
                reservation.State = "Cancelled"; reservation.Version++;
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await db.SaveChangesAsync(cleanup.Token);
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                incident.State = ex is RecoveryEvidenceChangedException ? "Refreshing" : "Held";
                incident.Error = ex.Message;
            }
            if (reservation.State == "Reserved") reservation.State = "Accepted";
            reservation.Result = incident.Answer ?? incident.Error;
            reservation.Version++;
            incident.NextAttemptAt = time.GetUtcNow() + RetryDelay(Math.Max(1, incident.Attempts));
            if (incident.State == "Held" && incident.Attempts >= AssessmentLimit)
                Escalate(incident, "Assessment budget exhausted: " + incident.Error);
            incident.Version++;
            // Result, state and exact acknowledgement commit together, with both CAS tokens. No control write.
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (seat is not null) await seat.DisposeAsync();
            Assessments.Release();
        }
    }

    /// <summary>Fresh dispatch/worker-entry authority; only this exact grant's own incident hold is excepted.</summary>
    public async Task<string?> CorrectionBaseShaAsync(string externalId, CancellationToken ct)
    {
        if (!ItemNaming.Recovery.TryParse(externalId, out var id)) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var correction = await RecoveryWorkSource.ValidAsync(db, id, ct);
        if (correction is null) return null;
        var incident = await db.HotFileIncidents.AsNoTracking().SingleAsync(i => i.Id == correction.IncidentId, ct);
        return RecoverySnapshot.FromDetail(HotFileAssessment.FromDetail(incident.Answer)?.Material)?.BaseTips.GetValueOrDefault(correction.Base);
    }

    public async Task<bool> AuthorizeCorrectionAsync(string externalId, string repository, CancellationToken ct,
        string? deliveryBase = null, string? baseSha = null, IReadOnlyList<string>? paths = null)
    {
        if (!ItemNaming.Recovery.TryParse(externalId, out var id) || capture is null || policy is null) return false;
        await using var held = await locks.TryAcquireAsync(LockKey(Key(repository)), ct);
        if (held is null) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var correction = await RecoveryWorkSource.ValidAsync(db, id, ct);
        if (correction is null || correction.Repo != Key(repository)) return false;
        var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == correction.IncidentId, ct);
        var trigger = HotFileSnapshot.Parse(incident.Snapshot);
        var provenStale = false;
        try
        {
            var material = await capture(trigger, ct);
            var snapshot = RecoverySnapshot.FromDetail(material);
            if (snapshot is not { Complete: true })
                throw new InvalidOperationException("Current evidence could not be completely verified.");
            if (RecoverySnapshot.Hash(material) != correction.MaterialHash)
            {
                provenStale = true;
                throw new InvalidOperationException("Snapshot evidence changed; correction authority is stale.");
            }
            var yaml = await policy(RepoRef.Parse(repository), snapshot.BaseTips[correction.Base], ct)
                ?? throw new InvalidOperationException("Correction base has no gate policy.");
            var currentPolicy = GatePolicy.Parse(yaml);
            provenStale = true; // A verified policy that excludes the envelope is definitive, unlike an unavailable read.
            var envelope = JsonSerializer.Deserialize<string[]>(correction.Paths)!;
            RecoveryCorrections.ValidateEnvelope(envelope, currentPolicy);
            if (deliveryBase is not null && deliveryBase != correction.Base
                || baseSha is not null && baseSha != snapshot.BaseTips[correction.Base])
                throw new InvalidOperationException("Correction delivery base no longer matches its pinned grant.");
            if (paths is not null && paths.Any(p => !envelope.Contains(p, StringComparer.Ordinal)))
                throw new InvalidOperationException("Correction diff leaves its authorized repair envelope.");
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = "Correction authorization deferred (could not verify): " + ex.Message;
            if (provenStale)
            {
                correction.State = "Revoked"; correction.Version++;
                incident.State = "Refreshing"; incident.Error = "Correction authority revoked: " + ex.Message; incident.Version++;
                reason = incident.Error;
            }
            await RecoveryCorrections.RecordRefusalAsync(db, incident, reason, time.GetUtcNow(), ct, externalId);
            await db.SaveChangesAsync(ct);
            return false;
        }
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
