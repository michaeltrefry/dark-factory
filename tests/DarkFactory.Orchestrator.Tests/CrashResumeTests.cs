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
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pid in new[] { Pids, FirstRun }.Where(File.Exists).SelectMany(File.ReadAllLines))
        {
            TryKill(int.Parse(pid));
        }
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private string Pids => Path.Combine(_dir, "worker.pids");
    private string Invocations => Path.Combine(_dir, "invocations.log");
    /// <summary>The first (orphaned) worker's pid and its child's.</summary>
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
            await WaitForAsync(async () => (await Rows()).Any(r => r.Step == RunPipeline.Steps.Session), first, "session checkpoint", ct);
            await WaitForAsync(async () => (await Events()).Count == 1, first, "stored init event", ct);
            first.Kill(); // SIGKILL
            await first.WaitForExitAsync(ct);
        }
        var crashed = await Rows();
        Assert.Equal(WorkState.Implement, crashed.Last(r => r.Step is null).State);
        Assert.Equal(Session, crashed.Single(r => r.Step == RunPipeline.Steps.Session).ClaudeSessionId);
        var orphan = File.ReadAllLines(FirstRun).Select(int.Parse).ToArray();
        Assert.Equal(orphan[0].ToString(), crashed.Single(r => r.Step == RunPipeline.Steps.WorkerStarted).Detail);
        Assert.All(orphan, pid => Assert.True(IsAlive(pid), $"orphan {pid} should outlive the orchestrator"));

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
        Assert.Equal(["worker-started", "session", "orphan-killed", "worker-started", "worker-done", "pushed"],
            rows.Where(r => r.Step is not null).Select(r => r.Step));
        Assert.Equal($"pid {orphan[0]}", rows.Single(r => r.Step == RunPipeline.Steps.OrphanKilled).Detail);
        Assert.Equal(Session, rows[^1].ClaudeSessionId);
        var pr = Assert.Single(File.ReadAllLines(Path.Combine(_dir, "prs.log")));
        Assert.Equal($"factory/sc-{Story}\thttps://github.com/acme/widgets/pull/1", pr);
        // Both the pre-crash and the resumed work were pushed from the one worktree.
        var files = Git(origin, "ls-tree", "--name-only", $"factory/sc-{Story}");
        Assert.Contains("before-crash.txt", files);
        Assert.Contains("after-resume.txt", files);
        Assert.False(File.Exists(Path.Combine(_dir, "comments.log")));

        // The resumed run appended to the same session: no gap, no duplicate (E7).
        Assert.Equal([(1L, "system"), (2L, "system"), (3L, "result")], (await Events()).Select(e => (e.Sequence, e.Type)));
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger)))
        {
            var session = await db.WorkerSessions.SingleAsync();
            Assert.Equal((Session, 1, "succeeded"), (session.ClaudeSessionId, session.Attempt, session.ExitStatus));
        }

        // Run 3: a parked item redoes nothing.
        using (var third = StartHost())
        {
            await third.WaitForExitAsync(ct);
            Assert.Equal(0, third.ExitCode);
        }
        Assert.Equal(3, File.ReadAllLines(Invocations).Length);
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "prs.log")));
        Assert.Equal(rows.Count, (await Rows()).Count);
    }

    private async Task<List<LedgerEntry>> Rows()
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger));
        if (!(await db.Database.GetAppliedMigrationsAsync()).Any())
        {
            return [];
        }
        return await db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<List<SessionEvent>> Events()
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_ledger));
        if (!(await db.Database.GetAppliedMigrationsAsync()).Any())
        {
            return [];
        }
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
            echo $$ >> "{{Pids}}"
            if printf '%s ' "$@" | grep -q -- '--resume {{Session}}'; then
              alive=""
              for p in $(cat "{{FirstRun}}"); do kill -0 "$p" 2>/dev/null && alive="$alive $p"; done
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
            printf '%s\n%s\n' $$ $! > "{{FirstRun}}"
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

    private static bool IsAlive(int pid)
    {
        using var p = Process.Start(new ProcessStartInfo("kill", ["-0", pid.ToString()]) { RedirectStandardError = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
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
