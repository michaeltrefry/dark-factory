using System.Diagnostics;
using System.Runtime.InteropServices;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Unsandboxed test steps own a fresh process group. Its supervisor remains alive after the command exits until the
/// group's descendants exit, including reparented pipe holders. A signal is sent only while that original leader is alive;
/// after its death, a group number alone is not authority to signal. Unconfirmed termination leaves a durable protection
/// marker beside the worktree: removal, replacement and orphan sweeps refuse it, also after the factory restarts.
/// </summary>
internal static class DirectTestProcess
{
    internal const string Launcher = "/usr/bin/perl";
    internal const string Supervisor = """
        use strict;
        setpgrp(0, 0) or die "setpgrp: $!\n";
        my $child = fork();
        die "fork: $!\n" unless defined $child;
        if ($child == 0) { exec { $ARGV[0] } @ARGV; print STDERR "exec: $!\n"; exit 127; }
        waitpid($child, 0);
        my $status = $?;
        while (1) {
            my $scan = open(my $rows, '-|', '/bin/ps', '-axo', 'pgid=,pid=,stat=');
            die "cannot inspect test process group\n" unless defined $scan;
            my $live = 0;
            while (<$rows>) {
                if (/^\s*(\d+)\s+(\d+)\s+(\S+)/) {
                    $live = 1 if $1 == $$ && $2 != $$ && $2 != $scan && $3 !~ /^[ZX]/;
                }
            }
            close($rows);
            die "cannot inspect test process group\n" if $? != 0;
            last unless $live;
            select(undef, undef, undef, 0.05);
        }
        exit(($status & 127) ? 128 + ($status & 127) : $status >> 8);
        """;

    internal static string Marker(string worktree) => worktree + ".factory-test-process";

    internal static bool Protected(string worktree)
    {
        try { _ = File.GetAttributes(Marker(worktree)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static Exception Unconfirmed(string worktree) => WorkerStillRunning.Mark(new InvalidOperationException(
        $"The test process tree could not be confirmed stopped. Worktree {worktree} is retained; cleanup, replacement and sweeps are refused. "
        + $"After confirming its processes have exited, remove {Marker(worktree)} by hand before retrying."));

    internal static void Stop(Process process)
    {
        if (process.HasExited) return; // no late signal by a possibly reused group number
        if (OperatingSystem.IsWindows()) process.Kill(entireProcessTree: true);
        else if (process.Id > 1 && Signal(-process.Id, 9) != 0 && !process.HasExited)
        {
            // Cancellation can arrive before perl establishes the group. This Process is still our live child.
            process.Kill(entireProcessTree: true);
        }
    }

    internal static async Task WaitStoppedAsync(Process process, CancellationToken ct)
    {
        await process.WaitForExitAsync(ct);
        if (!OperatingSystem.IsWindows())
        {
            // Conservative: a group that still exists (even only zombies) is not proof of complete teardown.
            while (true)
            {
                if (Signal(-process.Id, 0) < 0 && Marshal.GetLastPInvokeError() == 3) break; // ESRCH only: absent, not unreadable
                await Task.Delay(25, ct);
            }
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int pid, int signal);
}
