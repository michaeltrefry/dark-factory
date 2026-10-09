using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>What the git workspace needs from the sandbox: sharing a fresh worktree and deleting what the worker left.</summary>
public interface IWorkerSandbox
{
    /// <summary>Gives the worker user read/write on the empty directory <paramref name="path"/>, inherited by everything later created in it, while the owner keeps full access.</summary>
    Task ShareAsync(string path, CancellationToken ct);

    /// <summary>Deletes <paramref name="path"/> as the worker user (best effort), so the owner never walks a worker-controlled tree first.</summary>
    Task DeleteAsWorkerAsync(string path, CancellationToken ct);
}

/// <summary>
/// Runs workers as a dedicated macOS user (E5) through the root-owned launch helper
/// (<c>scripts/factory-worker-launch</c>), which <c>scripts/setup-worker-user.sh</c> installs
/// and allows the owner to run as <see cref="User"/> via a NOPASSWD sudoers rule.
/// Router variables travel on the helper's stdin, never argv; closing stdin stops the worker.
/// <paramref name="HelperSource"/>: the helper as <c>scripts/factory-worker-launch</c> has it, which the installed one must
/// match (<see cref="StaleHelperReason"/>); null is the copy compiled into this assembly.
/// </summary>
public sealed record WorkerSandbox(string User, string HelperPath, string SudoPath = WorkerSandbox.DefaultSudoPath, string? HelperSource = null)
    : IWorkerSandbox
{
    public const string DefaultUser = "_factory";
    public const string DefaultHelperPath = "/usr/local/libexec/dark-factory/factory-worker-launch";
    public const string DefaultSudoPath = "/usr/bin/sudo";
    public const string ReinstallCommand = "sudo scripts/setup-worker-user.sh";
    public const string SetupHint = $"run `{ReinstallCommand}` once (see README, Worker sandbox)";

    /// <summary>The <c>helper_version</c> of <c>scripts/factory-worker-launch</c>: an installed helper below it is stale.</summary>
    public const int HelperVersion = 2;

    /// <summary>
    /// The oldest Claude Code the worker user may run: 2.1.291, the release the confined triage session was built and checked
    /// against (it honours <c>--setting-sources ""</c>, <c>permissions.blockReadsOutsideWorkingDirectories</c> and the
    /// <c>dontAsk</c> permission mode). No earlier release is pinned as honouring all three, and an older CLI that does not know
    /// a setting ignores it silently, so <see cref="EnsureReadyAsync"/> refuses one below this.
    /// </summary>
    public const string MinClaudeVersion = "2.1.291";

    /// <summary><c>scripts/factory-worker-launch</c> as built into this assembly (never written out or run: only compared).</summary>
    private static readonly Lazy<string> CompiledHelper = new(() =>
    {
        using var stream = typeof(WorkerSandbox).Assembly.GetManifestResourceStream("factory-worker-launch")
            ?? throw new InvalidOperationException("The launch helper is not compiled into the orchestrator.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Directory ACL for the worker user and the owner; file_inherit/directory_inherit carry it to everything created later.</summary>
    private const string AclRights =
        "read,write,append,execute,delete,list,search,add_file,add_subdirectory,delete_child," +
        "readattr,writeattr,readextattr,writeextattr,readsecurity,file_inherit,directory_inherit";

    /// <summary><c>sudo -n -u &lt;user&gt; &lt;helper&gt; &lt;program&gt; args…</c>.</summary>
    public IReadOnlyList<string> BuildLaunchArguments(string program, IEnumerable<string> args) =>
        ["-n", "-u", User, HelperPath, program, .. args];

    /// <summary>The helper's stdin block: allowlisted <c>KEY=VALUE</c> lines and a terminating empty line.</summary>
    public static string BuildVariableBlock(IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (key, value) in variables)
        {
            if (key.Contains('=') || $"{key}{value}".IndexOfAny(['\n', '\r']) >= 0)
            {
                throw new ArgumentException($"Worker variable '{key}' contains '=' in its name or a line break.");
            }
        }
        return string.Concat(variables.Select(kv => $"{kv.Key}={kv.Value}\n")) + "\n";
    }

    /// <summary>Starts <paramref name="program"/> as the worker user. The caller must keep stdin open for the life of the run.</summary>
    public Process Start(string workingDirectory, string program, IEnumerable<string> args, IReadOnlyDictionary<string, string> variables)
    {
        var psi = new ProcessStartInfo(SudoPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in BuildLaunchArguments(program, args))
        {
            psi.ArgumentList.Add(arg);
        }
        // sudo resets the environment anyway; hand it nothing of the owner's.
        psi.Environment.Clear();
        psi.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {SudoPath}.");
        try
        {
            // Straight to the pipe, so the writer never holds unsent bytes that would throw again on Close.
            process.StandardInput.BaseStream.Write(System.Text.Encoding.UTF8.GetBytes(BuildVariableBlock(variables)));
            process.StandardInput.BaseStream.Flush();
        }
        catch (IOException)
        {
            // sudo or the helper exited before reading (e.g. no such user); its exit code and stderr say why.
        }
        return process;
    }

    /// <summary>
    /// Kills every process of the worker user (left by a crashed run) by running a no-op through the
    /// helper, whose exit kills them all. Returns false, running nothing, when the user has none.
    /// </summary>
    public async Task<bool> StopAllAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/usr/bin/pgrep", ["-U", User]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using (var pgrep = Process.Start(psi)!)
        {
            var pids = await pgrep.StandardOutput.ReadToEndAsync(ct);
            await pgrep.WaitForExitAsync(ct);
            if (pgrep.ExitCode != 0 || pids.Trim().Length == 0)
            {
                return false;
            }
        }
        var (exitCode, _, stderr) = await RunAsync("/", "/usr/bin/true", [], ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Could not stop the worker user's processes through {HelperPath} ({stderr.Trim()}).");
        }
        return true;
    }

    /// <summary>Closes the helper's stdin, which makes it kill the worker's processes (a no-op once it has exited).</summary>
    public static void Stop(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The helper already exited; the writer is closed regardless.
        }
    }

    /// <summary>Runs a short command as the worker user and returns (exit code, stdout, stderr).</summary>
    public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string workingDirectory, string program, IEnumerable<string> args, CancellationToken ct) =>
        RunAsync(workingDirectory, program, args, new Dictionary<string, string>(), ct);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string workingDirectory, string program, IEnumerable<string> args, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        using var process = Start(workingDirectory, program, args, variables);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            Stop(process);
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Why <paramref name="installed"/> (the installed helper's text) is not the current helper set up for
    /// <paramref name="user"/>, or null. Setup installs <c>scripts/factory-worker-launch</c> with exactly two lines edited,
    /// <c>sandbox_user=&lt;user&gt;</c> and <c>sandbox_uid=&lt;its uid&gt;</c>; with those two undone, the installed file
    /// must have the same SHA-256 as <paramref name="source"/> (null: the compiled-in helper). So a helper installed before
    /// a change (or edited since) is stale, whatever its version line says.
    /// </summary>
    public static string? StaleHelperReason(string installed, string user, string? source = null)
    {
        source ??= CompiledHelper.Value;
        var version = Regex.Match(installed, "^helper_version=([0-9]{1,9})$", RegexOptions.Multiline);
        if (!version.Success)
        {
            return "it has no helper_version line";
        }
        if (int.Parse(version.Groups[1].Value) < HelperVersion)
        {
            return $"its helper_version {version.Groups[1].Value} is older than {HelperVersion}";
        }
        var users = Regex.Matches(installed, "^sandbox_user=(.*)$", RegexOptions.Multiline);
        if (users.Count != 1 || users[0].Groups[1].Value != user)
        {
            return $"its sandbox_user is not {user}";
        }
        var uids = Regex.Matches(installed, "^sandbox_uid=(.*)$", RegexOptions.Multiline);
        if (uids.Count != 1 || !Regex.IsMatch(uids[0].Groups[1].Value, "^[0-9]+$"))
        {
            return "it has no pinned sandbox_uid";
        }
        var unpinned = installed
            .Remove(uids[0].Index, uids[0].Length).Insert(uids[0].Index, "sandbox_uid=");
        var userLine = Regex.Match(unpinned, "^sandbox_user=.*$", RegexOptions.Multiline);
        unpinned = unpinned.Remove(userLine.Index, userLine.Length).Insert(userLine.Index, $"sandbox_user={DefaultUser}");
        var (have, want) = (Sha256(unpinned), Sha256(source));
        return have == want ? null : $"its text differs from the repo helper, scripts/factory-worker-launch (sha256 {have[..12]}…, expected {want[..12]}…)";
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Fails fast, with the setup command, when the user, helper or sudoers rule is missing, or when the installed helper
    /// is stale. Before anything runs, the installed file must be the current helper (<see cref="StaleHelperReason"/>; it is
    /// installed 0755, so readable): an older one may lack the uid sweep's guards. Then the probe hands it the router variable
    /// names a worker launch uses for <paramref name="auth"/> (dummy values), so a helper whose allowlist refuses one fails
    /// here once instead of failing every worker launch; and a helper that refuses its uid sweep (its pinned uid is not the
    /// worker user's, or outside 400-499) fails too, since a worker's leftovers would then outlive every run. Last, the worker
    /// user's own Claude Code (<paramref name="claudePath"/>, as the worker sees it) must be at least <see cref="MinClaudeVersion"/>.
    /// </summary>
    public async Task EnsureReadyAsync(WorkerAuth auth, string claudePath, CancellationToken ct)
    {
        string installed;
        try
        {
            installed = await File.ReadAllTextAsync(HelperPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Worker sandbox unavailable: cannot read the launch helper {HelperPath} ({ex.Message}); {SetupHint}.", ex);
        }
        if (StaleHelperReason(installed, User, HelperSource) is { } stale)
        {
            throw new InvalidOperationException(
                $"Stale helper: the installed launch helper {HelperPath} is not the current scripts/factory-worker-launch ({stale}); "
                + $"re-run `{ReinstallCommand}` to install it (that kills every {User} process: stop `factory work` first).");
        }
        var probeVariables = ClaudeWorker.BuildRouterVariables(new Uri("http://127.0.0.1/"), "probe", auth);
        (int ExitCode, string Stdout, string Stderr) probe;
        try
        {
            probe = await RunAsync("/", "/usr/bin/id", ["-un"], probeVariables, ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Worker sandbox unavailable ({ex.Message}); {SetupHint}.", ex);
        }
        if (probe.ExitCode == 64 && probe.Stderr.Contains("non-allowlisted variable", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The installed launch helper {HelperPath} is stale: it refuses the worker's router variables ({probe.Stderr.Trim()}); "
                + "re-run `sudo scripts/setup-worker-user.sh` to install the current helper.");
        }
        if (probe.ExitCode != 0 || probe.Stdout.Trim() != User)
        {
            throw new InvalidOperationException(
                $"Worker sandbox user '{User}' is not usable through {HelperPath} ({probe.Stderr.Trim()}); {SetupHint}.");
        }
        if (probe.Stderr.Split('\n').FirstOrDefault(l => l.Contains("refusing the", StringComparison.Ordinal) && l.Contains("uid sweep", StringComparison.Ordinal))
            is { } refusal)
        {
            throw new InvalidOperationException(
                $"The installed launch helper {HelperPath} refuses its {User} uid sweep, so a worker's leftovers would outlive its run "
                + $"({refusal.Trim()}); re-run `{ReinstallCommand}` (it pins {User}'s uid; a {User} whose uid is outside 400-499 must be recreated).");
        }
        var claude = await RunAsync("/", claudePath, ["--version"], ct);
        if (claude.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Cannot run `{claudePath} --version` as {User} through {HelperPath} (exit {claude.ExitCode}: {claude.Stderr.Trim()}); {SetupHint}.");
        }
        if (OutdatedClaudeReason(claude.Stdout) is { } outdated)
        {
            throw new InvalidOperationException(
                $"Upgrade {User}'s claude: {outdated}, and an older CLI may silently ignore the triage session's read confinement "
                + $"(`--setting-sources \"\"`, `blockReadsOutsideWorkingDirectories`, `dontAsk`); run `sudo -u {User} -H ~{User}/.local/bin/claude update` "
                + $"(or delete ~{User}/.local/bin/claude and re-run `{ReinstallCommand}` to reinstall it).");
        }
    }

    /// <summary>
    /// Why <paramref name="versionOutput"/> (what <c>claude --version</c> printed, e.g. <c>2.1.291 (Claude Code)</c>) is not at least
    /// <see cref="MinClaudeVersion"/>, or null.
    /// </summary>
    public static string? OutdatedClaudeReason(string versionOutput)
    {
        var text = versionOutput.Trim();
        var match = Regex.Match(text, @"^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(?![0-9.])");
        if (!match.Success)
        {
            return $"its `claude --version` printed '{(text.Length > 80 ? text[..80] : text)}', no version (Claude Code {MinClaudeVersion} or newer is required)";
        }
        var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        return version < Version.Parse(MinClaudeVersion)
            ? $"it runs Claude Code {version}, older than the {MinClaudeVersion} the factory requires"
            : null;
    }

    /// <summary>
    /// Never recursive: macOS <c>chmod -R</c> applies ACLs to symlink targets, so sharing a checkout
    /// could grant the worker a committed symlink's target (e.g. the clone's hooks). The caller shares
    /// the empty directory first and lets everything created in it inherit the entries.
    /// </summary>
    public async Task ShareAsync(string path, CancellationToken ct)
    {
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException($"Refusing to share non-empty {path}: its contents would not inherit the ACL.");
        }
        foreach (var user in new[] { Environment.UserName, User })
        {
            await RunChecked("/bin/chmod", ["+a", $"user:{user} allow {AclRights}", path], ct);
        }
    }

    public async Task DeleteAsWorkerAsync(string path, CancellationToken ct)
    {
        // Best effort: whatever the worker user can't remove, the owner's retry reports.
        await RunAsync("/", "/bin/rm", ["-rf", "--", path], ct);
    }

    private static async Task RunChecked(string program, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} failed ({p.ExitCode}): {(await stderr).Trim()}");
        }
    }
}
