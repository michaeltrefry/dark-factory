using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// AT3, live: <c>factory work</c> as a child process (the real CLI, the built orchestrator) against a throwaway ledger and
/// work root (<see cref="E2e"/>). Once the ledger names the Claude session, the test SIGKILLs that child — its own pid
/// only, never its process tree or anything else — and starts it again. The restarted host resumes the same session and
/// the item reaches Review with exactly one PR. Runbook: docs/acceptance.md.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class CrashRestartTests
{
    [Fact]
    public async Task Factory_work_killed_mid_implement_resumes_the_same_session_and_opens_exactly_one_pr()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_CRASH_STORY", "a To Do bug story in the watch scope whose fix takes the worker a few minutes");
        var scope = E2e.WatchScope();
        await using var e2e = await E2e.StartAsync("df_e2e_at3");
        var ct = TestContext.Current.CancellationToken;
        var environment = e2e.ChildEnvironment(scope, FreePort());

        var first = StartWork(environment);
        var firstOwn = OwnProcess.Of(first);
        Process? second = null;
        OwnProcess? secondOwn = null;
        try
        {
            await E2e.WaitForAsync("the session checkpoint", TimeSpan.FromMinutes(20), async () =>
            {
                Assert.False(first.HasExited, $"factory work exited early with {(first.HasExited ? first.ExitCode : 0)}");
                return (await e2e.HistoryAsync(storyId, ct)).Any(e => e.Step == RunPipeline.Steps.Session);
            }, ct);
            var before = await e2e.HistoryAsync(storyId, ct);
            Assert.DoesNotContain(before, e => e.Step is null && e.State == WorkState.Review);
            var session = before.Last(e => e.Step == RunPipeline.Steps.Session).ClaudeSessionId;
            var workerPid = int.Parse(before.Last(e => e.Step == RunPipeline.Steps.WorkerStarted).Detail!);
            var killedAfter = before[^1].Id;

            firstOwn.KillIfStillRunning(); // SIGKILL to this child's own pid only
            await first.WaitForExitAsync(ct);

            second = StartWork(environment);
            secondOwn = OwnProcess.Of(second);
            await FactoryWorkTests.WaitForStateAsync(e2e, storyId, WorkState.Review, TimeSpan.FromMinutes(45), ct);
            var history = await e2e.HistoryAsync(storyId, ct);

            // The first run's worker did not keep going alongside the resumed one: stopped as an orphan, or already gone.
            Assert.True(history.Any(e => e.Id > killedAfter && e.Step == RunPipeline.Steps.OrphanKilled) || !IsRunning(workerPid),
                $"worker pid {workerPid} of the killed run is neither recorded as stopped nor gone");
            // The same Claude session throughout, and the resumed run carried it to Review.
            Assert.All(history.Where(e => e.Step == RunPipeline.Steps.Session), e => Assert.Equal(session, e.ClaudeSessionId));
            Assert.Equal(session, history.Last(e => e.Step is null).ClaudeSessionId);
            await using (var db = e2e.Db())
            {
                Assert.Equal([session], await db.WorkerSessions.AsNoTracking().Select(s => s.ClaudeSessionId).Distinct().ToListAsync(ct));
            }
            // Exactly one PR for the branch.
            await using (var db = e2e.Db())
            {
                var repo = RepoRef.Parse((await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(storyId), ct)).Repo);
                Assert.Single(await E2e.PullRequestsAsync(repo, storyId, ct));
            }
        }
        finally
        {
            // Only the children this test started, each by its own pid.
            foreach (var (child, own) in new[] { (first, (OwnProcess?)firstOwn), (second, secondOwn) })
            {
                if (child is { HasExited: false } && own is not null)
                {
                    own.KillIfStillRunning();
                    await child.WaitForExitAsync(CancellationToken.None);
                }
                child?.Dispose();
            }
        }
    }

    /// <summary><c>dotnet DarkFactory.Orchestrator.dll work</c> with the test's settings; its output goes to the test log.</summary>
    private static Process StartWork(IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(typeof(FactoryRunner).Assembly.Location);
        psi.ArgumentList.Add("work");
        foreach (var (name, value) in environment)
        {
            psi.Environment[name] = value;
        }
        var process = Process.Start(psi)!;
        // Drained continuously, so a full pipe never blocks the host.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[factory work {process.Id}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[factory work {process.Id}!] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Whether a process with this pid exists (read-only: <c>ps</c>, never a signal).</summary>
    private static bool IsRunning(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-p", pid.ToString(), "-o", "pid="]) { RedirectStandardOutput = true })!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return output.Trim().Length > 0;
    }
}
