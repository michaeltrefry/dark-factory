using System.Diagnostics;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// E3 crash resume, end to end with real processes: the orchestrator (DarkFactory.CrashHost)
/// is SIGKILLed while its worker runs, then restarted at once against the same Postgres ledger.
/// The orphaned worker is silent, so nothing (no SIGPIPE) ends it but the restarted run.
/// Needs the compose Postgres on localhost:5434 (CI provides it as a service).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class CrashResumeTests : IAsyncLifetime
{
    private const int Story = 4242;
    private const string Session = "sess-crash-1";
    private readonly string _dir = Directory.CreateTempSubdirectory("df-crash-").FullName;
    private TempPostgresDatabase? _db;
    private string _ledger = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_crash");
        _ledger = _db.ConnectionString;
        // Fully migrated before any host starts: polling while a host migrates would see a half-built
        // schema (e.g. ledger_entries without "Step", 42703), since each migration commits on its own.
        await LedgerMigrations.MigrateAsync(_ledger, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        // Each recorded with its start time by the process itself, so a pid reused since is never signalled.
        foreach (var record in new[] { Pids, FirstRun }.Where(File.Exists).SelectMany(File.ReadAllLines))
        {
            OwnProcess.Parse(record).KillIfStillRunning();
        }
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private string Pids => Path.Combine(_dir, "worker.pids");
    private string Invocations => Path.Combine(_dir, "invocations.log");
    /// <summary>The first (orphaned) worker's and its child's "pid|start" records.</summary>
    private string FirstRun => Path.Combine(_dir, "first-run.pids");

    [Fact]
    public async Task Killed_during_implement_resumes_the_same_claude_session_and_opens_exactly_one_pr()
    {
        var ct = TestContext.Current.CancellationToken;
        var origin = SeedOrigin();
        WriteFakeClaude();

        // Run 1: the worker starts, streams its session id, then works silently; the orchestrator dies.
        using (var first = StartHost())
        {
            var host = OwnProcess.Of(first);
            // Killed as soon as the ledger names the session, whether or not its first event is stored yet.
            await WaitForAsync(async () => (await Rows()).Any(r => r.Step == RunPipeline.Steps.Session), first, "session checkpoint", ct);
            host.KillIfStillRunning(); // SIGKILL
            await first.WaitForExitAsync(ct);
        }
        var crashed = await Rows();
        Assert.Equal(WorkState.Implement, crashed.Last(r => r.Step is null).State);
        Assert.Equal(Session, crashed.Single(r => r.Step == RunPipeline.Steps.Session).ClaudeSessionId);
        var orphan = File.ReadAllLines(FirstRun).Select(OwnProcess.Parse).ToArray();
        Assert.Equal(orphan[0].Pid.ToString(), crashed.Single(r => r.Step == RunPipeline.Steps.WorkerStarted).Detail);
        Assert.All(orphan, o => Assert.True(o.IsAlive, $"orphan {o.Pid} should outlive the orchestrator"));

        // Run 2: restart against the same ledger at once; it must stop the orphan before resuming.
        using (var second = StartHost())
        {
            await second.WaitForExitAsync(ct);
            Assert.True(second.ExitCode == 0, await second.StandardOutput.ReadToEndAsync(ct) + await second.StandardError.ReadToEndAsync(ct));
        }

        // The resumed claude checked, as it started, that the first one (and its child) were already dead.
        Assert.Equal(["fresh", "orphans gone", $"resume {Session}"], File.ReadAllLines(Invocations));
        var rows = await Rows();
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], rows.Where(r => r.Step is null).Select(r => r.State));
        // The kill may land before or after the first run's model-class row; the resumed run records its own (E8, an unestimated story: mid).
        Assert.Equal(["claimed", "worker-started", "session", "orphan-killed", "worker-started", "worker-done", "pushed", "linked"],
            rows.Where(r => r.Step is not null && r.Step != RunPipeline.Steps.ModelClass).Select(r => r.Step));
        var resumedClass = rows.SkipWhile(r => r.Step != RunPipeline.Steps.OrphanKilled).Single(r => r.Step == RunPipeline.Steps.ModelClass);
        Assert.Equal((Session, "mid"), (resumedClass.ClaudeSessionId, resumedClass.Detail));
        Assert.Equal($"pid {orphan[0].Pid}", rows.Single(r => r.Step == RunPipeline.Steps.OrphanKilled).Detail);
        Assert.Equal(Session, rows[^1].ClaudeSessionId);
        var pr = Assert.Single(File.ReadAllLines(Path.Combine(_dir, "prs.log")));
        Assert.Equal($"factory/sc-{Story}\thttps://github.com/acme/widgets/pull/1", pr);
        // Both the pre-crash and the resumed work were pushed from the one worktree.
        var files = Git(origin, "ls-tree", "--name-only", $"factory/sc-{Story}");
        Assert.Contains("before-crash.txt", files);
        Assert.Contains("after-resume.txt", files);
        Assert.False(File.Exists(Path.Combine(_dir, "comments.log")));

        // The resumed run appended to the same session row: no gap, no duplicate (E7). The first run's init
        // line is stored unless the kill beat its queued write (a lost tail, never a hole).
        var events = (await Events()).Select(e => (e.Sequence, e.Type)).ToList();
        Assert.True(events.SequenceEqual([(1L, "system"), (2L, "system"), (3L, "result")]) || events.SequenceEqual([(1L, "system"), (2L, "result")]),
            string.Join(", ", events));
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger)))
        {
            var session = await db.WorkerSessions.SingleAsync();
            Assert.Equal((Session, 1, "succeeded"), (session.ClaudeSessionId, session.Attempt, session.ExitStatus));
        }

        // Run 3: a parked item redoes nothing; the story is told once why nothing happens.
        using (var third = StartHost())
        {
            await third.WaitForExitAsync(ct);
            Assert.Equal(0, third.ExitCode);
        }
        Assert.Equal(3, File.ReadAllLines(Invocations).Length);
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "prs.log")));
        var after = await Rows();
        Assert.Equal(rows.Count + 1, after.Count);
        Assert.Equal(RunPipeline.Steps.HeldNotice, after[^1].Step);
        Assert.Contains("is Review in the factory ledger", Assert.Single(File.ReadAllLines(Path.Combine(_dir, "comments.log"))));
    }

    private async Task<List<LedgerEntry>> Rows()
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger));
        return await db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<List<SessionEvent>> Events()
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger));
        return await db.SessionEvents.OrderBy(e => e.Sequence).ToListAsync();
    }

    private Process StartHost()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "DarkFactory.CrashHost.dll"), _ledger, _dir, Story.ToString() })
        {
            psi.ArgumentList.Add(a);
        }
        return Process.Start(psi)!;
    }

    private string SeedOrigin()
    {
        var origin = Path.Combine(_dir, "origin.git");
        var seed = Path.Combine(_dir, "seed");
        Git(_dir, "init", "--bare", "-b", "main", origin);
        Git(_dir, "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", origin, "main");
        return origin;
    }

    /// <summary>
    /// A stand-in for `claude -p --output-format stream-json`. Fresh: writes a file, starts a
    /// long-running child (a "tool"), announces its session, then goes silent. With --resume:
    /// records whether the first run's processes are dead, then finishes the work.
    /// </summary>
    private void WriteFakeClaude()
    {
        var script = Path.Combine(_dir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo {{OwnProcess.ShellRecord("$$")}} >> "{{Pids}}"
            if printf '%s ' "$@" | grep -q -- '--resume {{Session}}'; then
              alive=""
              while IFS='|' read -r p start; do
                cur=$(/bin/ps -o lstart= -p "$p" | /usr/bin/awk '{ $1 = $1; print }')
                if [ -n "$cur" ] && [ "$cur" = "$start" ]; then alive="$alive $p"; fi
              done < "{{FirstRun}}"
              if [ -z "$alive" ]; then echo "orphans gone" >> "{{Invocations}}"; else echo "orphans alive:$alive" >> "{{Invocations}}"; fi
              echo "resume {{Session}}" >> "{{Invocations}}"
              echo '{"type":"system","subtype":"init","session_id":"{{Session}}"}'
              echo done > after-resume.txt
              echo '{"type":"result","subtype":"success","is_error":false,"session_id":"{{Session}}"}'
              exit 0
            fi
            echo fresh >> "{{Invocations}}"
            echo wip > before-crash.txt
            sleep 600 &
            { echo {{OwnProcess.ShellRecord("$$")}}; echo {{OwnProcess.ShellRecord("$!")}}; } > "{{FirstRun}}"
            echo '{"type":"system","subtype":"init","session_id":"{{Session}}"}'
            wait
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, Process? host, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            if (host is { HasExited: true })
            {
                Assert.Fail($"host exited ({host.ExitCode}) before {what}: {await host.StandardOutput.ReadToEndAsync(ct)} {await host.StandardError.ReadToEndAsync(ct)}");
            }
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(100, ct);
        }
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
}
