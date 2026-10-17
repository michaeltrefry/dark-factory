using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using DarkFactory.Orchestrator.Recovery;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// What the live hot-file recovery runs (epic sc-26335 AT8, sc-26345) decide with, read from the throwaway ledger alone. Pure, so
/// <see cref="HotFileLiveTests"/> proves in every <c>dotnet test</c> that a live run can neither pass vacuously nor on a
/// half-finished recovery: a run that ends with no incident, an unresolved one, or (defect case) a safe first verdict is a
/// problem — reported as a failure, never a skip.
/// </summary>
internal static class HotFileLive
{
    public sealed record Incident(long Id, string Repo, int Generation, string State, int Attempts, string? Error);
    public sealed record Assessment(long Row, long Incident, string Decision, string? ServedClass, string? Session, int CorrectionFindings);
    public sealed record Correction(int Id, long Incident, int Attempt, string State, long? MergeRow, bool GatePassed);

    public sealed record Evidence(IReadOnlyList<Incident> Incidents, IReadOnlyList<Assessment> Assessments,
        IReadOnlyList<Correction> Corrections, IReadOnlyCollection<string> WorkerAndReviewSessions);

    /// <summary>Problems common to both cases: one incident in the repository, Resolved, every assessment fresh and served high.</summary>
    private static List<string> Common(Evidence e, RepoRef repo, out Incident? found)
    {
        var problems = new List<string>();
        var incidents = e.Incidents.Where(i => repo.Is(RepoRef.Parse(i.Repo))).ToList();
        found = incidents.Count == 1 ? incidents[0] : null;
        if (found is not { } incident)
        {
            problems.Add($"expected exactly one hot-file incident in {repo.FullName}, found {incidents.Count}");
            return problems;
        }
        if (incident.State != "Resolved")
        {
            problems.Add($"incident {incident.Id} ended {incident.State}{(incident.Error is null ? "" : $" ({incident.Error})")}, not Resolved");
        }
        var mine = e.Assessments.Where(a => a.Incident == incident.Id).ToList();
        if (mine.Count == 0)
        {
            problems.Add("no recovery assessment was recorded");
        }
        foreach (var a in mine.Where(a => a.ServedClass != "high"))
        {
            problems.Add($"assessment row {a.Row} was served on {a.ServedClass ?? "no class"}, not high");
        }
        var sessions = mine.Select(a => a.Session).ToList();
        if (sessions.Any(string.IsNullOrEmpty) || sessions.Distinct().Count() != sessions.Count)
        {
            problems.Add("assessment sessions are missing or reused");
        }
        foreach (var s in sessions.Where(s => s is not null && e.WorkerAndReviewSessions.Contains(s)))
        {
            problems.Add($"assessment session {s} is also a worker or review session");
        }
        if (mine.Count > 0 && mine.MaxBy(a => a.Row)!.Decision != "safe")
        {
            problems.Add($"the last assessment was {mine.MaxBy(a => a.Row)!.Decision}, not safe");
        }
        return problems;
    }

    /// <summary>The safe case: resolved by a safe high-class assessment with no correction at all.</summary>
    public static List<string> SafeProblems(Evidence e, RepoRef repo)
    {
        var problems = Common(e, repo, out var incident);
        if (incident is not null && e.Corrections.Any(c => c.Incident == incident.Id))
        {
            problems.Add("a correction was created for an incident the run expected to be safe");
        }
        return problems;
    }

    /// <summary>
    /// The merged-defect case: the first assessment is unsafe with a merged correction finding; exactly one correction is merged
    /// by the gate (a first attempt the gate rejected or a fresh assessment revoked may leave a second, at most); a fresh assessment after that merge is safe and resolves it.
    /// </summary>
    public static List<string> DefectProblems(Evidence e, RepoRef repo)
    {
        var problems = Common(e, repo, out var incident);
        if (incident is null)
        {
            return problems;
        }
        var assessments = e.Assessments.Where(a => a.Incident == incident.Id).OrderBy(a => a.Row).ToList();
        if (assessments.FirstOrDefault() is { } first && (first.Decision != "unsafe" || first.CorrectionFindings == 0))
        {
            problems.Add($"INCONCLUSIVE: the first assessment was {first.Decision} with {first.CorrectionFindings} merged correction finding(s); "
                + "the seeded defect was not found, so the correction path was not exercised");
        }
        var corrections = e.Corrections.Where(c => c.Incident == incident.Id).OrderBy(c => c.Attempt).ToList();
        var merged = corrections.Where(c => c.MergeRow is not null).ToList();
        if (merged.Count != 1)
        {
            problems.Add($"expected exactly one correction merged, found {merged.Count} of {corrections.Count}");
        }
        else
        {
            var delivered = merged[0];
            if (!delivered.GatePassed)
            {
                problems.Add($"correction rc-{delivered.Id} has a Merge row but no gate-passed row");
            }
            if (!assessments.Any(a => a.Row > delivered.MergeRow && a.Decision == "safe"))
            {
                problems.Add($"no safe assessment after correction rc-{delivered.Id} merged (a gate merge is not itself a safe verdict)");
            }
        }
        if (corrections.Count > 2)
        {
            problems.Add($"{corrections.Count} correction attempts exceed the cap of 2");
        }
        return problems;
    }

    /// <summary>
    /// The defect case's order: the defective story (d1) must merge first and the second story (d2) after it, so that the second
    /// merge opens the incident over d1's merged defect. The factory does not order ready work by priority; the harness releases
    /// d2 only after d1's Merge row, and this checks that it happened so.
    /// </summary>
    public static IReadOnlyList<string> OrderProblem(long? d1Merge, long? d2Merge) => (d1Merge, d2Merge) switch
    {
        (null, _) => ["the defective story (d1) has no Merge row"],
        (_, null) => ["the second story (d2) has no Merge row"],
        ({ } a, { } b) when b < a => [$"the second story (d2) merged (row {b}) before the defective story (d1, row {a})"],
        _ => [],
    };

    /// <summary>Reads the evidence from a (throwaway) ledger.</summary>
    public static async Task<Evidence> ReadAsync(LedgerDbContext db, CancellationToken ct)
    {
        var incidents = (await db.HotFileIncidents.AsNoTracking().ToListAsync(ct))
            .Select(i => new Incident(i.Id, i.Repo, i.Generation, i.State, i.Attempts, i.Error)).ToList();
        var rows = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == HotFileAssessment.Step).OrderBy(e => e.Id).ToListAsync(ct);
        var assessments = new List<Assessment>();
        foreach (var row in rows)
        {
            if (HotFileAssessment.FromDetail(row.Detail) is not { } a)
            {
                continue;
            }
            // An assessment names its incident by repository and generation (unique together).
            var owner = incidents.SingleOrDefault(i => i.Repo == a.Repo && i.Generation == a.Generation);
            assessments.Add(new(row.Id, owner?.Id ?? 0, a.Decision, a.ServedClass, a.Session, a.CorrectionFindings.Count));
        }
        var corrections = new List<Correction>();
        foreach (var c in await db.RecoveryCorrections.AsNoTracking().ToListAsync(ct))
        {
            var externalId = ItemNaming.Recovery.Format(c.Id);
            var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(w => w.ExternalId == externalId, ct);
            long? merge = null;
            var gate = false;
            if (item is not null)
            {
                merge = await db.LedgerEntries.Where(e => e.WorkItemId == item.Id && e.Step == null && e.State == WorkState.Merge)
                    .OrderBy(e => e.Id).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);
                gate = await db.LedgerEntries.AnyAsync(e => e.WorkItemId == item.Id && e.Step == RunPipeline.Steps.GatePassed, ct);
            }
            corrections.Add(new(c.Id, c.IncidentId, c.Attempt, c.State, merge, gate));
        }
        var sessions = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.ClaudeSessionId != null && e.Step != HotFileAssessment.Step).Select(e => e.ClaudeSessionId!).Distinct().ToListAsync(ct);
        var reviews = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == RunPipeline.Steps.ReviewSession && e.Detail != null)
            .Select(e => e.Detail!).ToListAsync(ct);
        sessions.AddRange(reviews.Select(d => d.Split(' ')[0]));
        return new(incidents, assessments, corrections, sessions.ToHashSet(StringComparer.Ordinal));
    }
}
