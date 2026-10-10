using System.Diagnostics;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Controls end to end with real processes: the real <see cref="ClaudeWorker"/> runs a fake <c>claude</c> that works
/// in tool calls and, before each, runs the PreToolUse hook the worker passed in <c>--settings</c> (as Claude Code
/// does), stopping the session when the hook says <c>continue: false</c>. The ledger, controls, run locks and session
/// capture are on Postgres; git is a local bare "origin". Needs the compose Postgres (CI provides it).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ControlProcessTests : IAsyncLifetime
{
    private const int StoryId = 4343;
    private const string Session = "sess-ctl-process";
    private static readonly RepoRef Repo = new("acme", "widgets");
    private readonly string _dir = Directory.CreateTempSubdirectory("df-ctl-").FullName;
    private TempPostgresDatabase? _db;
    private string _ledger = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_ctl");
        _ledger = _db.ConnectionString;
        await LedgerMigrations.MigrateAsync(_ledger, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private string Log => Path.Combine(_dir, "tools.log");
    private string Origin => Path.Combine(_dir, "origin.git");
    private LedgerDbContextFactory Contexts => new(LedgerDbContext.PostgresOptions(_ledger));
    private IControls Controls => new LedgerControls(Contexts, TimeProvider.System);
    private readonly FakeWorkSource _stories = new(new WorkStory(StoryId, "Add files", "Add tool files.", "feature", $"https://app.shortcut.com/test/story/{StoryId}"));
    private readonly FakePullRequests _prs = new();

    private async Task<RunOutcome> RunAsync(LedgerDbContext db, CancellationToken ct)
    {
        var pipeline = new RunPipeline(
            _stories,
            new WorkLedger(db, TimeProvider.System),
            new PostgresRunLocks(_ledger),
            new GitWorkspace(Path.Combine(_dir, "work"), _ => Origin, (_, _) => Task.FromResult<string?>(null)),
            new ClaudeWorker(Path.Combine(_dir, "fake-claude.sh"), new Uri("http://127.0.0.1:9/"), "rk_test", WorkerAuth.ClaudeLogin,
                TimeSpan.FromMinutes(2), pauseFlagDirectory: Path.Combine(_dir, "controls")),
            _prs,
            Repo,
            TextWriter.Null,
            new SessionRecorder(Contexts, new NoCost(), TimeProvider.System, TextWriter.Null, costRetryDelays: []),
            controls: Controls,
            controlPollInterval: TimeSpan.FromMilliseconds(20));
        return await pipeline.RunAsync(StoryId, ct);
    }

    private ControlActions Actions() => new(Controls, Contexts, new Stops(this));

    private sealed class Stops(ControlProcessTests t) : IItemStops
    {
        public async Task<ControlResult> StopAsync(int storyId, CancellationToken ct)
        {
            await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(t._ledger));
            return await new ItemStopper(t._stories, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(t._ledger), t._prs, t.Controls, TextWriter.Null)
                .StopAsync(storyId, ct);
        }
    }

    [Fact]
    public async Task Pause_stops_the_worker_after_its_current_tool_call_continue_resumes_the_same_session_and_stop_drafts_the_pr()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedOrigin();
        WriteFakeClaude();
        var scope = ControlScope.Item(Shortcut.StoryId.Format(StoryId));

        // Implement: the worker makes tool calls until the item is paused.
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger)))
        {
            var run = RunAsync(db, ct);
            await WaitForAsync(() => ToolLog().Count(l => l.StartsWith("end ")) >= 3, "three tool calls", ct);
            await Actions().PauseAsync(scope, "tester", ct);
            var paused = await run.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal(WorkState.Paused, paused.State);
        }
        var log = ToolLog();
        // The tool call in flight finished; the next one was denied by the hook and nothing ran after it.
        Assert.StartsWith("denied t", log[^1]);
        var started = log.Where(l => l.StartsWith("start ")).Select(l => l[6..]).ToList();
        Assert.Equal(started, log.Where(l => l.StartsWith("end ")).Select(l => l[4..]));
        Assert.Equal($"t{started.Count + 1}", log[^1]["denied ".Length..]);
        var worktree = Path.Combine(_dir, "work", "worktrees", "acme", "widgets", $"factory-sc-{StoryId}");
        Assert.True(File.Exists(Path.Combine(worktree, "tool-t1.txt"))); // worktree kept for Continue
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger)))
        {
            var last = await db.LedgerEntries.Where(e => e.Step == null).OrderBy(e => e.Id).LastAsync(ct);
            Assert.Equal((WorkState.Paused, RunPipeline.UserPaused), (last.State, last.Detail));
            Assert.Equal("paused", (await db.WorkerSessions.SingleAsync(ct)).ExitStatus);
            Assert.Empty(await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, Controls));
        }

        // Continue: the intake loop picks it up and the worker resumes its own session.
        await Actions().ContinueAsync(scope, "tester", ct);
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger)))
        {
            Assert.Equal([StoryId], await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, Controls));
            var resumed = await RunAsync(db, ct);
            Assert.True(resumed.Succeeded, resumed.Error);
            Assert.Equal(WorkState.Review, resumed.State);

            Assert.Contains($"resume {Session}", ToolLog());
            var session = await db.WorkerSessions.SingleAsync(ct); // one session row, continued
            Assert.Equal((Session, "succeeded"), (session.ClaudeSessionId, session.ExitStatus));
            var events = await db.SessionEvents.OrderBy(e => e.Sequence).ToListAsync(ct);
            Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence)); // gapless across the pause
            Assert.Equal(2, events.Count(e => e.Type == "system")); // first run's init, then the resumed run's
            Assert.Equal(1, events.Count(e => e.Type == "result" && e.Payload.Contains("hook_stopped")));
            Assert.Equal(1, db.LedgerEntries.Count(e => e.Step == RunPipeline.Steps.Session));
        }
        Assert.Contains("tool-t1.txt", Git(_dir, "--git-dir", Origin, "ls-tree", "--name-only", $"factory/sc-{StoryId}"));

        // Stop: Cancelled, the PR a draft again, the branch intact, the story told and back in the Backlog.
        var stop = await Actions().StopAsync(scope, "tester", ct);
        Assert.True(stop.Ok, stop.Message);
        Assert.Contains("stopped", stop.Message);
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger)))
        {
            Assert.Equal(WorkState.Cancelled, (await db.WorkItems.SingleAsync(ct)).State);
            Assert.Equal(PrUrl, (await db.LedgerEntries.SingleAsync(e => e.Step == RunPipeline.Steps.PrsDrafted, ct)).Detail);
        }
        Assert.Equal([$"factory/sc-{StoryId}"], _prs.Drafted);
        Assert.Contains($"state {StoryId} Stopped", _stories.Writes);
        Assert.Contains("tool-t1.txt", Git(_dir, "--git-dir", Origin, "ls-tree", "--name-only", $"factory/sc-{StoryId}"));
        Assert.Null(await Controls.GetAsync(scope, ct));
    }

    [Fact]
    public async Task Stop_kills_a_running_worker_at_once_cancels_the_item_and_removes_its_worktree()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedOrigin();
        WriteFakeClaude();

        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger));
        var run = RunAsync(db, ct);
        await WaitForAsync(() => ToolLog().Count(l => l.StartsWith("end ")) >= 2, "two tool calls", ct);
        await Actions().StopAsync(ControlScope.Item(Shortcut.StoryId.Format(StoryId)), "tester", ct);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Cancelled, outcome.State);
        var pid = int.Parse((await db.LedgerEntries.AsNoTracking().SingleAsync(e => e.Step == RunPipeline.Steps.WorkerStarted, ct)).Detail!);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid)); // killed, not waited for
        Assert.DoesNotContain(ToolLog(), l => l.StartsWith("denied"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "work", "worktrees", "acme", "widgets", $"factory-sc-{StoryId}")));
        Assert.Equal("stopped", (await db.WorkerSessions.AsNoTracking().SingleAsync(ct)).ExitStatus);
        Assert.Contains($"state {StoryId} Stopped", _stories.Writes);
        var settled = ToolLog().Count;
        await Task.Delay(300, ct);
        Assert.Equal(settled, ToolLog().Count); // no tool runs after the stop
    }

    private List<string> ToolLog() => File.Exists(Log) ? File.ReadAllLines(Log).ToList() : [];

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(20, ct);
        }
    }

    /// <summary>
    /// A stand-in for <c>claude -p --output-format stream-json</c> working in tool calls. Before each call it runs the
    /// PreToolUse hook from <c>--settings</c>; on <c>continue: false</c> it denies the call and ends the session as
    /// Claude Code does (result success, terminal_reason hook_stopped). Fresh, it works until stopped; resumed, it
    /// makes two more calls and finishes. Each call writes a different file (named in its input), so the stuck detector
    /// (sc-25388) never takes the calls for a loop, however many run before the pause lands.
    /// </summary>
    private void WriteFakeClaude()
    {
        var script = Path.Combine(_dir, "fake-claude.sh");
        SandboxSupport.ExecutableAt(script, $$$"""
            #!/bin/sh
            settings=""; resume=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --settings) settings="$2"; shift 2 ;;
                --resume) resume="$2"; shift 2 ;;
                *) shift ;;
              esac
            done
            hook=$(printf '%s' "$settings" | python3 -c 'import json,sys; print(json.load(sys.stdin)["hooks"]["PreToolUse"][0]["hooks"][0]["command"])')
            S={{{Session}}}
            echo "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"$S\"}"
            if [ -n "$resume" ]; then echo "resume $resume" >> "{{{Log}}}"; prefix=r; max=2; else prefix=t; max=2000; fi
            n=0
            while [ $n -lt $max ]; do
              n=$((n+1)); id="$prefix$n"
              echo "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"$id\",\"name\":\"Write\",\"input\":{\"file_path\":\"tool-$id.txt\"}}]},\"session_id\":\"$S\"}"
              decision=$(sh -c "$hook")
              if printf '%s' "$decision" | grep -q '"continue":false'; then
                echo "denied $id" >> "{{{Log}}}"
                echo "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"$id\",\"is_error\":true,\"content\":\"PreToolUse hook denied\"}]},\"session_id\":\"$S\"}"
                echo "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"terminal_reason\":\"hook_stopped\",\"session_id\":\"$S\"}"
                exit 0
              fi
              echo "start $id" >> "{{{Log}}}"
              sleep 0.05
              echo "$id" > "tool-$id.txt"
              echo "end $id" >> "{{{Log}}}"
              echo "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"$id\",\"content\":\"ok\"}]},\"session_id\":\"$S\"}"
            done
            echo "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"$S\"}"
            """);
    }

    private void SeedOrigin()
    {
        var seed = Path.Combine(_dir, "seed");
        Git(_dir, "init", "--bare", "-b", "main", Origin);
        Git(_dir, "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", Origin, "main");
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    private sealed class NoCost : ISessionCostSource
    {
        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionCost?>(null);
    }
}
