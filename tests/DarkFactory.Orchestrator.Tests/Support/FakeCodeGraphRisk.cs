using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using Microsoft.Extensions.Time.Testing;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A CodeGraph for the merge gate's <c>risk-threshold</c> (sc-25709), in memory: the project is found, the overlay of any commit is
/// ready at once, and by default every file has no data (<c>found: false</c>: blast radius none, hotspot 0) — each answer built field by
/// field as CodeGraph's C7 (michaeltrefry/CodeGraph PR #76) shapes it (<see cref="C7Answers"/>: a file without metrics answers
/// <c>commitSha: null</c> and names the index only in <c>indexCommitSha</c>) and parsed by the factory's own client — so a pipeline test's
/// gate passes the check unless the test says otherwise through <see cref="Impact"/>, <see cref="Hotspot"/>, <see cref="Overlay"/>,
/// <see cref="Status"/> or <see cref="Project"/>. Like a real client, a call made with a cancelled token throws.
/// </summary>
public sealed class FakeCodeGraphRisk : ICodeGraphRisk
{
    /// <summary>The default-branch index commit hotspots are read at.</summary>
    public const string IndexCommit = "c0ffee0123456789abcdef0123456789abcdef01";

    public string? Project { get; set; } = "Sandbox";

    /// <summary>When set, the overlay answer for a requested commit (else ready at that commit).</summary>
    public Func<string, OverlayAnswer>? Overlay { get; set; }

    /// <summary>When set, the status answer for an overlay id (else failed).</summary>
    public Func<long, OverlayAnswer>? Status { get; set; }

    /// <summary>When set, the impact answer for (path, sha); throw from it to make the call fail.</summary>
    public Func<string, string, ImpactFileAnswer>? Impact { get; set; }

    /// <summary>When set, the hotspot answer for a path; throw from it to make the call fail.</summary>
    public Func<string, HotspotAnswer>? Hotspot { get; set; }

    /// <summary>Every call: tool and its main argument.</summary>
    public List<(string Tool, string Argument)> Calls { get; } = [];

    /// <summary>The overlay wait's timeout of a reader whose gate runs on the system clock.</summary>
    public static readonly TimeSpan OverlayTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The overlay wait's timeout of a reader whose gate's waits run on a fake clock: that timeout is a timer on the same clock, and
    /// a fake clock moves in jumps — a <see cref="SteppingClock"/> a whole step per timer created, from another thread; a test's own
    /// <c>Advance</c> by minutes. A timeout shorter than a jump (<see cref="OverlayTimeout"/>) then fires at an arbitrary moment of
    /// the wait it bounds — before the overlay is even requested, when the machine is loaded — and the wait answers
    /// <c>timed-out</c> instead of what CodeGraph would have answered (a second <c>codegraph-overlay</c> row, and no impact read in
    /// that poll). An hour is past every such test's CI timeout, which is what bounds the gate's wait there.
    /// </summary>
    public static readonly TimeSpan FakeClockOverlayTimeout = TimeSpan.FromHours(1);

    /// <param name="onFakeClock">
    /// Whether the gate the reader serves runs its waits on a fake clock (<see cref="FakeClockOverlayTimeout"/>). A harness that
    /// hands its gate a fake clock passes it here, so the pairing cannot be got wrong test by test.
    /// </param>
    public static CodeGraphRiskReader Reader(FakeCodeGraphRisk? fake = null, bool onFakeClock = false) =>
        new(fake ?? new FakeCodeGraphRisk(), onFakeClock ? FakeClockOverlayTimeout : OverlayTimeout, TimeSpan.FromMilliseconds(1), NoWait);

    /// <summary>A test reader's wait between attempts: none (the retries are counted, not timed).</summary>
    public static Task NoWait(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;

    /// <summary>A found file's impact answer at <paramref name="sha"/> (C7).</summary>
    public static ImpactFileAnswer Found(string path, string sha, BlastRadius radius) =>
        C7Answers.ParsedImpactFile("Sandbox", path, sha, true, BlastRadii.Name(radius));

    /// <summary>A found file's hotspot answer with <paramref name="riskScore"/>, its metrics computed at the index commit (C7).</summary>
    public static HotspotAnswer Hot(string path, double riskScore) => C7Answers.ParsedHotspot("Sandbox", path, riskScore);

    /// <summary>C4's not-indexed answer of a SHA CodeGraph has no usable overlay of (e.g. status <c>stale</c>).</summary>
    public static ImpactFileAnswer NotIndexed(string sha, string status) =>
        CodeGraphMcpClient.ImpactFile(C7Answers.Result(FakeMcpServer.NotIndexed("Sandbox", sha, status, 7)));

    public Task<string?> FindProjectAsync(RepoRef repo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(("search_projects", repo.FullName));
        return Task.FromResult(Project);
    }

    public Task<OverlayAnswer> RequestOverlayAsync(string project, string gitRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(("request_overlay", gitRef));
        return Task.FromResult(Overlay?.Invoke(gitRef) ?? new OverlayAnswer(false, "ready", 7, "ready", gitRef, IndexCommit, null));
    }

    public Task<OverlayAnswer> OverlayStatusAsync(long overlayId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(("get_overlay_status", overlayId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Task.FromResult(Status?.Invoke(overlayId) ?? new OverlayAnswer(false, "failed", overlayId, "failed", null, null, "polled"));
    }

    public Task<ImpactFileAnswer> AnalyzeImpactFileAsync(string project, string path, string? sha, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((sha is null ? DefaultBranchImpactCall : "analyze_impact_file", path));
        // No sha: the default-branch index answers, at its index commit (sc-26254).
        return Task.FromResult(sha is null
            ? DefaultImpact?.Invoke(path) ?? CodeGraphMcpClient.ImpactFile(C7Answers.Result(C7Answers.ImpactFile(project, path, IndexCommit, false, "none", overlay: false)))
            : Impact?.Invoke(path, sha) ?? C7Answers.ParsedImpactFile(project, path, sha, false, "none"));
    }

    /// <summary>How <see cref="Calls"/> names an <c>analyze_impact_file</c> call sent without <c>sha</c> (the default branch).</summary>
    public const string DefaultBranchImpactCall = "analyze_impact_file (default branch)";

    /// <summary>When set, the default-branch impact answer for a path (a call without <c>sha</c>); throw from it to make the call fail.</summary>
    public Func<string, ImpactFileAnswer>? DefaultImpact { get; set; }

    /// <summary>A found file's default-branch impact answer at the index commit (C7, no <c>sha</c> asked).</summary>
    public static ImpactFileAnswer FoundOnDefault(string path, BlastRadius radius) =>
        CodeGraphMcpClient.ImpactFile(C7Answers.Result(C7Answers.ImpactFile("Sandbox", path, IndexCommit, true, BlastRadii.Name(radius), overlay: false)));

    public Task<HotspotAnswer> FileHotspotAsync(string project, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(("file_hotspot", path));
        return Task.FromResult(Hotspot?.Invoke(path) ?? C7Answers.ParsedHotspot(project, path, null));
    }
}

/// <summary>
/// A fake clock on which every wait finishes by itself: each timer created (a <c>Task.Delay</c>, a <c>CancellationTokenSource</c>
/// deadline) moves the clock on by <see cref="Step"/> once it exists, so waits run in fake time, deterministically, however loaded the
/// machine is — the time elapsed is (timers created) × <see cref="Step"/>, and a deadline of N steps fires after N timers.
/// </summary>
public sealed class SteppingClock(DateTimeOffset start, TimeSpan step) : FakeTimeProvider(start)
{
    public TimeSpan Step { get; } = step;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _ = Task.Run(() => Advance(Step));
        return timer;
    }
}

/// <summary>CodeGraph's inputs to <c>risk-threshold</c> as the merge gate's unit tests hand them in.</summary>
internal static class TestRisk
{
    /// <summary>Every file the diff touches with no CodeGraph data (blast radius none, hotspot 0) at <paramref name="head"/>.</summary>
    public static CodeGraphRisk None(string head, string? diff) => With(head, diff ?? "", _ => BlastRadius.None, _ => 0);

    /// <summary>Every file the diff touches with the blast radius and hotspot score the functions give (found).</summary>
    public static CodeGraphRisk With(string head, string diff, Func<string, BlastRadius> radius, Func<string, double> score)
    {
        var files = MergeGate.TouchedFiles(diff);
        return new CodeGraphRisk(head, files.Select(f => new FileBlastRadius(f, radius(f), true)).ToList(),
            files.Select(f => new FileHotspot(f, score(f), true, FakeCodeGraphRisk.IndexCommit)).ToList(), []);
    }
}
