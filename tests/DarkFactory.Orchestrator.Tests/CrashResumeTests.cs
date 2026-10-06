using System.Diagnostics;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// E3 crash resume, end to end with real processes: the orchestrator (DarkFactory.CrashHost)
/// is SIGKILLed while its worker runs, then restarted against the same Postgres ledger.
/// Needs the compose Postgres on localhost:5434 (CI provides it as a service).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class CrashResumeTests : IAsyncLifetime
{
    private const int Story = 4242;
    private const string Session = "sess-crash-1";
    private readonly string _dir = Directory.CreateTempSubdirectory("df-crash-").FullName;
    private readonly string _database = $"df_crash_{Guid.NewGuid():N}";
    private NpgsqlConnectionStringBuilder _admin = null!;
    private string _ledger = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = new NpgsqlConnectionStringBuilder(FactoryOptions.LoadConfiguration().GetLedgerConnectionString());
        try
        {
            await using var conn = new NpgsqlConnection(_admin.ConnectionString);
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", conn);
            await create.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException && Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.Skip($"Ledger Postgres not reachable ({ex.Message}); run `docker compose up -d`.");
        }
        _ledger = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _database }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pid in File.Exists(Pids) ? File.ReadAllLines(Pids) : [])
        {
            TryKill(int.Parse(pid));
        }
        if (_ledger is null)
        {
            return;
        }
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin.ConnectionString);
        await conn.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync();
    }

    private string Pids => Path.Combine(_dir, "worker.pids");
    private string Invocations => Path.Combine(_dir, "invocations.log");

    [Fact]
    public async Task Killed_during_implement_resumes_the_same_claude_session_and_opens_exactly_one_pr()
    {
        var ct = TestContext.Current.CancellationToken;
        var origin = SeedOrigin();
        WriteFakeClaude();

        // Run 1: the worker starts, streams its session id, then keeps working until the orchestrator dies.
        using (var first = StartHost())
        {
            await WaitForAsync(async () => (await Rows()).Any(r => r.Step == RunPipeline.Steps.Session), first, "session checkpoint", ct);
            first.Kill(); // SIGKILL
            await first.WaitForExitAsync(ct);
        }
        var crashed = await Rows();
        Assert.Equal(WorkState.Implement, crashed.Last(r => r.Step is null).State);
        Assert.Equal(Session, crashed.Single(r => r.Step == RunPipeline.Steps.Session).ClaudeSessionId);
        // The orphaned worker dies on its next write to the dead orchestrator's pipe.
        var workerPid = int.Parse(File.ReadAllLines(Pids).Single());
        await WaitForAsync(() => Task.FromResult(!IsAlive(workerPid)), null, "orphaned worker to exit", ct);

        // Run 2: restart against the same ledger.
        using (var second = StartHost())
        {
            await second.WaitForExitAsync(ct);
            Assert.True(second.ExitCode == 0, await second.StandardOutput.ReadToEndAsync(ct) + await second.StandardError.ReadToEndAsync(ct));
        }

        Assert.Equal(["fresh", $"resume {Session}"], File.ReadAllLines(Invocations));
        var rows = await Rows();
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], rows.Where(r => r.Step is null).Select(r => r.State));
        Assert.Equal(["session", "worker-done", "pushed"], rows.Where(r => r.Step is not null).Select(r => r.Step));
        Assert.Equal(Session, rows[^1].ClaudeSessionId);
        var pr = Assert.Single(File.ReadAllLines(Path.Combine(_dir, "prs.log")));
        Assert.Equal($"factory/sc-{Story}\thttps://github.com/acme/widgets/pull/1", pr);
        // Both the pre-crash and the resumed work were pushed from the one worktree.
        var files = Git(origin, "ls-tree", "--name-only", $"factory/sc-{Story}");
        Assert.Contains("before-crash.txt", files);
        Assert.Contains("after-resume.txt", files);
        Assert.False(File.Exists(Path.Combine(_dir, "comments.log")));

        // Run 3: a parked item redoes nothing.
        using (var third = StartHost())
        {
            await third.WaitForExitAsync(ct);
            Assert.Equal(0, third.ExitCode);
        }
        Assert.Equal(2, File.ReadAllLines(Invocations).Length);
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
    /// A stand-in for `claude -p --output-format stream-json`. Fresh: announces its session,
    /// writes a file, then streams progress forever. With --resume: finishes the work.
    /// </summary>
    private void WriteFakeClaude()
    {
        var script = Path.Combine(_dir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo $$ >> "{{Pids}}"
            if printf '%s ' "$@" | grep -q -- '--resume {{Session}}'; then
              echo "resume {{Session}}" >> "{{Invocations}}"
              echo '{"type":"system","subtype":"init","session_id":"{{Session}}"}'
              echo done > after-resume.txt
              echo '{"type":"result","subtype":"success","is_error":false,"session_id":"{{Session}}"}'
              exit 0
            fi
            echo fresh >> "{{Invocations}}"
            echo wip > before-crash.txt
            echo '{"type":"system","subtype":"init","session_id":"{{Session}}"}'
            while true; do
              echo '{"type":"assistant","message":{"content":[]},"session_id":"{{Session}}"}' || exit 141
              sleep 0.2
            done
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
