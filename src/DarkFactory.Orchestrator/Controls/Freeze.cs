using System.Globalization;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Controls;

/// <summary>The freeze triggers (<see cref="Control.Reason"/> of the <see cref="ControlScope.Freeze"/> row) and who writes a freeze.</summary>
public static class FreezeTrigger
{
    /// <summary><see cref="FreezeOptions.MaxConsecutiveFailures"/> items escalated in a row, with no factory merge between.</summary>
    public const string ConsecutiveFailures = "consecutive-failures";

    /// <summary>One file changed by <see cref="FreezeOptions.HotFileMerges"/> factory merges within <see cref="FreezeOptions.HotFileWindow"/>.</summary>
    public const string HotFile = "hot-file";

    /// <summary>An item's cost per round rose <see cref="FreezeOptions.CostRisingRounds"/> rounds in a row, through a fix round.</summary>
    public const string CostRising = "cost-rising";

    /// <summary>The base branch's head is red after a factory merge into it.</summary>
    public const string MainRed = "main-red";

    /// <summary>The freeze record could not be read: counted as frozen (never written: there is nothing readable to write over).</summary>
    public const string RecordUnreadable = "freeze-record-unreadable";

    /// <summary>
    /// A trigger could not be checked (the ledger or GitHub could not be read): counted as frozen for this dispatch only (E2), no
    /// freeze written — until the failure persists (<see cref="CheckFailedHeld"/>).
    /// </summary>
    public const string CheckFailed = "freeze-check-failed";

    /// <summary>
    /// A trigger's check kept failing for <see cref="FreezeOptions.CheckFailedEvaluations"/> evaluations in a row or for
    /// <see cref="FreezeOptions.CheckFailedWindow"/> (P1-E10: no failure sits silently): written as the freeze, naming the failing
    /// check, so a human sees it; their Continue acknowledges exactly that failure (its <c>check-failed</c> line) until it changes.
    /// </summary>
    public const string CheckFailedHeld = "check-failed";

    /// <summary>Who writes a freeze (<see cref="Control.ChangedBy"/>); any other writer of the row is a human's Continue.</summary>
    public const string By = "freeze";

    /// <summary>What lifts a freeze of <paramref name="trigger"/>: the words a paused or deferred item's message ends with.</summary>
    public static string Remedy(string? trigger) => trigger switch
    {
        null => "",
        CheckFailed => "it resumes on its own once the freeze triggers can be checked again (a check that keeps failing is frozen as "
            + $"{CheckFailedHeld}, which `factory continue --freeze` acknowledges)",
        RecordUnreadable => "the freeze record in the ledger's controls table cannot be read: repair it (`factory continue --freeze` cannot read it either)",
        _ => "`factory continue --freeze` clears it",
    };
}

/// <summary>
/// The freeze thresholds (<c>Freeze:*</c>). Defaults: 3 items escalated in a row; one file changed by 3 factory merges within 24
/// hours; an item's cost per round rising 2 rounds in a row (implement → fix 1 → fix 2, each round dearer than the one before).
/// </summary>
public sealed record FreezeOptions
{
    public const int DefaultMaxConsecutiveFailures = 3;
    public const int DefaultHotFileMerges = 3;
    public const double DefaultHotFileWindowHours = 24;
    public const int DefaultCostRisingRounds = 2;

    /// <summary><c>Freeze:MaxConsecutiveFailures</c> (≥ 1): distinct items escalated in a row that freeze the factory.</summary>
    public int MaxConsecutiveFailures { get; init; } = DefaultMaxConsecutiveFailures;

    /// <summary><c>Freeze:HotFileMerges</c> (≥ 2): factory merges changing one file within the window that freeze the factory.</summary>
    public int HotFileMerges { get; init; } = DefaultHotFileMerges;

    /// <summary><c>Freeze:HotFileWindowHours</c> (&gt; 0): the window of <see cref="HotFileMerges"/>.</summary>
    public TimeSpan HotFileWindow { get; init; } = TimeSpan.FromHours(DefaultHotFileWindowHours);

    /// <summary>
    /// <c>Freeze:CostRisingRounds</c> (≥ 1): consecutive rises in an item's cost per round that freeze the factory; the last
    /// <c>n + 1</c> rounds of its current attempt must each cost strictly more than the one before.
    /// </summary>
    public int CostRisingRounds { get; init; } = DefaultCostRisingRounds;

    public const int DefaultCheckFailedEvaluations = 10;
    public const double DefaultCheckFailedMinutes = 30;

    /// <summary>
    /// <c>Freeze:CheckFailedEvaluations</c> (≥ 1): failed evaluations in a row (any check failing, none holding) after which the
    /// failure is written as a <see cref="FreezeTrigger.CheckFailedHeld"/> freeze.
    /// </summary>
    public int CheckFailedEvaluations { get; init; } = DefaultCheckFailedEvaluations;

    /// <summary><c>Freeze:CheckFailedMinutes</c> (&gt; 0): how long the evaluations may keep failing before the same happens.</summary>
    public TimeSpan CheckFailedWindow { get; init; } = TimeSpan.FromMinutes(DefaultCheckFailedMinutes);
}

/// <summary>
/// The freeze checks failing in a row (<see cref="FreezeTrigger.CheckFailed"/>): how many evaluations and since when. One per
/// process in production (<see cref="Process"/>: every run's evaluator and the intake loop's share it), so a failure that persists
/// across runs is counted; reset by an evaluation that checks every trigger, and once the failure is written as a freeze.
/// </summary>
public sealed class FreezeCheckFailures
{
    public static readonly FreezeCheckFailures Process = new();

    private readonly Lock _gate = new();
    private int _count;
    private DateTimeOffset? _since;

    internal (int Count, DateTimeOffset Since) Failed(DateTimeOffset now)
    {
        lock (_gate)
        {
            _since ??= now;
            return (++_count, _since.Value);
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            (_count, _since) = (0, null);
        }
    }
}

/// <summary>Whether the factory is frozen for this dispatch, and why. A frozen factory defers new work (outcome <c>deferred</c>).</summary>
public sealed record FreezeStatus(bool Frozen, string? Trigger, string? Detail)
{
    public static readonly FreezeStatus Clear = new(false, null, null);

    public static FreezeStatus Holds(string trigger, string detail) => new(true, trigger, detail);

    public string Message => Frozen ? $"factory frozen ({Trigger}): {Detail}" : "not frozen";

    /// <summary>What lifts this freeze (<see cref="FreezeTrigger.Remedy"/>).</summary>
    public string Remedy => FreezeTrigger.Remedy(Trigger);

    /// <summary>
    /// What the evaluation passed over without counting it either way, e.g. a repo whose base branch GitHub no longer has (404):
    /// main-red reads no tip there. The caller logs them.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>This status with <paramref name="notes"/> (itself when there are none, so an unannotated status equals <see cref="Clear"/>).</summary>
    public FreezeStatus Noting(IReadOnlyList<string> notes) => notes.Count == 0 ? this : this with { Notes = notes };
}

/// <summary>
/// What the merge gate recorded about a merge before making it (<see cref="RunPipeline.Steps.MergeFiles"/>): the PR's base branch
/// and the files its diff changes, which the freeze's hot-file and main-red triggers read.
/// </summary>
public sealed record MergeFiles(string Base, IReadOnlyList<string> Files)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToDetail() => JsonSerializer.Serialize(this, Json);

    public static MergeFiles? FromDetail(string? detail)
    {
        if (detail is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<MergeFiles>(detail, Json) is { Base: not null, Files: not null } parsed ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The factory-wide automatic freeze (sc-25387). <see cref="CheckAsync"/> runs before every dispatch (<see cref="RunPipeline.RunAsync"/>),
/// before every step and worker session of a run, before a merge-queue base-update push, right before a merge, and at the start
/// of every intake poll (before triage):
/// a freeze in the ledger (<see cref="ControlScope.Freeze"/>) holds until a human's Continue; otherwise each trigger is checked
/// on the ledger (and, for main-red, GitHub) and the first that holds is written as the freeze before the dispatch is deferred.
/// <para>
/// Continue rule: a human's Continue clears the freeze and starts a fresh count — only events recorded after it (escalations,
/// merges, fix rounds) can freeze the factory again, so a Continue is never undone by the evidence it acknowledged, and a new
/// occurrence after it freezes again. The trigger rules, each but main-red over the events after the last Continue:
/// consecutive-failures — the trailing run of Escalated rows with no Merge row after them covers
/// <see cref="FreezeOptions.MaxConsecutiveFailures"/> distinct items; hot-file — one file of one repo is in the recorded files of
/// <see cref="FreezeOptions.HotFileMerges"/> factory merges within <see cref="FreezeOptions.HotFileWindow"/>; cost-rising — an
/// active item's rounds (its last Implement, then each fix round, <see cref="TransitionContext.IsFixRound"/>) cost (the router
/// cost of the worker sessions started in each) strictly more each round over the last
/// <see cref="FreezeOptions.CostRisingRounds"/> rises, the newest round started after the Continue and every one of those rounds'
/// costs recorded (an unrecorded cost is not evidence of either kind), merged items (Merge, Watch) not active; main-red — a
/// state, not an event: for each repo the factory works on now (the configured repos, <c>repos</c>, and the repos of its
/// non-terminal items), the base branch's head after its latest factory merge over all history is red by the
/// gate's CI rule (<see cref="Ci.Evaluate"/>: a failed check, or checks that could not all be read; pending is not red), unless
/// that very head is one a Continue acknowledged (the cleared freeze's <see cref="RedTipMarker"/> lines, carried into later
/// freezes) — once the head moves and is still red, it freezes again. A repo or base branch GitHub answers 404 for has no tip,
/// so nothing there can be red (a <see cref="FreezeStatus.Notes"/> line says so). Main-red's GitHub reads are reused by this
/// evaluator for <see cref="MainRedCacheTtl"/> unless a check asks for them fresh (the one right before a merge).
/// </para>
/// <para>
/// An unreadable freeze record counts as frozen; a trigger that cannot be checked (the ledger or GitHub unreadable) defers the
/// dispatch too (E2), writing no freeze — once it can be checked, the dispatch goes on. A check that keeps failing
/// (<see cref="FreezeOptions.CheckFailedEvaluations"/> evaluations in a row, counted in <c>failures</c>, or for
/// <see cref="FreezeOptions.CheckFailedWindow"/>) is written as a <see cref="FreezeTrigger.CheckFailedHeld"/> freeze naming the
/// failing check (its <see cref="CheckFailedMarker"/> line), so a human sees it (P1-E10); their Continue acknowledges that
/// failure — the same check failing the same way is passed over — until it changes.
/// </para>
/// </summary>
public sealed class FactoryFreeze(IDbContextFactory<LedgerDbContext> contexts, IControls controls, FreezeOptions options, TimeProvider time,
    IGateGitHub? github = null, IReadOnlyCollection<string>? repos = null, FreezeCheckFailures? failures = null)
{
    private sealed record Hit(string Trigger, string Detail);

    /// <summary>A check that could not run: which (<c>ledger</c>, <c>main-red owner/name@base</c>) and why.</summary>
    private sealed record Failure(string Check, Exception Error)
    {
        public string Marker => $"{CheckFailedMarker}{Check}: {Error.GetType().Name}";

        public string Why => $"{Check}: {Error.GetType().Name}: {Error.Message}";
    }

    private sealed record Evaluation(Hit? Hit, Failure? Failure, List<string> Notes);

    /// <summary>How long a main-red read (the base tip after a merge, and that tip's CI) is reused by later checks of this evaluator.</summary>
    public static readonly TimeSpan MainRedCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>The line a main-red freeze's Detail carries per red tip; a Continue of that freeze acknowledges exactly that tip.</summary>
    internal const string RedTipMarker = "red-tip ";

    /// <summary>The line a <see cref="FreezeTrigger.CheckFailedHeld"/> freeze's Detail carries; a Continue of it acknowledges that failure.</summary>
    internal const string CheckFailedMarker = "check-failed ";

    private readonly FreezeCheckFailures _failures = failures ?? new FreezeCheckFailures();
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<(string Repo, string Base, string Merge), (DateTimeOffset At, string? Tip)> _tips = [];
    private readonly Dictionary<(string Repo, string Tip), (DateTimeOffset At, CiState State, string Why)> _ci = [];

    /// <summary>
    /// Whether the factory is frozen now. <paramref name="fresh"/>: read main-red from GitHub now rather than reuse a read younger
    /// than <see cref="MainRedCacheTtl"/> (the check right before a merge); the ledger triggers are always read fresh.
    /// </summary>
    public async Task<FreezeStatus> CheckAsync(CancellationToken ct, bool fresh = false)
    {
        Control? row;
        try
        {
            row = await controls.GetAsync(ControlScope.Freeze, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return FreezeStatus.Holds(FreezeTrigger.RecordUnreadable, $"the freeze record could not be read ({ex.GetType().Name}: {ex.Message}); counted as frozen");
        }
        if (row is not null && row.State != ControlState.Running)
        {
            return FreezeStatus.Holds(row.Reason ?? FreezeTrigger.RecordUnreadable, row.Detail ?? "the freeze record gives no reason");
        }
        var acknowledged = Acknowledged(row);
        var evaluation = await FindTriggerAsync(row?.ChangedAt, acknowledged, fresh, ct);
        var hit = evaluation.Hit;
        if (hit is null && evaluation.Failure is null)
        {
            _failures.Reset();
            return FreezeStatus.Clear.Noting(evaluation.Notes);
        }
        if (hit is null)
        {
            var failure = evaluation.Failure!;
            var now = time.GetUtcNow();
            var (count, since) = _failures.Failed(now);
            if (count < options.CheckFailedEvaluations && now - since < options.CheckFailedWindow)
            {
                return FreezeStatus.Holds(FreezeTrigger.CheckFailed,
                    $"the freeze triggers could not be checked ({failure.Why}); counted as frozen until they can "
                    + $"(failed {count} time(s) in a row; written as a {FreezeTrigger.CheckFailedHeld} freeze after "
                    + $"{options.CheckFailedEvaluations} or {Minutes(options.CheckFailedWindow)})").Noting(evaluation.Notes);
            }
            hit = new Hit(FreezeTrigger.CheckFailedHeld,
                $"the freeze check {failure.Check} has failed {count} time(s) in a row since {since:u} ({failure.Error.GetType().Name}: "
                + $"{failure.Error.Message}); a Continue acknowledges this failure until it changes\n{failure.Marker}");
        }
        _failures.Reset();
        try
        {
            // Red tips a Continue acknowledged stay acknowledged through a later freeze's Continue (they are carried over); an
            // acknowledged check failure is not: a later freeze's Continue acknowledges only what that freeze names.
            var carried = acknowledged
                .Where(m => m.StartsWith(RedTipMarker, StringComparison.Ordinal) && !hit.Detail.Contains(m, StringComparison.Ordinal)).ToList();
            hit = hit with { Detail = carried.Count == 0 ? hit.Detail : $"{hit.Detail}\n{string.Join("\n", carried)}" };
            var stored = await controls.FreezeAsync(hit.Trigger, hit.Detail, row?.ChangedAt, ct);
            if (stored.State != ControlState.Running)
            {
                return FreezeStatus.Holds(stored.Reason ?? hit.Trigger, stored.Detail ?? hit.Detail).Noting(evaluation.Notes);
            }
            // A human's Continue landed meanwhile: it is not overwritten, and this dispatch is deferred once; the next decides again.
            return FreezeStatus.Holds(hit.Trigger, hit.Detail).Noting(evaluation.Notes);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return FreezeStatus.Holds(hit.Trigger, $"{hit.Detail} (the freeze could not be recorded: {ex.Message})").Noting(evaluation.Notes);
        }
    }

    /// <summary>
    /// What the last Continue acknowledged: the <see cref="RedTipMarker"/> and <see cref="CheckFailedMarker"/> lines of the cleared
    /// freeze's Detail.
    /// </summary>
    private static List<string> Acknowledged(Control? row) =>
        row is { State: ControlState.Running, Detail: { } detail }
            ? detail.Split('\n')
                .Where(l => l.StartsWith(RedTipMarker, StringComparison.Ordinal) || l.StartsWith(CheckFailedMarker, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToList()
            : [];

    private static string RedTip(string repo, string baseRef, string tip) => $"{RedTipMarker}{repo.ToLowerInvariant()}@{baseRef} {tip}";

    /// <summary>
    /// The first trigger that holds; else the first check that could not run and no Continue acknowledged (a red base on one repo
    /// still wins over a main-red read that failed on another).
    /// </summary>
    private async Task<Evaluation> FindTriggerAsync(DateTimeOffset? since, List<string> acknowledged, bool fresh, CancellationToken ct)
    {
        var notes = new List<string>();
        Evaluation Failed(string check, Exception ex)
        {
            var failure = new Failure(check, ex);
            return new Evaluation(null, acknowledged.Contains(failure.Marker) ? null : failure, notes);
        }
        LedgerDbContext db;
        Dictionary<long, (string ExternalId, string Repo, WorkState State)> names;
        Hit? hit;
        try
        {
            db = await contexts.CreateDbContextAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Failed("ledger", ex);
        }
        await using (db)
        {
            try
            {
                var transitions = await db.LedgerEntries.AsNoTracking()
                    .Where(e => e.Step == null && (since == null || e.RecordedAt > since))
                    .OrderBy(e => e.Id)
                    .ToListAsync(ct);
                names = await db.WorkItems.AsNoTracking().ToDictionaryAsync(i => i.Id, i => (i.ExternalId, i.Repo, i.State), ct);
                hit = ConsecutiveFailures(transitions, names)
                    ?? await HotFileAsync(db, transitions, names, ct)
                    ?? await CostRisingAsync(db, since, names, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Failed("ledger", ex);
            }
            if (hit is not null)
            {
                return new Evaluation(hit, null, notes);
            }
            List<Merge> latest;
            try
            {
                latest = await LatestMergesAsync(db, names, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Failed("ledger", ex);
            }
            Failure? failed = null;
            foreach (var merge in latest)
            {
                var check = $"main-red {merge.Repo.ToLowerInvariant()}@{merge.Files!.Base}";
                try
                {
                    if (await MainRedAsync(merge, names, acknowledged, fresh, notes, ct) is { } red)
                    {
                        return new Evaluation(red, null, notes);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var failure = new Failure(check, ex);
                    if (!acknowledged.Contains(failure.Marker))
                    {
                        failed ??= failure;
                    }
                }
            }
            return new Evaluation(null, failed, notes);
        }
    }

    private Hit? ConsecutiveFailures(List<LedgerEntry> transitions, Dictionary<long, (string ExternalId, string Repo, WorkState State)> names)
    {
        var outcomes = transitions.Where(e => e.State is WorkState.Escalated or WorkState.Merge).ToList();
        var streak = outcomes.Skip(outcomes.FindLastIndex(e => e.State == WorkState.Merge) + 1).Select(e => e.WorkItemId).Distinct().ToList();
        return streak.Count >= options.MaxConsecutiveFailures
            ? new Hit(FreezeTrigger.ConsecutiveFailures,
                $"{streak.Count} items escalated in a row with no factory merge between ({string.Join(", ", streak.Select(i => Name(names, i)))}); "
                + $"threshold {options.MaxConsecutiveFailures}")
            : null;
    }

    private sealed record Merge(LedgerEntry Row, string Repo, MergeFiles? Files);

    /// <summary>Factory merges among <paramref name="transitions"/>, each with the files the gate recorded before making it.</summary>
    private static async Task<List<Merge>> MergesAsync(LedgerDbContext db, List<LedgerEntry> transitions,
        Dictionary<long, (string ExternalId, string Repo, WorkState State)> names, CancellationToken ct)
    {
        var merges = transitions.Where(e => e.State == WorkState.Merge).ToList();
        var ids = merges.Select(m => m.WorkItemId).Distinct().ToList();
        var recorded = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.Step == RunPipeline.Steps.MergeFiles && ids.Contains(e.WorkItemId))
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        return merges.Select(m => new Merge(m, names.TryGetValue(m.WorkItemId, out var n) ? n.Repo : "",
                MergeFiles.FromDetail(recorded.LastOrDefault(r => r.WorkItemId == m.WorkItemId && r.Id < m.Id)?.Detail)))
            .ToList();
    }

    private async Task<Hit?> HotFileAsync(LedgerDbContext db, List<LedgerEntry> transitions,
        Dictionary<long, (string ExternalId, string Repo, WorkState State)> names, CancellationToken ct)
    {
        var from = time.GetUtcNow() - options.HotFileWindow;
        var merges = (await MergesAsync(db, transitions, names, ct)).Where(m => m.Row.RecordedAt >= from && m.Files is not null).ToList();
        var hot = merges
            .SelectMany(m => m.Files!.Files.Distinct(StringComparer.Ordinal).Select(f => (m.Repo, File: f, Merge: m)))
            .GroupBy(x => (x.Repo, x.File))
            .Where(g => g.Count() >= options.HotFileMerges)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Repo, StringComparer.Ordinal).ThenBy(g => g.Key.File, StringComparer.Ordinal)
            .FirstOrDefault();
        return hot is null
            ? null
            : new Hit(FreezeTrigger.HotFile,
                $"{hot.Key.File} on {hot.Key.Repo} was changed by {hot.Count()} factory merges within {Hours(options.HotFileWindow)} "
                + $"({string.Join(", ", hot.Select(x => $"{Name(names, x.Merge.Row.WorkItemId)} as {Ci.Short(x.Merge.Row.Detail ?? "?")}"))}); "
                + $"threshold {options.HotFileMerges}");
    }

    private async Task<Hit?> CostRisingAsync(LedgerDbContext db, DateTimeOffset? since,
        Dictionary<long, (string ExternalId, string Repo, WorkState State)> names, CancellationToken ct)
    {
        // Merged items (Merge, Watch) and finished ones: no further round can follow, so their past rounds are no evidence.
        var active = names.Where(n => !Lifecycle.IsTerminal(n.Value.State) && n.Value.State is not (WorkState.Merge or WorkState.Watch))
            .Select(n => n.Key).ToList();
        var rows = (await db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && active.Contains(e.WorkItemId)).OrderBy(e => e.Id).ToListAsync(ct))
            .ToLookup(e => e.WorkItemId);
        var sessions = (await db.WorkerSessions.AsNoTracking().Where(s => active.Contains(s.WorkItemId)).ToListAsync(ct)).ToLookup(s => s.WorkItemId);
        foreach (var id in active)
        {
            var transitions = rows[id].ToList();
            // The round starts where the item entered Implement, not where a pause returned it there ("unpaused"): a resumed
            // session keeps its original start, which would fall outside a round begun at the return.
            var implement = transitions.FindLastIndex(e => e.State == WorkState.Implement && e.Detail != "unpaused");
            if (implement < 0)
            {
                continue;
            }
            var starts = new List<LedgerEntry> { transitions[implement] };
            for (var i = implement + 1; i < transitions.Count; i++)
            {
                if (TransitionContext.IsFixRound(transitions[i - 1].State, transitions[i].State))
                {
                    starts.Add(transitions[i]);
                }
            }
            var needed = options.CostRisingRounds + 1;
            if (starts.Count < needed || (since is { } s && starts[^1].RecordedAt <= s))
            {
                continue;
            }
            var costs = new List<decimal>();
            for (var r = starts.Count - needed; r < starts.Count; r++)
            {
                var (begin, end) = (starts[r].RecordedAt, r + 1 < starts.Count ? starts[r + 1].RecordedAt : DateTimeOffset.MaxValue);
                var round = sessions[id].Where(x => x.StartedAt >= begin && x.StartedAt < end).ToList();
                if (round.Count == 0 || round.Any(x => x.CostUsd is null))
                {
                    break; // a cost not recorded (yet) is no evidence either way
                }
                costs.Add(round.Sum(x => x.CostUsd!.Value));
            }
            if (costs.Count == needed && costs.Zip(costs.Skip(1)).All(p => p.Second > p.First))
            {
                var first = starts.Count - needed;
                return new Hit(FreezeTrigger.CostRising,
                    $"{Name(names, id)}'s cost per round is still rising after a fix round: "
                    + string.Join(" → ", costs.Select((c, i) => $"{RoundName(first + i)} ${c.ToString("0.00####", CultureInfo.InvariantCulture)}"))
                    + $"; threshold {options.CostRisingRounds} rise(s) in a row");
            }
        }
        return null;
    }

    /// <summary>
    /// The latest factory merge of each repo the factory works on now: one of the configured repos (<c>repos</c>) or a repo of a
    /// non-terminal item. Main-red is a state, not an event: over all history, not only since the Continue.
    /// </summary>
    private async Task<List<Merge>> LatestMergesAsync(LedgerDbContext db, Dictionary<long, (string ExternalId, string Repo, WorkState State)> names,
        CancellationToken ct)
    {
        if (github is null)
        {
            return [];
        }
        var current = new HashSet<string>(repos ?? [], StringComparer.OrdinalIgnoreCase);
        current.UnionWith(names.Values.Where(n => !Lifecycle.IsTerminal(n.State)).Select(n => n.Repo));
        var allMerges = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && e.State == WorkState.Merge).OrderBy(e => e.Id).ToListAsync(ct);
        return (await MergesAsync(db, allMerges, names, ct))
            .Where(m => m.Files is not null && m.Row.Detail is not null && m.Repo.Length > 0 && current.Contains(m.Repo))
            .GroupBy(m => m.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(m => m.Row.Id)!)
            .OrderBy(m => m.Row.Id)
            .ToList();
    }

    private async Task<Hit?> MainRedAsync(Merge merge, Dictionary<long, (string ExternalId, string Repo, WorkState State)> names,
        List<string> acknowledged, bool fresh, List<string> notes, CancellationToken ct)
    {
        var repo = RepoRef.Parse(merge.Repo);
        if (await TipAsync(repo, merge.Repo, merge.Files!.Base, merge.Row.Detail!, fresh, ct) is not { } tip)
        {
            notes.Add($"main-red: GitHub has no {merge.Files.Base} of {merge.Repo} (404: the repo or branch is gone, or the gate App "
                + "cannot see it), so there is no tip to be red");
            return null;
        }
        var marker = RedTip(merge.Repo, merge.Files.Base, tip);
        if (acknowledged.Contains(marker))
        {
            return null; // a Continue acknowledged main red at exactly this tip; a new tip is checked again
        }
        var (state, why) = await CiAsync(repo, merge.Repo, tip, fresh, ct);
        return state == CiState.Failed
            ? new Hit(FreezeTrigger.MainRed,
                $"{merge.Files.Base} of {merge.Repo} is red at {Ci.Short(tip)} after the factory merged {Name(names, merge.Row.WorkItemId)} "
                + $"as {Ci.Short(merge.Row.Detail!)}: {why}\n{marker}")
            : null;
    }

    /// <summary>The base branch's head after <paramref name="mergeCommit"/>, or null when GitHub answers 404 for the repo or branch.</summary>
    private async Task<string?> TipAsync(RepoRef repo, string repoName, string baseRef, string mergeCommit, bool fresh, CancellationToken ct)
    {
        var key = (repoName.ToLowerInvariant(), baseRef, mergeCommit);
        var now = time.GetUtcNow();
        lock (_cacheGate)
        {
            if (!fresh && _tips.TryGetValue(key, out var hit) && now - hit.At < MainRedCacheTtl)
            {
                return hit.Tip;
            }
        }
        string? tip;
        try
        {
            tip = (await github!.CompareAsync(repo, baseRef, mergeCommit, ct)).BaseSha;
        }
        catch (GitHubNotFoundException)
        {
            tip = null;
        }
        lock (_cacheGate)
        {
            _tips[key] = (now, tip);
        }
        return tip;
    }

    private async Task<(CiState State, string Why)> CiAsync(RepoRef repo, string repoName, string tip, bool fresh, CancellationToken ct)
    {
        var key = (repoName.ToLowerInvariant(), tip);
        var now = time.GetUtcNow();
        lock (_cacheGate)
        {
            if (!fresh && _ci.TryGetValue(key, out var hit) && now - hit.At < MainRedCacheTtl)
            {
                return (hit.State, hit.Why);
            }
        }
        var (state, why) = Ci.Evaluate(await github!.GetCiAsync(repo, tip, ct));
        lock (_cacheGate)
        {
            _ci[key] = (now, state, why);
        }
        return (state, why);
    }

    private static string Name(Dictionary<long, (string ExternalId, string Repo, WorkState State)> names, long id) =>
        names.TryGetValue(id, out var n) ? n.ExternalId : $"item {id}";

    private static string RoundName(int round) => round == 0 ? "implement" : $"fix {round}";

    private static string Hours(TimeSpan window) => $"{window.TotalHours.ToString("0.##", CultureInfo.InvariantCulture)}h";

    private static string Minutes(TimeSpan window) => $"{window.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)} minutes";
}
