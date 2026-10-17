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

    /// <summary>
    /// The material's required reads cannot fit one assessment session (<see cref="RouterHotFileAssessor.Unreadable"/>, sc-26444):
    /// decided before any router call, and the same for every attempt on this evidence, so the incident escalates at once.
    /// </summary>
    public bool Oversized { get; init; }
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
    internal static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private async Task<List<HotFileMerge>> EvidenceAsync(LedgerDbContext db, string repo, CancellationToken ct)
    {
        var from = time.GetUtcNow() - options.HotFileWindow;
        // Only this repository's merges in the window are read: its items' own, or a plan item's changeset pass into it (its
        // merge-files row names the repository). The exact repository check below still decides; this only bounds the read.
        var mention = "\"repo\":\"" + repo + "\"";
        var merges = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && e.State == WorkState.Merge && e.RecordedAt >= from
                && db.WorkItems.Any(i => i.Id == e.WorkItemId && (i.Repo.ToLower() == repo || db.LedgerEntries.Any(f => f.WorkItemId == i.Id
                    && f.Step == RunPipeline.Steps.MergeFiles && f.Detail != null && f.Detail.ToLower().Contains(mention)))))
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
        // Any unresolved generation holds the repository (LedgerControls), so any one is the active incident here, the newest
        // first; AdvanceAsync drives every one of them, never only the newest.
        if (incidents.LastOrDefault(i => i.State != "Resolved") is { } active) return active;
        var evidence = await EvidenceAsync(db, repo, ct);
        var acknowledged = incidents.Where(i => i.State == "Resolved").SelectMany(i => HotFileSnapshot.Parse(i.Snapshot).Merges)
            .SelectMany(m => m.Files.Select(f => (m.Row, m.Base, File: f))).ToHashSet();
        var hot = QualifyingPaths(evidence, acknowledged);
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
        if (await DetectLockedAsync(db, repo, ct) is null) return;
        // Every unresolved incident of the repository holds it, so every one is advanced, oldest generation first — an older
        // generation left unresolved behind a newer one would otherwise hold the repository with nothing to move it.
        var unresolved = await db.HotFileIncidents.Where(i => i.Repo == repo && i.State != "Resolved")
            .OrderBy(i => i.Generation).ThenBy(i => i.Id).ToListAsync(ct);
        foreach (var each in unresolved) await AdvanceIncidentAsync(db, repo, each, ct);
    }

    private async Task AdvanceIncidentAsync(LedgerDbContext db, string repo, HotFileIncident incident, CancellationToken ct)
    {
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
            using var deadline = new CancellationTokenSource(StageTimeout, time);
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
                var current = await RefreshEvidenceAsync(db, snapshot, ct);
                // New overlaps and newly qualifying hot files both need review. Compare tuples, not just rows:
                // an existing contributing row may now contribute another file that was not in the snapshot.
                var reviewed = snapshot.Merges.SelectMany(m => m.Files.Select(f => (m.Row, m.Base, File: f))).ToHashSet();
                var changed = current.Any(m => m.Files.Any(f => !reviewed.Contains((m.Row, m.Base, f))));
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
                        var yaml = await policy(RepoRef.Parse(repo), material.BaseTips[bases[0]], bounded.Token).WaitAsync(bounded.Token)
                            ?? throw new InvalidOperationException("Correction base has no gate policy.");
                        var next = (await db.RecoveryCorrections.Where(c => c.IncidentId == incident.Id).MaxAsync(c => (int?)c.Attempt, ct) ?? 0) + 1;
                        bounded.Token.ThrowIfCancellationRequested();
                        await RecoveryCorrections.MaterializeAsync(db, incident, answer, GatePolicy.Parse(yaml), time.GetUtcNow(), ct, next);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested
                        && ex is not (OperationCanceledException or HttpRequestException or TimeoutException or GitHub.GitHubGatewayException))
                    {
                        // Invalid scope/policy is definitive. Unavailable verification and the shared stage deadline
                        // instead reach the bounded assessment retry path; a transient read spends no correction.
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
                else if (!changed && answer.Oversized)
                {
                    // Every attempt on this evidence would answer the same: escalate now, naming why, rather than spend the rest.
                    Escalate(incident, "Assessment cannot read the required evidence: " + answer.Problem?.TrimEnd('.'));
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

    /// <summary>
    /// The structural grant predicate alone (no evidence capture): true only for a correction whose current grant belongs to
    /// <paramref name="incident"/> in <paramref name="repository"/>. Used by repeated hold checks; the four authority
    /// boundaries revalidate in full with <see cref="AuthorizeCorrectionAsync"/>.
    /// </summary>
    public async Task<bool> GrantedAsync(string externalId, string repository, HotFileIncident incident, CancellationToken ct)
    {
        if (incident.Id == 0 || !ItemNaming.Recovery.TryParse(externalId, out _)) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await RecoveryCorrections.Grants(db, externalId, Key(repository)).AnyAsync(c => c.IncidentId == incident.Id, ct);
    }

    /// <summary>
    /// Read-only (no detection, no lock): the unresolved incident that holds <paramref name="externalId"/> in
    /// <paramref name="repository"/> now, or null — a correction is not held by its own incident while its grant is current.
    /// </summary>
    public async Task<HotFileIncident?> HoldingAsync(string externalId, string repository, CancellationToken ct)
    {
        var repo = Key(repository);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var granted = RecoveryCorrections.Grants(db, externalId, repo).Select(c => c.IncidentId);
        return await db.HotFileIncidents.AsNoTracking().Where(i => i.Repo == repo && i.State != "Resolved" && !granted.Contains(i.Id))
            .OrderByDescending(i => i.Generation).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> AuthorizeCorrectionAsync(string externalId, string repository, CancellationToken ct,
        string? deliveryBase = null, string? baseSha = null, IReadOnlyList<string>? paths = null)
    {
        if (!ItemNaming.Recovery.TryParse(externalId, out var id) || capture is null || policy is null) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var correction = await RecoveryWorkSource.ValidAsync(db, id, ct);
        if (correction is null || correction.Repo != Key(repository)) return false;
        // Backoff (E11): after a boundary that could not verify, nothing is captured again before RetryAt — not every poll.
        if (correction.RetryAt > time.GetUtcNow()) return false;
        // Other holders of the repository's lock are brief while it is Correcting (a hold check, a reconciliation, another
        // boundary): wait for it a little before calling the boundary unverifiable.
        // The deadline is the injected clock's; the tries are also bounded (a clock that does not move — a test's — cannot
        // make the wait endless), and paced by a short real pause between them.
        var lockUntil = time.GetUtcNow() + LockWait;
        var tries = (int)(LockWait / LockRetry);
        var held = await locks.TryAcquireAsync(LockKey(Key(repository)), ct);
        while (held is null && time.GetUtcNow() < lockUntil && tries-- > 0)
        {
            await Task.Delay(LockRetry, ct);
            held = await locks.TryAcquireAsync(LockKey(Key(repository)), ct);
        }
        await using var _ = held;
        if (held is null)
        {
            // Another process is deciding the repository. Counted like any unverifiable boundary, under the rows' CAS tokens.
            try { await DeferAsync(db, correction, null, "the repository's recovery lock is held by another process", externalId, ct); }
            catch (DbUpdateConcurrencyException) { } // a writer under the lock decided meanwhile; the next boundary reads it
            return false;
        }
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
            // A verified boundary alone does not end a run of deferrals (dispatch verifying and worker entry deferring would
            // never escalate): only real progress past the boundaries does (ProgressedAsync, after the push and the merge).
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!provenStale)
            {
                await DeferAsync(db, correction, incident, ex.Message, externalId, ct);
                return false;
            }
            correction.State = "Revoked"; correction.Version++;
            incident.State = "Refreshing"; incident.Error = "Correction authority revoked: " + ex.Message; incident.Version++;
            await RecoveryCorrections.RecordRefusalAsync(db, incident, incident.Error, time.GetUtcNow(), ct, externalId);
            await db.SaveChangesAsync(ct);
            return false;
        }
    }

    /// <summary>
    /// The correction made real progress past its boundaries (its push, its authorized merge): a run of deferrals ends. A
    /// verified boundary alone does not count, so a correction that always verifies at dispatch and defers at worker entry
    /// still escalates after <see cref="DeferralLimit"/>.
    /// </summary>
    public async Task ProgressedAsync(string externalId, CancellationToken ct)
    {
        if (!ItemNaming.Recovery.TryParse(externalId, out var id)) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var correction = await db.RecoveryCorrections.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (correction is null || correction.Deferrals == 0 && correction.RetryAt is null) return;
        correction.Deferrals = 0; correction.RetryAt = null; correction.Version++;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Boundary authorizations in a row that may not verify before the incident escalates (E11).</summary>
    internal const int DeferralLimit = 3;

    /// <summary>How long a boundary waits for the repository's recovery lock before it counts as unverifiable.</summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A boundary that could not verify the correction's authority (no proof it is stale): counted durably on the correction,
    /// retried after 30 s then 120 s (the assessment retry backoff), and after <see cref="DeferralLimit"/> in a row the incident
    /// escalates with the reason and the human remedy — never a capture and a pair of pause rows every poll, forever.
    /// </summary>
    private async Task DeferAsync(LedgerDbContext db, RecoveryCorrection correction, HotFileIncident? incident, string why,
        string externalId, CancellationToken ct)
    {
        incident ??= await db.HotFileIncidents.SingleAsync(i => i.Id == correction.IncidentId, ct);
        var now = time.GetUtcNow();
        correction.Deferrals++;
        correction.RetryAt = now + RetryDelay(correction.Deferrals);
        correction.Version++;
        // Each deferral is its own row (count and time in it), so the ledger shows every one, never one deduplicated line.
        var reason = $"Correction authorization deferred (could not verify, {correction.Deferrals} of {DeferralLimit} in a row, "
            + $"at {now:O}; next try not before {correction.RetryAt:O}): {why}";
        if (correction.Deferrals >= DeferralLimit)
            Escalate(incident, $"Correction authority could not be verified {DeferralLimit} times in a row ({why}); fix the named "
                + $"evidence or environment problem, then `factory recovery reassess --repo {incident.Repo}`");
        await RecoveryCorrections.RecordRefusalAsync(db, incident, reason, now, ct, externalId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The repositories a poll must look at, in a fixed number of queries however many repositories the ledger has ever seen:
    /// those with an unresolved incident, and those a factory merge inside the window went into (an item's own repository, or the
    /// repository a changeset pass's merge-files row names). Any other repository can neither be held nor become hot this poll.
    /// </summary>
    internal async Task<string[]> CandidateReposAsync(LedgerDbContext db, CancellationToken ct)
    {
        var from = time.GetUtcNow() - options.HotFileWindow;
        var unresolved = await db.HotFileIncidents.AsNoTracking().Where(i => i.State != "Resolved").Select(i => i.Repo).Distinct().ToListAsync(ct);
        var merged = db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && e.State == WorkState.Merge && e.RecordedAt >= from)
            .Select(e => e.WorkItemId).Distinct();
        var own = await db.WorkItems.AsNoTracking().Where(i => merged.Contains(i.Id)).Select(i => i.Repo).Distinct().ToListAsync(ct);
        var passes = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == RunPipeline.Steps.MergeFiles && merged.Contains(e.WorkItemId)
            && e.Detail != null && e.Detail.Contains("\"repo\":")).Select(e => e.Detail).Distinct().ToListAsync(ct);
        return unresolved.Concat(own).Concat(passes.Select(d => MergeFiles.FromDetail(d)?.Repo).OfType<string>())
            .Select(Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Kick independent repo tasks without making intake wait for a model. Faults are observed and logged.</summary>
    public async Task ScheduleAsync(TextWriter log, CancellationToken ct)
    {
        string[] repos;
        await using (var db = await contexts.CreateDbContextAsync(ct)) repos = await CandidateReposAsync(db, ct);
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
