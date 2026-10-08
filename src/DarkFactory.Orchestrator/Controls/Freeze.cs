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

    /// <summary>A trigger could not be checked (the ledger or GitHub could not be read): counted as frozen for this dispatch only (E2).</summary>
    public const string CheckFailed = "freeze-check-failed";

    /// <summary>Who writes a freeze (<see cref="Control.ChangedBy"/>); any other writer of the row is a human's Continue.</summary>
    public const string By = "freeze";
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
}

/// <summary>Whether the factory is frozen for this dispatch, and why. A frozen factory defers new work (outcome <c>deferred</c>).</summary>
public sealed record FreezeStatus(bool Frozen, string? Trigger, string? Detail)
{
    public static readonly FreezeStatus Clear = new(false, null, null);

    public static FreezeStatus Holds(string trigger, string detail) => new(true, trigger, detail);

    public string Message => Frozen ? $"factory frozen ({Trigger}): {Detail}" : "not frozen";
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
/// The factory-wide automatic freeze (sc-25387). <see cref="CheckAsync"/> runs before every dispatch (<see cref="RunPipeline.RunAsync"/>):
/// a freeze in the ledger (<see cref="ControlScope.Freeze"/>) holds until a human's Continue; otherwise each trigger is checked
/// on the ledger (and, for main-red, GitHub) and the first that holds is written as the freeze before the dispatch is deferred.
/// <para>
/// Continue rule: a human's Continue clears the freeze and starts a fresh count — only events recorded after it (escalations,
/// merges, fix rounds) can freeze the factory again, so a Continue is never undone by the evidence it acknowledged, and a new
/// occurrence after it freezes again. The trigger rules, each over the events after the last Continue:
/// consecutive-failures — the trailing run of Escalated rows with no Merge row after them covers
/// <see cref="FreezeOptions.MaxConsecutiveFailures"/> distinct items; hot-file — one file of one repo is in the recorded files of
/// <see cref="FreezeOptions.HotFileMerges"/> factory merges within <see cref="FreezeOptions.HotFileWindow"/>; cost-rising — an
/// active item's rounds (its last Implement, then each fix round, <see cref="TransitionContext.IsFixRound"/>) cost (the router
/// cost of the worker sessions started in each) strictly more each round over the last
/// <see cref="FreezeOptions.CostRisingRounds"/> rises, the newest round started after the Continue and every one of those rounds'
/// costs recorded (an unrecorded cost is not evidence of either kind); main-red — for each repo, the base branch's head after
/// the latest factory merge into it is red by the gate's CI rule (<see cref="Ci.Evaluate"/>: a failed check, or checks that
/// could not all be read; pending is not red).
/// </para>
/// <para>
/// An unreadable freeze record counts as frozen; a trigger that cannot be checked (the ledger or GitHub unreadable) defers the
/// dispatch too (E2), but writes no freeze — once it can be checked, the dispatch goes on.
/// </para>
/// </summary>
public sealed class FactoryFreeze(IDbContextFactory<LedgerDbContext> contexts, IControls controls, FreezeOptions options, TimeProvider time,
    IGateGitHub? github = null)
{
    private sealed record Hit(string Trigger, string Detail);

    public async Task<FreezeStatus> CheckAsync(CancellationToken ct)
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
        Hit? hit;
        try
        {
            hit = await FindTriggerAsync(row?.ChangedAt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return FreezeStatus.Holds(FreezeTrigger.CheckFailed, $"the freeze triggers could not be checked ({ex.GetType().Name}: {ex.Message}); counted as frozen until they can");
        }
        if (hit is null)
        {
            return FreezeStatus.Clear;
        }
        try
        {
            var stored = await controls.FreezeAsync(hit.Trigger, hit.Detail, row?.ChangedAt, ct);
            if (stored.State != ControlState.Running)
            {
                return FreezeStatus.Holds(stored.Reason ?? hit.Trigger, stored.Detail ?? hit.Detail);
            }
            // A human's Continue landed meanwhile: it is not overwritten, and this dispatch is deferred once; the next decides again.
            return FreezeStatus.Holds(hit.Trigger, hit.Detail);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return FreezeStatus.Holds(hit.Trigger, $"{hit.Detail} (the freeze could not be recorded: {ex.Message})");
        }
    }

    private async Task<Hit?> FindTriggerAsync(DateTimeOffset? since, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var transitions = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.Step == null && (since == null || e.RecordedAt > since))
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        var names = await db.WorkItems.AsNoTracking().ToDictionaryAsync(i => i.Id, i => (i.ExternalId, i.Repo, i.State), ct);
        return ConsecutiveFailures(transitions, names)
            ?? await HotFileAsync(db, transitions, names, ct)
            ?? await CostRisingAsync(db, since, names, ct)
            ?? await MainRedAsync(db, transitions, names, ct);
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
        var active = names.Where(n => !Lifecycle.IsTerminal(n.Value.State)).Select(n => n.Key).ToList();
        var rows = (await db.LedgerEntries.AsNoTracking().Where(e => e.Step == null && active.Contains(e.WorkItemId)).OrderBy(e => e.Id).ToListAsync(ct))
            .ToLookup(e => e.WorkItemId);
        var sessions = (await db.WorkerSessions.AsNoTracking().Where(s => active.Contains(s.WorkItemId)).ToListAsync(ct)).ToLookup(s => s.WorkItemId);
        foreach (var id in active)
        {
            var transitions = rows[id].ToList();
            var implement = transitions.FindLastIndex(e => e.State == WorkState.Implement);
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

    private async Task<Hit?> MainRedAsync(LedgerDbContext db, List<LedgerEntry> transitions,
        Dictionary<long, (string ExternalId, string Repo, WorkState State)> names, CancellationToken ct)
    {
        if (github is null)
        {
            return null;
        }
        var latest = (await MergesAsync(db, transitions, names, ct))
            .Where(m => m.Files is not null && m.Row.Detail is not null && m.Repo.Length > 0)
            .GroupBy(m => m.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(m => m.Row.Id)!)
            .OrderBy(m => m.Row.Id)
            .ToList();
        foreach (var merge in latest)
        {
            var repo = RepoRef.Parse(merge.Repo);
            var tip = (await github.CompareAsync(repo, merge.Files!.Base, merge.Row.Detail!, ct)).BaseSha;
            var (state, why) = Ci.Evaluate(await github.GetCiAsync(repo, tip, ct));
            if (state == CiState.Failed)
            {
                return new Hit(FreezeTrigger.MainRed,
                    $"{merge.Files.Base} of {merge.Repo} is red at {Ci.Short(tip)} after the factory merged {Name(names, merge.Row.WorkItemId)} "
                    + $"as {Ci.Short(merge.Row.Detail!)}: {why}");
            }
        }
        return null;
    }

    private static string Name(Dictionary<long, (string ExternalId, string Repo, WorkState State)> names, long id) =>
        names.TryGetValue(id, out var n) ? n.ExternalId : $"item {id}";

    private static string RoundName(int round) => round == 0 ? "implement" : $"fix {round}";

    private static string Hours(TimeSpan window) => $"{window.TotalHours.ToString("0.##", CultureInfo.InvariantCulture)}h";
}
