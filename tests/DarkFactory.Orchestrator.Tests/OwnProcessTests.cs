using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <see cref="OwnProcess"/>, the one way tests signal a process: it never signals a pid whose start time is no longer the one
/// recorded (a reused pid, possibly any process of the owner's session), and no test signals a process any other way.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class OwnProcessTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-own-process-").FullName;

    /// <summary>
    /// A record of a process that died and whose pid now belongs to another (here: <paramref name="reuser"/>, this test's
    /// own detached process, so even a broken check could only hit that).
    /// </summary>
    private static OwnProcess Reused(OwnProcess reuser) => reuser with { Start = "Thu Jan 1 00:00:00 1970" };

    [Fact]
    public void Never_signals_a_pid_whose_start_time_no_longer_matches()
    {
        var reuser = OwnProcess.StartDetached(_dir);
        try
        {
            Assert.Equal(reuser.Start, OwnProcess.StartTimeOf(reuser.Pid)); // the pid is the test's own process
            var reused = Reused(reuser);
            Assert.False(reused.IsAlive); // what decides: checked before any signal could be sent

            reused.KillIfStillRunning();

            Thread.Sleep(200);
            Assert.True(reuser.IsAlive, "a reused pid was signalled");
        }
        finally
        {
            reuser.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Kills_its_own_process_while_the_start_time_matches()
    {
        var own = OwnProcess.StartDetached(_dir);
        try
        {
            own.KillIfStillRunning();

            await OwnProcess.WaitUntilDeadAsync(own);
            Assert.Null(OwnProcess.StartTimeOf(own.Pid));
        }
        finally
        {
            own.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Waiting_treats_a_reused_pid_as_dead_and_never_signals_it()
    {
        var reuser = OwnProcess.StartDetached(_dir);
        try
        {
            var reused = Reused(reuser);
            Assert.False(reused.IsAlive);

            await OwnProcess.WaitUntilDeadAsync(reused); // passes at once: that process is gone

            Thread.Sleep(200);
            Assert.True(reuser.IsAlive, "waiting on a dead process signalled the pid's new owner");
        }
        finally
        {
            reuser.KillIfStillRunning();
        }
    }

    /// <summary>
    /// Nothing in the test projects sends a signal except <see cref="OwnProcess"/> (its file is the only one allowed a raw
    /// kill): no <c>Process.Kill</c>, no <c>kill(2)</c> import, no <c>kill</c>/<c>pkill</c>/<c>killall</c>/<c>killpg</c>
    /// command or call in a script a test writes — except the listed lines, which are the launch helper's seam (its own
    /// guard: <see cref="SafeHelperTests"/>) and text that is only checked, never run with a real <c>kill</c>.
    /// The patterns are assembled so this file does not match them itself.
    /// </summary>
    [Fact]
    public void No_test_signals_a_process_except_through_OwnProcess()
    {
        var k = "ki" + "ll";
        var rawKill = new Regex(string.Join("|",
            @"\.Ki" + @"ll\s*\(", // Process.Kill
            @"\b(Dll|Library)Im" + @"port\b", // a libc signal call (the kill system call and friends)
            $@"(?<![\w./-])(p{k}|{k}all|{k}pg|{k})\s+(-|\$|""|'|\\|[0-9])", // a kill command in a script
            $@"(?<![\w.])({k}|{k}pg)\s*\(", // a signal call in perl, C or awk
            $@"\b(os|process|Process)\.{k}", // python, node
            $@"(ProcessStartInfo|Process\.Start)\(\s*""(/usr)?(/s?bin/)?(p{k}|{k}all|{k})""", // a signal program run directly
            $@"/bin/{k}\b"));
        string[] ownerFiles = ["OwnProcess.cs"];
        // file -> exact trimmed lines: the seam and text only checked or run against a fake kill.
        var allowed = new Dictionary<string, string[]>
        {
            ["SafeHelper.cs"] =
            [
                $"{k} \"$@\" 2>/dev/null", // the production primitive, matched to replace it
                $"{k} \"$sig\" -- \"${{kills[@]}}\" 2>/dev/null", // the seam
            ],
            ["SafeHelperTests.cs"] =
            [
                $"\"raw-{k}\" => Before(\"{k} -KILL \\\"$@\\\"\"),",
                $"\"p{k}\" => Before(\"p{k} -U \\\"$self_uid\\\"\"),",
                $"\"{k}all\" => Before(\"{k}all -u \\\"$sandbox_user\\\"\"),",
                $"\"perl-{k}\" => Before(\"perl -e '{k} 9, -1'\"),",
                $"\"after-quoted-hash\" => Before(\"echo \\\"a # b\\\"; {k} -KILL -1\"),",
                $"Copy(new SafeHelperOptions {{ Identity = FakeIdentity.SandboxRole, FakeListing = \"{k} -KILL -1; echo '400001 1'\" }}));",
                $"var script = \"enable -n {k}\\nPATH=/nonexistent\\n{k}() {{ echo \\\"args $*\\\"; }}\\n\"",
            ],
            ["WorkerSandboxTests.cs"] = [$"Assert.False(SafeHelper.SourceContains(\"{k} -KILL -1\"));"],
        };

        var testsRoot = Path.Combine(SandboxSupport.RepoRoot, "tests");
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}") && !f.Contains($"{separator}obj{separator}"))
            .ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == ownerFiles[0]);
        Assert.Contains(files, f => f.Contains($"{separator}DarkFactory.AcceptanceTests{separator}"));

        var violations = new List<string>();
        var used = new HashSet<string>();
        foreach (var file in files.Where(f => !ownerFiles.Contains(Path.GetFileName(f))))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                // A script in a C# string starts its lines after a \n (or \t) escape: matched as the line break it is.
                var code = trimmed.Replace("\\n", " ").Replace("\\t", " ");
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || !rawKill.IsMatch(code))
                {
                    continue;
                }
                if (allowed.TryGetValue(name, out var lineAllowList) && lineAllowList.Contains(trimmed))
                {
                    used.Add($"{name}|{trimmed}");
                }
                else
                {
                    violations.Add($"{Path.GetRelativePath(testsRoot, file)}:{i + 1}: {trimmed}");
                }
            }
        }

        Assert.True(violations.Count == 0, "violations:\n" + string.Join("\n", violations));
        var unused = allowed.SelectMany(kv => kv.Value.Select(l => $"{kv.Key}|{l}")).Where(a => !used.Contains(a)).ToList();
        Assert.True(unused.Count == 0, "allowances that match no line: " + string.Join("; ", unused));
    }
}
