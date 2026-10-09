using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Epic AT5 (sc-25391), the freeze and stuck-worker detection through the production wiring (<c>factory run</c>) against a
/// throwaway ledger (docs/acceptance.md, P2 epic AT5). Live: skipped unless FACTORY_E2E=1 and each test's own story variable.
/// <list type="bullet">
/// <item>Freeze: the test seeds the throwaway ledger (escalations, or an unreadable freeze record); the frozen factory defers the
/// story before reading the board, so the story stays To Do and is never claimed.</item>
/// <item>Stuck: the worker is a stub Claude Code CLI (a bash script the test writes) that replays a recorded stream-json
/// transcript — the same fixtures the unit tests replay — and honours the factory's pause hook exactly as the real CLI does (it
/// parses the hook's flag path out of the <c>--settings</c> JSON, <see cref="StubClaude.PauseFlagExtractor"/>, and ends the session at the next tool call once the flag exists). A real model
/// cannot be made to loop on demand, so this is the deterministic looping worker. It runs as the owner (<c>Worker:RunAs=none</c>):
/// the stub reads and writes nothing but its fixture and its own log, and the only process the factory then stops is the stub.</item>
/// </list>
/// </summary>
public class FreezeAndStuckTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

    private static CancellationTokenSource Timeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        return timeout;
    }

    private static async Task<RunOutcome> FactoryRunAsync(FactoryOptions options, int storyId, CancellationToken ct)
    {
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        return await FactoryRunner.RunAsync(options, FactoryRunner.CreateWorkSource(options, shortcutHttp), storyId, ignoreScope: true, Console.Out, ct);
    }

    /// <summary>The run was deferred by the freeze with <paramref name="trigger"/>: no ledger row for the story, the story untouched.</summary>
    private static async Task AssertDeferredAsync(E2e e2e, int storyId, string trigger, RunOutcome outcome, string boardStateBefore, CancellationToken ct)
    {
        Assert.False(outcome.Succeeded);
        Assert.StartsWith($"factory frozen ({trigger}): ", outcome.Deferred);
        Assert.Empty(await e2e.HistoryAsync(storyId, ct)); // not even taken into the ledger
        Assert.Equal(boardStateBefore, (await E2e.StoryAsync(storyId, ct)).State); // never claimed
        Assert.Empty(await E2e.PullRequestsAsync(Harness.Options.DefaultRepo, storyId, ct));
    }

    /// <summary>
    /// AT5: N failures stop dispatch. The test seeds <c>Freeze:MaxConsecutiveFailures</c> items escalated in a row (no factory merge
    /// between) into the throwaway ledger; the next dispatch writes the consecutive-failures freeze and defers.
    /// </summary>
    [Fact]
    public async Task N_items_escalated_in_a_row_freeze_the_factory_and_the_next_dispatch_is_deferred()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_FREEZE_STORY", "a To Do story the frozen factory must leave alone (it stays To Do)");
        await using var e2e = await E2e.StartAsync("df_e2e_p2at5_freeze");
        var options = e2e.Options();
        using var timeout = Timeout();
        var ct = timeout.Token;
        var before = (await E2e.StoryAsync(storyId, ct)).State;
        var n = options.Freeze.MaxConsecutiveFailures;
        await using (var db = e2e.Db())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            for (var i = 1; i <= n; i++)
            {
                var item = await ledger.GetOrCreateAsync(RunPipeline.Source, StoryId.Format(900_000_000 + i), $"[dark-factory e2e] seeded escalation {i}",
                    options.DefaultRepo.FullName, null, ct);
                await ledger.RecordAsync(item, WorkState.Implement, null, null, ct);
                await ledger.RecordAsync(item, WorkState.Escalated, null, "seeded by the acceptance test: the run failed", ct);
            }
        }

        var outcome = await FactoryRunAsync(options, storyId, ct);

        await AssertDeferredAsync(e2e, storyId, FreezeTrigger.ConsecutiveFailures, outcome, before, ct);
        Assert.Contains($"{n} items escalated in a row", outcome.Deferred);
        await using var read = e2e.Db();
        var freeze = await read.Controls.AsNoTracking().SingleAsync(c => c.Scope == ControlScope.Freeze, ct);
        Assert.Equal((ControlState.Paused, FreezeTrigger.ConsecutiveFailures, FreezeTrigger.By), (freeze.State, freeze.Reason, freeze.ChangedBy));
        // It holds from the record alone: the next dispatch is deferred too.
        Assert.StartsWith($"factory frozen ({FreezeTrigger.ConsecutiveFailures}): ", (await FactoryRunAsync(options, storyId, ct)).Deferred);
    }

    /// <summary>
    /// AT5: an unreadable freeze record counts as frozen. The test writes the throwaway ledger's freeze row with a state no
    /// <see cref="ControlState"/> has (raw SQL), so reading it fails; the dispatch is deferred as frozen.
    /// </summary>
    [Fact]
    public async Task An_unreadable_freeze_record_counts_as_frozen_and_the_dispatch_is_deferred()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_FREEZE_STORY", "a To Do story the frozen factory must leave alone (it stays To Do)");
        await using var e2e = await E2e.StartAsync("df_e2e_p2at5_unreadable");
        var options = e2e.Options();
        using var timeout = Timeout();
        var ct = timeout.Token;
        var before = (await E2e.StoryAsync(storyId, ct)).State;
        await using (var db = e2e.Db())
        {
            const string corrupt = "corrupted";
            Assert.False(Enum.TryParse<ControlState>(corrupt, ignoreCase: true, out _));
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO controls ("Scope", "State", "ChangedBy", "ChangedAt") VALUES ({ControlScope.Freeze}, {corrupt}, {"e2e"}, {DateTimeOffset.UtcNow})""", ct);
        }

        var outcome = await FactoryRunAsync(options, storyId, ct);

        await AssertDeferredAsync(e2e, storyId, FreezeTrigger.RecordUnreadable, outcome, before, ct);
        Assert.Contains("could not be read", outcome.Deferred);
    }

    /// <summary>
    /// AT5: a looping worker is interrupted. The stub replays <c>stream-json-stuck-loop.jsonl</c> (the same failing Edit again and
    /// again); the detector trips, the pipeline raises the pause flag, the stub stops at its next tool call, the round is recorded
    /// stuck (failed) and retried once with a fresh session, which loops again, so the item escalates. Nothing is pushed.
    /// </summary>
    [Fact]
    public async Task A_looping_worker_is_interrupted_at_its_next_tool_call_and_the_item_escalates_after_its_retry()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_STUCK_STORY", "a To Do story for the stub looping worker (it escalates; nothing is pushed)");
        await using var e2e = await E2e.StartAsync("df_e2e_p2at5_stuck");
        using var stub = new StubClaude("stream-json-stuck-loop.jsonl");
        var options = e2e.Options(stub.Settings());
        using var timeout = Timeout();
        var ct = timeout.Token;

        var outcome = await FactoryRunAsync(options, storyId, ct);

        Assert.Equal(WorkState.Escalated, outcome.State);
        var history = await e2e.HistoryAsync(storyId, ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        var stuck = history.Where(e => e.Step == RunPipeline.Steps.Stuck).ToList();
        Assert.Equal(2, stuck.Count);
        Assert.All(stuck, e =>
        {
            Assert.Equal(StepOutcome.Failed, e.Outcome);
            Assert.Contains("repeating tool calls: Edit", e.Detail);
        });
        Assert.Single(history, e => e.Step == RunPipeline.Steps.StuckRetry);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Escalated], history.Where(e => e.Step is null).Select(e => e.State));
        // Both sessions were stopped by the pause hook at a tool call, neither ran to its end, and each was a fresh session.
        var runs = stub.Runs();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(StubClaude.Stopped, r.Ending));
        Assert.Equal(stuck.Select(e => e.ClaudeSessionId), runs.Select(r => r.Session));
        Assert.Empty(await E2e.PullRequestsAsync(options.DefaultRepo, storyId, ct));
    }

    /// <summary>
    /// AT5: a silent worker is not interrupted. The stub replays <c>stream-json-silence-progress.jsonl</c> with a long silence
    /// (<c>FACTORY_E2E_SILENCE_SECONDS</c>, default 120) before the slow test run's result, then makes progress and ends: no
    /// stuck row, the session ran to its end and the ledger records it done.
    /// </summary>
    [Fact]
    public async Task A_silent_worker_that_then_makes_progress_is_not_interrupted()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_SILENT_STORY", "a To Do story for the stub silent worker (it makes no change, so it does not merge)");
        var silence = int.TryParse(Environment.GetEnvironmentVariable("FACTORY_E2E_SILENCE_SECONDS"), out var s) && s > 0 ? s : 120;
        await using var e2e = await E2e.StartAsync("df_e2e_p2at5_silent");
        using var stub = new StubClaude("stream-json-silence-progress.jsonl", silenceBeforeSecondResult: silence);
        var options = e2e.Options(stub.Settings());
        using var timeout = Timeout();
        var ct = timeout.Token;

        await FactoryRunAsync(options, storyId, ct);

        var history = await e2e.HistoryAsync(storyId, ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.DoesNotContain(history, e => e.Step == RunPipeline.Steps.Stuck);
        var run = Assert.Single(stub.Runs());
        Assert.Equal(StubClaude.Completed, run.Ending);
        Assert.Equal(run.Session, Assert.Single(history, e => e.Step == RunPipeline.Steps.WorkerDone).ClaudeSessionId);
    }
}

/// <summary>
/// A stub Claude Code CLI for the stuck acceptance tests: a bash script replaying one recorded stream-json transcript (from the unit
/// tests' fixtures) under a fresh session id per run, one line a second, honouring the factory's pause hook as the real CLI does
/// (the tool call streams, then the hook ends the session with <c>terminal_reason: hook_stopped</c>). Each run appends
/// <c>&lt;session&gt; &lt;ending&gt;</c> to its log. It answers <c>--version</c>. Deleted on dispose.
/// </summary>
internal sealed class StubClaude : IDisposable
{
    public const string Stopped = "stopped-at-tool-boundary";
    public const string Completed = "completed";

    private readonly string _dir;
    private readonly string _log;

    public StubClaude(string fixture, int? silenceBeforeSecondResult = null)
    {
        _dir = Directory.CreateTempSubdirectory("df-e2e-stub-claude-").FullName;
        _log = Path.Combine(_dir, "runs.log");
        var transcript = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var lines = File.ReadAllLines(transcript);
        // The silence comes before the second tool result (the slow test run's), as the unit test replays it.
        var silenceAt = silenceBeforeSecondResult is null ? -1
            : lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("\"tool_result\"", StringComparison.Ordinal)).Select(x => x.i).Skip(1).First();
        Extractor = Path.Combine(_dir, "pause-flag.pl");
        File.WriteAllText(Extractor, PauseFlagExtractor);
        Script = Path.Combine(_dir, "claude");
        File.WriteAllText(Script, $$"""
            #!/bin/bash
            # [dark-factory e2e] stub Claude Code CLI (sc-25391): replays {{fixture}} under a fresh session id, honouring the pause hook.
            if [ "$1" = "--version" ]; then echo "0.0.0 (dark-factory e2e stub)"; exit 0; fi
            settings=""; previous=""
            for arg in "$@"; do
              if [ "$previous" = "--settings" ]; then settings="$arg"; fi
              previous="$arg"
            done
            flag=$(/usr/bin/perl '{{Extractor}}' "$settings")
            session=$( { /usr/bin/uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid; } | tr 'A-Z' 'a-z')
            recorded=$(head -n 1 '{{transcript}}' | sed -n 's/.*"session_id": "\([^"]*\)".*/\1/p')
            n=0
            while IFS= read -r line || [ -n "$line" ]; do
              if [ "$n" -eq {{silenceAt}} ]; then sleep {{silenceBeforeSecondResult ?? 0}}; fi
              printf '%s\n' "${line//$recorded/$session}"
              n=$((n+1))
              case "$line" in
                *'"type": "tool_use"'*)
                  sleep 1
                  if [ -n "$flag" ] && [ -e "$flag" ]; then
                    echo "$session {{Stopped}}" >> '{{_log}}'
                    printf '{"type": "result", "subtype": "success", "is_error": false, "result": "stopped by the pause hook", "session_id": "%s", "terminal_reason": "hook_stopped"}\n' "$session"
                    exit 0
                  fi;;
                *) sleep 1;;
              esac
            done < '{{transcript}}'
            echo "$session {{Completed}}" >> '{{_log}}'

            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Prints the pause flag path of a <c>--settings</c> value (its first argument) as <see cref="ClaudeWorker.BuildPauseSettings"/>
    /// writes it, or nothing. It parses the JSON (core JSON::PP): the serializer escapes the hook command's quotes
    /// (<c>'</c>), so no text match on the raw value can find the path. /usr/bin/perl is what the worker launch needs anyway.
    /// </summary>
    public const string PauseFlagExtractor = """
        use strict; use warnings; use JSON::PP;
        binmode STDOUT, ':encoding(UTF-8)';
        my $settings = eval { JSON::PP->new->decode($ARGV[0] // '') } or exit 0;
        for my $matcher (@{ $settings->{hooks}{PreToolUse} || [] }) {
            for my $hook (@{ $matcher->{hooks} || [] }) {
                if (($hook->{command} // '') =~ /\[ -e '([^']*)' \]/) { print $1; exit 0; }
            }
        }

        """;

    /// <summary>The script's path.</summary>
    public string Script { get; }

    /// <summary>The pause flag extractor the script runs (<see cref="PauseFlagExtractor"/>).</summary>
    public string Extractor { get; }

    /// <summary>The settings that make the factory run this stub, as the owner (no sandbox: the stub is the only process run).</summary>
    public Dictionary<string, string?> Settings() => new() { ["Worker:ClaudePath"] = Script, ["Worker:RunAs"] = "none" };

    /// <summary>Each run so far: its session and how it ended.</summary>
    public List<(string Session, string Ending)> Runs() =>
        File.Exists(_log)
            ? [.. File.ReadAllLines(_log).Where(l => l.Length > 0).Select(l => l.Split(' ', 2)).Select(p => (p[0], p[1]))]
            : [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
