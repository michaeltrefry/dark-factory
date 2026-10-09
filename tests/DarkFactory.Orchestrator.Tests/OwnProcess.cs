using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// A process a test started, named by its pid and its start time (<c>ps -o lstart=</c>, whitespace collapsed). This is the
/// only place in the test projects that signals a process (<c>OwnProcessTests</c> fails on any other): <see cref="KillIfStillRunning"/>
/// signals only while the pid still has the recorded start time, so a pid that died and was reused — by any process of the
/// owner's session — is never hit. The start time is captured where the pid cannot have been reused yet: by the process
/// itself (<see cref="ShellRecord"/>), or for a child of this host while it is still unreaped (<see cref="Of"/>).
/// (Linked into DarkFactory.AcceptanceTests.)
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed record OwnProcess(int Pid, string Start)
{
    private const int SigKill = 9;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);

    /// <summary>
    /// A shell expression printing "pid|start" for the pid in <paramref name="pidExpression"/> (e.g. <c>$$</c>, <c>$!</c>):
    /// run by the process itself or its unreaped parent, so the pid is still that process's.
    /// </summary>
    public static string ShellRecord(string pidExpression) =>
        $"\"{pidExpression}|$(/bin/ps -o lstart= -p {pidExpression} | /usr/bin/awk '{{ $1 = $1; print }}')\"";

    /// <summary>Parses a "pid|start" record (<see cref="ShellRecord"/>).</summary>
    public static OwnProcess Parse(string record)
    {
        var parts = record.Trim().Split('|', 2);
        Assert.True(parts.Length == 2 && int.TryParse(parts[0], out _), $"not a pid|start record: '{record}'");
        var start = Collapse(parts[1]);
        Assert.False(start.Length == 0, $"no start time in '{record}' (the process was gone when it was recorded)");
        return new OwnProcess(int.Parse(parts[0]), start);
    }

    /// <summary>A child this host started, recorded while it is provably unreaped (so its pid cannot have been reused).</summary>
    public static OwnProcess Of(Process child)
    {
        var start = StartTimeOf(child.Id);
        Assert.False(start is null || child.HasExited, $"child {child.Id} exited before its start time was recorded");
        return new OwnProcess(child.Id, start!);
    }

    /// <summary>
    /// A process of the current user outside this test host's tree (double-forked and setsid()'d, so reparented to launchd,
    /// its own group leader) — what any other process of the owner's session looks like. It records its own pid and start
    /// time in <paramref name="dir"/>, then sleeps 600 s.
    /// </summary>
    public static OwnProcess StartDetached(string dir)
    {
        var pidFile = Path.Combine(dir, $"detached-{Guid.NewGuid():N}");
        using (var p = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-MPOSIX", "-e",
            "my $f = shift; exit 0 if fork; POSIX::setsid(); exit 0 if fork; "
            + "open(STDIN, '<', '/dev/null'); open(STDOUT, '>', '/dev/null'); open(STDERR, '>', '/dev/null'); "
            + "my $ls = `/bin/ps -o lstart= -p $$`; $ls =~ s/\\s+/ /g; $ls =~ s/^ | $//g; "
            + "open(my $h, '>', \"$f.tmp\"); print $h \"$$|$ls\\n\"; close $h; rename(\"$f.tmp\", $f); exec('/bin/sleep', '600');",
            pidFile]))!)
        {
            p.WaitForExit();
        }
        for (var i = 0; i < 200 && !File.Exists(pidFile); i++)
        {
            Thread.Sleep(25);
        }
        return Parse(File.ReadAllText(pidFile));
    }

    /// <summary><c>ps -o lstart=</c> of <paramref name="pid"/>, whitespace collapsed; null when no such process.</summary>
    public static string? StartTimeOf(int pid)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/ps", ["-o", "lstart=", "-p", pid.ToString()]) { RedirectStandardOutput = true })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var start = Collapse(text);
        return start.Length == 0 ? null : start;
    }

    /// <summary>Whether any process has <paramref name="pid"/> (no signal; for pids this test does not own).</summary>
    public static bool Exists(int pid) => StartTimeOf(pid) is not null;

    /// <summary>This process is still running (the pid has the recorded start time).</summary>
    public bool IsAlive => StartTimeOf(Pid) == Start;

    /// <summary>SIGKILL, only while the pid is still this process; otherwise nothing.</summary>
    public void KillIfStillRunning()
    {
        if (Pid > 1 && StartTimeOf(Pid) == Start)
        {
            SendSignal(Pid, SigKill);
        }
    }

    /// <summary>
    /// Asserts every process dies within 5 s. Only those still alive (same start time) at the deadline are killed, so a
    /// failure leaks nothing; one confirmed dead is never signalled.
    /// </summary>
    public static async Task WaitUntilDeadAsync(params OwnProcess[] processes)
    {
        for (var i = 0; i < 50 && processes.Any(p => p.IsAlive); i++)
        {
            await Task.Delay(100);
        }
        var survivors = processes.Where(p => p.IsAlive).ToArray();
        foreach (var survivor in survivors)
        {
            survivor.KillIfStillRunning();
        }
        Assert.True(survivors.Length == 0, $"survived the stop: {string.Join(", ", survivors.Select(s => s.Pid))}");
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
