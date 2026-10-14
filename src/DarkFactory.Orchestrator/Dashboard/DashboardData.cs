using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>One active work item as the pipeline view shows it.</summary>
public sealed record PipelineRow(
    long Id,
    string ExternalId,
    string Title,
    string Repo,
    WorkState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PullRequestUrl,
    decimal? CostUsd,
    IReadOnlyList<SessionLink> Sessions,
    long? EpicId = null,
    ControlState Control = ControlState.Running,
    CloseoutStatus? FailedCloseout = null,
    long? PlanItemId = null,
    PlanView? Plan = null)
{
    /// <summary>Whether the factory will try the failed closeout again (the next poll), or has left it to a human.</summary>
    public bool CloseoutRetrying => FailedCloseout is { Attempts: < RunPipeline.MaxCloseoutAttempts };

    /// <summary>
    /// Done or Cancelled: only a plan item is ever listed so (sc-25877), for as long as one of its stories is — a row that groups
    /// them and offers no control (nothing of a finished item can be paused or stopped).
    /// </summary>
    public bool Finished => State is WorkState.Done or WorkState.Cancelled;
}

/// <summary>
/// A plan item's stories as the pipeline page groups them under it (sc-25876), read from the ledger alone: the stories its
/// <c>plan-stories</c> checkpoint lists, in plan order, classified (<see cref="PlanRelease.Classify"/>) from their own items' rows,
/// the plan's own order (its <c>plan</c> checkpoint) and its <c>plan-released</c> rows — the board is never asked.
/// </summary>
/// <param name="Hold">What a failed story holds, as the plan item records it (<see cref="PlanRelease.HeldByFailed"/>); null when none is failed.</param>
/// <param name="LastHeld">The plan item's newest <c>plan-held</c> row that names something else (a blocker outside the plan, an archived
/// story, …), with when it was recorded: such a hold has no record of its end, so it is shown as the last one recorded. A lost
/// claim (<c>claim lost: …</c>, sc-25984) does have one (<c>plan-claim-regained</c>) and is shown only until then.</param>
/// <param name="FailedNotices">How often the plan item's comment for its latest hold by a failed story (or its lifting) was tried and
/// refused by the board (its <c>plan-notice</c> rows since that record); 0 once it is posted, or when there is none to post.</param>
/// <param name="Changesets">A plan of several repositories (sc-25873): where each repository's changeset stands, in merge order
/// (<see cref="Changeset.States"/>, from the plan item's own rows); empty for a plan with one changeset or none.</param>
public sealed record PlanView(IReadOnlyList<PlanStoryView> Stories, PlanStatus Status, string? Hold, (string Detail, DateTimeOffset At)? LastHeld = null,
    int FailedNotices = 0, IReadOnlyList<ChangesetState>? Changesets = null)
{

    /// <summary>Whether the factory will try the refused comment again (the next evaluation), or has left it to the ledger and this page.</summary>
    public bool NoticeRetrying => FailedNotices < RunPipeline.MaxPlanNoticeAttempts;

    public int Merged => Status.Count(PlanStoryState.Merged);
    public int InFlight => Status.Count(PlanStoryState.InFlight);
    /// <summary>Not released yet: waiting on blockers, or blocked behind a failed story.</summary>
    public int Waiting => Status.Count(PlanStoryState.Waiting) + Status.Count(PlanStoryState.Blocked);
    public int Failed => Status.Count(PlanStoryState.Failed);
    public bool Held => Status.Held;
    public bool Stuck => Status.Stuck;
}

/// <summary>One story of a plan beneath its plan item (sc-25876).</summary>
/// <param name="Title">The story's title: its work item's, or — not released yet, so with no work item — the one its plan recorded,
/// made inert (<see cref="WorkSources.UntrustedText.Inert"/>: a planner wrote it).</param>
/// <param name="ItemState">Its work item's state; null while the ledger does not know it.</param>
/// <param name="Waits">What it waits on, in words: its unmerged blockers, <c>held by sc-N (escalated)</c>, how it failed, or — for a
/// story that counts as merged though the factory's gate did not merge it — that it was merged outside the factory into the changeset
/// branch; empty when nothing.</param>
/// <param name="Repo">The repository the plan worked it in (sc-25873); null when the plan's record no longer reads back.</param>
public sealed record PlanStoryView(string ExternalId, string Title, PlanStoryState State, WorkState? ItemState, string Waits, string? Repo = null)
{
    /// <summary>The state the page shows: the work item's, or what the plan says of a story with none.</summary>
    public string Shown => ItemState?.ToString() ?? (State == PlanStoryState.InFlight ? "released" : "not released");

    /// <summary>Failed, or blocked behind a failed story: shown as an error.</summary>
    public bool Failing => State is PlanStoryState.Failed or PlanStoryState.Blocked;

    public static string WaitsOf(PlanStoryStatus story, PlanStatus plan) => story.State switch
    {
        PlanStoryState.Failed => $"failed: {story.Failure!.As}",
        PlanStoryState.Blocked => "held by " + string.Join(", ", story.HeldBy.Select(by =>
            $"{by} ({plan.Stories.First(s => s.Id == by).Failure?.As ?? "failed"})")),
        PlanStoryState.Waiting when story.WaitsOn.Count > 0 => $"waits on {string.Join(", ", story.WaitsOn)}",
        PlanStoryState.Waiting => "nothing: released at the next evaluation",
        PlanStoryState.InFlight when story.ItemState is null => "to be picked up",
        // Escalated, yet counted as merged (sc-25877): the row says why.
        PlanStoryState.Merged when story.MergedOutside => PlanRelease.MergedOutsideIntoChangeset,
        _ => "",
    };
}

/// <summary>
/// A worker session of an item; <see cref="ClaudeSessionId"/> is null until the worker reports it. <see cref="LastEventAt"/>: when
/// its latest stream event was stored (read for running sessions only).
/// </summary>
public sealed record SessionLink(string? ClaudeSessionId, int Attempt, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? ExitStatus, decimal? CostUsd,
    DateTimeOffset? LastEventAt = null)
{
    public bool Running => EndedAt is null;

    /// <summary>
    /// A running session with no event (nor, before its first, since it started) for <paramref name="threshold"/> at
    /// <paramref name="now"/> is quiet (sc-25388). Only marked on the dashboard: silence alone never interrupts a worker.
    /// </summary>
    public bool QuietAt(DateTimeOffset now, TimeSpan threshold) => Running && now - (LastEventAt ?? StartedAt) >= threshold;
}

public sealed record SessionHeader(long WorkItemId, string ExternalId, string Title, string Repo, SessionLink Session);

/// <summary>
/// The dashboard's reads of the ledger. Read-only: the dashboard's only writes are the controls
/// (<see cref="Controls.ControlActions"/>, E8).
/// </summary>
public interface IDashboardData
{
    /// <summary>Items not Done or Cancelled, oldest first, each with the control that applies to it.</summary>
    Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct);

    Task<SessionHeader?> SessionAsync(string claudeSessionId, CancellationToken ct);

    /// <summary>Every scope's control (factory, epics, items).</summary>
    Task<IReadOnlyList<Control>> ControlsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Control>>([]);

    /// <summary>How long a running session may go without an event before the pages mark it quiet (<c>Worker:QuietMinutes</c>).</summary>
    TimeSpan QuietThreshold => DashboardData.DefaultQuietThreshold;

    /// <summary>The factory's metrics over the ledger (<see cref="LedgerMetrics"/>; N/A when unmeasured, sandbox and demo items left out).</summary>
    Task<IReadOnlyList<Metric>> MetricsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Metric>>([]);
}

public sealed class DashboardData(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time, TimeSpan? quietThreshold = null,
    MetricsOptions? metrics = null, TimeSpan? metricsTtl = null) : IDashboardData
{
    private readonly LedgerMetrics _metrics = new(contexts, metrics ?? MetricsOptions.Default);
    private readonly TimeSpan _metricsTtl = metricsTtl ?? DefaultMetricsTtl;
    private readonly SemaphoreSlim _metricsGate = new(1, 1);
    private (IReadOnlyList<Metric> Value, DateTimeOffset At)? _cachedMetrics;

    /// <summary>
    /// How long computed metrics are served before they are computed again: they read every item's rows, so they are not recomputed on
    /// every ledger change the pipeline page reloads on.
    /// </summary>
    public static readonly TimeSpan DefaultMetricsTtl = TimeSpan.FromSeconds(60);

    /// <summary>The metrics, computed at most once per <see cref="DefaultMetricsTtl"/> (<c>metricsTtl</c>) for every page and viewer.</summary>
    public async Task<IReadOnlyList<Metric>> MetricsAsync(CancellationToken ct)
    {
        await _metricsGate.WaitAsync(ct);
        try
        {
            if (_cachedMetrics is { } cached && time.GetUtcNow() - cached.At < _metricsTtl)
            {
                return cached.Value;
            }
            var value = await _metrics.ComputeAsync(ct);
            _cachedMetrics = (value, time.GetUtcNow());
            return value;
        }
        finally
        {
            _metricsGate.Release();
        }
    }

    /// <summary>
    /// <c>Worker:QuietMinutes</c>'s default: the longest legitimate tool call (<see cref="RunPipeline.LongestToolCall"/>, a long
    /// <c>dotnet test</c>) streams nothing for that long, so only a silence past it is worth a look.
    /// </summary>
    public static readonly TimeSpan DefaultQuietThreshold = RunPipeline.LongestToolCall;

    private static readonly WorkState[] Finished = [WorkState.Done, WorkState.Cancelled];

    public TimeSpan QuietThreshold { get; } = quietThreshold ?? DefaultQuietThreshold;

    public async Task<IReadOnlyList<Control>> ControlsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Controls.AsNoTracking().OrderBy(c => c.Scope).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var items = await db.WorkItems.AsNoTracking().Where(i => !Finished.Contains(i.State)).OrderBy(i => i.Id).ToListAsync(ct);
        // A finished plan item (Done, Cancelled) is listed for as long as one of its stories is (sc-25877): a merged story stays in
        // Watch after its plan closed, and is still shown beneath its plan, not as a row of its own.
        var listed = items.Select(i => i.Id).ToHashSet();
        var finishedPlans = items.Select(i => i.PlanItemId).OfType<long>().Where(p => !listed.Contains(p)).Distinct().ToList();
        if (finishedPlans.Count > 0)
        {
            items = [.. items.Concat(await db.WorkItems.AsNoTracking().Where(i => finishedPlans.Contains(i.Id)).ToListAsync(ct)).OrderBy(i => i.Id)];
        }
        var controls = (await db.Controls.AsNoTracking().ToListAsync(ct)).ToDictionary(c => c.Scope);
        var now = time.GetUtcNow();
        var ids = items.Select(i => i.Id).ToList();
        var plans = await PlansAsync(db, items, ct);
        var byId = items.ToDictionary(i => i.Id);
        // As LedgerControls.EffectiveAsync decides it: a story of a plan is also held to its plan item's control (sc-25876), and a
        // plan being stopped stops its stories except the merged ones.
        ControlState ControlOf(WorkItem i)
        {
            var plan = i.PlanItemId is { } p && byId.TryGetValue(p, out var planItem) ? planItem : null;
            var planScope = plan is null ? "" : ControlScope.Item(plan.ExternalId);
            var merged = plan is not null && plans.GetValueOrDefault(plan.Id)?.Status.Stories
                .Any(s => s.Id == i.ExternalId && s.State == PlanStoryState.Merged) == true;
            return controls.GetValueOrDefault(ControlScope.Item(i.ExternalId))?.State == ControlState.Stopping
                || (!merged && controls.GetValueOrDefault(planScope)?.State == ControlState.Stopping) ? ControlState.Stopping
                : new[] { ControlScope.Factory, ControlScope.Usage, ControlScope.Freeze, ControlScope.Item(i.ExternalId), i.EpicId is { } e ? ControlScope.Epic(e) : "", planScope }
                    .Any(s => controls.GetValueOrDefault(s)?.PausesAt(now) == true) ? ControlState.Paused
                : ControlState.Running;
        }
        var rows = await db.WorkerSessions.AsNoTracking().Where(s => ids.Contains(s.WorkItemId)).OrderBy(s => s.Id).ToListAsync(ct);
        var lastEvents = await LastEventsAsync(db, rows.Where(s => s.EndedAt is null).Select(s => s.Id), ct);
        var sessions = rows.ToLookup(s => s.WorkItemId);
        var links = (await db.LedgerEntries.AsNoTracking()
                .Where(e => ids.Contains(e.WorkItemId) && e.Step == RunPipeline.Steps.Linked)
                .OrderBy(e => e.Id)
                .ToListAsync(ct))
            .GroupBy(e => e.WorkItemId)
            .ToDictionary(g => g.Key, g => g.Last().Detail);
        var closeouts = await new WorkLedger(db, time).CloseoutRowsAsync(items.Where(i => i.State is WorkState.Watch or WorkState.Planned).Select(i => i.Id).ToList(), ct);
        return items.Select(i => new PipelineRow(
                i.Id, i.ExternalId, i.Title, i.Repo, i.State, i.CreatedAt, i.UpdatedAt,
                PullRequestUrl(links.GetValueOrDefault(i.Id)),
                // Spend per item (E9, reporting only): the sum of its sessions' measured router costs, null (N/A) when none is
                // measured; the page labels a partial sum with how many sessions it covers (Format.ItemCost, E5).
                sessions[i.Id].Any(s => s.CostUsd is not null) ? sessions[i.Id].Where(s => s.CostUsd is not null).Sum(s => s.CostUsd!.Value) : null,
                sessions[i.Id].Select(s => Link(s, lastEvents.GetValueOrDefault(s.Id))).ToList(),
                i.EpicId,
                ControlOf(i),
                // A merged item's closeout, or a finished plan's (sc-25871: a plan item has no Merge row, so every closeout row is its own).
                i.State is WorkState.Watch or WorkState.Planned && RunPipeline.CloseoutOf(closeouts[i.Id]) is { Posted: false, Attempts: > 0 } failed ? failed : null,
                // A story of a plan is shown under its plan item (sc-25876), which is listed while any story of it is (sc-25877).
                i.PlanItemId is { } p && byId.ContainsKey(p) ? p : null,
                plans.GetValueOrDefault(i.Id)))
            .ToList();
    }

    private static readonly string[] PlanSteps =
        [RunPipeline.Steps.Plan, RunPipeline.Steps.PlanStories, RunPipeline.Steps.PlanReleased, RunPipeline.Steps.PlanHeld,
            RunPipeline.Steps.PlanStuck, RunPipeline.Steps.PlanResumed, RunPipeline.Steps.PlanClaimRegained, RunPipeline.Steps.PlanNotice,
            // What its changesets' states are read from (sc-25873).
            RunPipeline.Steps.Changeset, RunPipeline.Steps.ChangesetPushed, RunPipeline.Steps.ChangesetDelivered, RunPipeline.Steps.ChangesetEmpty,
            RunPipeline.Steps.Linked];

    /// <summary>
    /// The plan of each active item that has one (sc-25876; keyed by the plan item's id): its stories from its <c>plan-stories</c>
    /// row, each classified from its own item's transitions — a story with no work item yet (not released, or not picked up) is
    /// listed all the same, with the title the <c>plan</c> checkpoint recorded.
    /// </summary>
    private static async Task<Dictionary<long, PlanView>> PlansAsync(LedgerDbContext db, List<WorkItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        var rows = (await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.WorkItemId) && e.Step != null && PlanSteps.Contains(e.Step)).OrderBy(e => e.Id).ToListAsync(ct))
            .ToLookup(e => e.WorkItemId);
        var planned = items.Where(i => rows[i.Id].Any(e => e.Step == RunPipeline.Steps.PlanStories))
            .Select(i =>
            {
                var own = rows[i.Id].ToList();
                var recorded = own.FindLastIndex(e => e.Step == RunPipeline.Steps.PlanStories);
                return (Item: i, Stories: PlanRelease.StoriesOf(own[recorded].Detail), Plan: own.Take(recorded).LastOrDefault(e => e.Step == RunPipeline.Steps.Plan)?.Detail,
                    Since: own.Skip(recorded + 1).ToList());
            }).ToList();
        if (planned.Count == 0)
        {
            return [];
        }
        var wanted = planned.SelectMany(p => p.Stories).Distinct(StringComparer.Ordinal).ToList();
        var stories = await db.WorkItems.AsNoTracking().Where(i => wanted.Contains(i.ExternalId)).ToListAsync(ct);
        var storyIds = stories.Select(s => s.Id).ToList();
        // Each story's transitions, and the one checkpoint that counts a story as merged without a Merge row (sc-25877).
        var transitions = (await db.LedgerEntries.AsNoTracking()
                .Where(e => storyIds.Contains(e.WorkItemId) && (e.Step == null || e.Step == RunPipeline.Steps.MergedIntoChangeset)).OrderBy(e => e.Id).ToListAsync(ct))
            .ToLookup(e => e.WorkItemId);
        var plannedIds = planned.Select(p => p.Item.Id).ToList();
        var ownTransitions = (await db.LedgerEntries.AsNoTracking().Where(e => plannedIds.Contains(e.WorkItemId) && e.Step == null).OrderBy(e => e.Id).ToListAsync(ct))
            .ToLookup(e => e.WorkItemId);
        var views = new Dictionary<long, PlanView>();
        foreach (var (item, ofPlan, plan, since) in planned)
        {
            var known = stories.Where(s => s.Source == item.Source).ToDictionary(s => s.ExternalId, StringComparer.Ordinal);
            var blockers = PlanRelease.RecordedBlockers(plan, ofPlan);
            var titles = PlanRelease.RecordedTitles(plan, ofPlan);
            var released = since.Where(e => e.Step == RunPipeline.Steps.PlanReleased)
                .Select(e => PlanRelease.StoriesOf(e.Detail).FirstOrDefault() ?? "").ToHashSet(StringComparer.Ordinal);
            // Several repositories (sc-25873): the changesets a story waits on, and where each repository's changeset stands.
            var primary = RepoRef.Parse(item.Repo);
            var repos = PlanRelease.RecordedRepos(plan, ofPlan, primary);
            var all = rows[item.Id].Concat(ownTransitions[item.Id]).OrderBy(e => e.Id).ToList();
            var changesets = Changeset.States(all, primary, item.State);
            var delivered = changesets.Where(c => c.Delivered).Select(c => c.Changeset.Repo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> Blockers(string s) => [.. blockers?.GetValueOrDefault(s) ?? [],
                .. (repos?.ChangesetBlockers.GetValueOrDefault(s) ?? []).Where(r => !delivered.Contains(r)).Select(PlanRelease.ChangesetBlocker)];
            var status = PlanRelease.Classify(ofPlan, released, Blockers,
                id => known.TryGetValue(id, out var story) ? transitions[story.Id].ToList() : null,
                changesetStories: blocker => repos?.StoriesIn(PlanRelease.RepoOfChangesetBlocker(blocker)) ?? []);
            // A lost claim that is the factory's again (sc-25984) holds nothing any more.
            var claimLost = RunPipeline.PlanClaimLost(since);
            var lastHeld = since.LastOrDefault(e => e.Step == RunPipeline.Steps.PlanHeld
                && e.Detail?.StartsWith(PlanRelease.FailedPrefix, StringComparison.Ordinal) != true
                && (claimLost is not null || !RunPipeline.IsClaimLost(e.Detail)));
            // The comment for the latest hold by a failed story (or its lifting): tried and refused so far, unless it was posted since.
            var notices = since.Skip(since.FindLastIndex(RunPipeline.IsFailedHoldRow) + 1).Where(e => e.Step == RunPipeline.Steps.PlanNotice).ToList();
            var failedNotices = since.Any(RunPipeline.IsFailedHoldRow) && !notices.Any(n => n.Detail == RunPipeline.Posted) ? notices.Count : 0;
            views[item.Id] = new PlanView(
                [.. status.Stories.Select(s => new PlanStoryView(s.Id,
                    known.TryGetValue(s.Id, out var story) ? story.Title : UntrustedText.Inert(titles?.GetValueOrDefault(s.Id) ?? ""),
                    s.State, s.ItemState, PlanStoryView.WaitsOf(s, status), repos?.RepoOf(s.Id)))],
                status, status.Held ? PlanRelease.HeldByFailed(status) : null,
                lastHeld?.Detail is { } detail ? (detail, lastHeld.RecordedAt) : null, failedNotices, changesets.Count > 1 ? changesets : []);
        }
        return views;
    }

    public async Task<SessionHeader?> SessionAsync(string claudeSessionId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var session = await db.WorkerSessions.AsNoTracking().SingleOrDefaultAsync(s => s.ClaudeSessionId == claudeSessionId, ct);
        if (session is null)
        {
            return null;
        }
        var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Id == session.WorkItemId, ct);
        var lastEvents = await LastEventsAsync(db, session.EndedAt is null ? [session.Id] : [], ct);
        return new SessionHeader(item.Id, item.ExternalId, item.Title, item.Repo, Link(session, lastEvents.GetValueOrDefault(session.Id)));
    }

    /// <summary>When each of <paramref name="sessionIds"/>' latest event was stored (its highest sequence: the unique index).</summary>
    private static async Task<Dictionary<long, DateTimeOffset>> LastEventsAsync(LedgerDbContext db, IEnumerable<long> sessionIds, CancellationToken ct)
    {
        var last = new Dictionary<long, DateTimeOffset>();
        foreach (var id in sessionIds)
        {
            if (await db.SessionEvents.AsNoTracking().Where(e => e.WorkerSessionId == id).OrderByDescending(e => e.Sequence)
                    .Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct) is { } at)
            {
                last[id] = at;
            }
        }
        return last;
    }

    private static SessionLink Link(WorkerSession s, DateTimeOffset? lastEventAt) =>
        new(s.ClaudeSessionId, s.Attempt, s.StartedAt, s.EndedAt, s.ExitStatus, s.CostUsd, s.EndedAt is null ? lastEventAt : null);

    /// <summary>The <c>linked</c> checkpoint's detail is "&lt;PR url&gt; &lt;branch url&gt;".</summary>
    private static string? PullRequestUrl(string? linkedDetail) =>
        linkedDetail?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : null;
}

/// <summary>
/// Ledger changes to work items (any state, checkpoint or session-cost write, from any process),
/// relayed by <see cref="Sessions.SessionEventRelay"/>. A null item id means "anything may have
/// changed" (notifications were missed while the relay reconnected).
/// </summary>
public sealed class PipelineChanges
{
    /// <summary>Raised on the relay's thread: handlers must only schedule their work.</summary>
    public event Action<long?>? Changed;

    public void Notify(long? workItemId)
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<long?>>() ?? [])
        {
            try
            {
                handler(workItemId);
            }
            catch (Exception)
            {
                // one broken viewer must not stop the others (or the relay)
            }
        }
    }
}
