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

    /// <summary>
    /// <see cref="DeleteAsWorkerAsync"/> only if the worker user is free now (or the calling flow holds its lock): answers false,
    /// deleting nothing and waiting for nothing, while another run's worker phase has it — for clean-up outside a worker phase,
    /// which must not wait out another run's session and which a later sweep does as well.
    /// </summary>
    async Task<bool> TryDeleteAsWorkerAsync(string path, CancellationToken ct)
    {
        await DeleteAsWorkerAsync(path, ct);
        return true;
    }

    /// <summary>
    /// Closes the owner's directory <paramref name="path"/> to the worker user and everyone else: owner-only mode and no ACL entry
    /// (the work root's inherited one included), so nothing in it can be opened by anyone but the owner. Fails unless the
    /// directory then reads back closed.
    /// </summary>
    Task CloseToWorkerAsync(string path, CancellationToken ct);

    /// <summary>
    /// Before worker work in the worktree <paramref name="path"/> is pushed (sc-25990, E4/E7): fails
    /// (<see cref="WorktreeWritersException"/>) unless the only principals its ACL lets write are the owner and the worker user
    /// whose work it is — the slot the calling flow holds. A worktree another slot's user, anyone else, or (by its mode) everyone
    /// could write may hold what a session other than the granted ones put there, so nothing of it is committed or pushed.
    /// Fails closed: a listing that cannot be read refuses the push too.
    /// </summary>
    Task EnsureOnlyWorkerWritesAsync(string path, CancellationToken ct);
}

/// <summary>
/// The push of worker work was refused (sc-25990): the worktree <see cref="Path"/> is writable by someone other than its own
/// slot's sandbox user and the owner, so a session that is not among the push grant's — another slot's, possibly tainted (E4) —
/// could have changed the files. The item's failure (Escalated, with this reason). Its worktree is removed with the escalation like
/// any escalated item's, deliberately: what is in it cannot be vouched for, so it is not kept to be resumed or pushed later; the
/// reason names the entry, and a re-run does the work again in a fresh worktree.
/// </summary>
public sealed class WorktreeWritersException(string path, string user, string why)
    : InvalidOperationException($"Refusing to push the work in {path}: {why}. Only {user} (the sandbox user of the worker slot whose work this is) "
        + "and the owner may write an item's worktree and what is in it, so that no other slot's session can change files this push would publish (E4). "
        + "Nothing was committed or pushed, and the session's work is discarded with the worktree (it cannot be vouched for); "
        + "a re-run does it again in a fresh worktree. If it happens again, something the worker runs in this repository (a build "
        + "or test step) opens files to other users: `chmod go-w`/umask 022 there, or run with Worker:Slots 1.")
{
    public string Path => path;
}

/// <summary>
/// Runs workers as a dedicated macOS user (E5) through the root-owned launch helper
/// (<c>scripts/factory-worker-launch</c>), which <c>scripts/setup-worker-user.sh</c> installs — one copy per sandbox user, pinned
/// to it — and allows the owner to run as <see cref="User"/> via a NOPASSWD sudoers rule.
/// Router variables travel on the helper's stdin, never argv; closing stdin stops the worker.
/// <paramref name="HelperSource"/>: the helper as <c>scripts/factory-worker-launch</c> has it, which the installed one must
/// match (<see cref="StaleHelperReason"/>); null is the copy compiled into this assembly.
/// A sandbox is one sandbox user — one worker slot of the pool (<see cref="WorkerSlots"/>, sc-25989), which has one of these per
/// slot, each with its own user, helper and lock.
/// Every run of the helper happens under the sandbox user's lock (<see cref="Lock"/>, sc-25988): the helper's exit kills every
/// process of <see cref="User"/>, another run's worker included. <see cref="Start"/> refuses to launch unless the calling flow
/// holds it (the caller holds it for as long as the process lives); the short commands here (<see cref="RunAsync(string, string, IEnumerable{string}, CancellationToken)"/>,
/// <see cref="EnsureReadyAsync"/>, <see cref="StopAllAsync"/>, <see cref="DeleteAsWorkerAsync"/>) take it themselves, waiting,
/// unless their caller's phase already holds it. And the lock covers the whole life of every helper process: whoever started one
/// ends it with <see cref="EndAsync"/>, which waits for it to exit (<see cref="StopGrace"/>) and, if it will not, keeps the lock
/// (<see cref="WorkerLock.KeepUntil"/>) until it is gone — its exit, whenever it comes, still kills every process of <see cref="User"/>.
/// </summary>
public sealed record WorkerSandbox(string User, string HelperPath, string SudoPath = WorkerSandbox.DefaultSudoPath, string? HelperSource = null)
    : IWorkerSandbox
{
    /// <summary>
    /// The sandbox user's lock (<see cref="WorkerLock"/>). The production sandbox always has one (<c>FactoryOptions.WorkerSandbox</c>:
    /// the work root's); null only for a sandbox a test builds over its own helper copy, which shares its user with no other run.
    /// </summary>
    public WorkerLock? Lock { get; init; }

    /// <summary>
    /// What the owner runs to (re)install this sandbox's helper: <see cref="ReinstallCommand"/>, with <c>--slots n</c> when the
    /// factory has several slots (<see cref="WorkerSlots.ReinstallCommand"/>: the script then sets up every slot's user and helper).
    /// </summary>
    public string Reinstall { get; init; } = ReinstallCommand;

    /// <summary>
    /// The Swift/Xcode toolchain this sandbox user must have (sc-26130), or null when the factory works no Swift repository
    /// (<c>Worker:Swift</c> off, the default: nothing of Swift or Xcode is checked, E7). Set, <see cref="EnsureHelperCurrentAsync"/>
    /// also checks the installed xcodebuild wrapper and <see cref="EnsureReadyAsync"/> probes Swift, xcodebuild, Xcode's first
    /// launch and license, and the wrapper as <see cref="User"/> (<see cref="SwiftToolchain.EnsureUsableAsync"/>).
    /// </summary>
    public SwiftToolchain? Swift { get; init; }

    /// <summary>The hint a missing or unusable sandbox is reported with.</summary>
    private string Hint => $"run `{Reinstall}` once (see README, Worker sandbox)";

    /// <summary>Runs <paramref name="command"/> holding <see cref="Lock"/> (waited for; at once when the calling flow holds it or there is none).</summary>
    private Task<T> LockedAsync<T>(Func<Task<T>> command, CancellationToken ct) => Lock is null ? command() : Lock.HoldAsync(command, ct);

    /// <summary>How long a helper whose stdin was closed (it then kills its worker's processes, sweeps the user's and exits) is waited for.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether <paramref name="process"/> has exited, waiting up to <paramref name="grace"/> for it (never cancelled otherwise).</summary>
    public static async Task<bool> ExitsWithinAsync(Process process, TimeSpan grace)
    {
        if (process.HasExited)
        {
            return true;
        }
        if (grace <= TimeSpan.Zero)
        {
            return false;
        }
        using var bounded = new CancellationTokenSource(grace);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    /// <summary>
    /// Ends a helper process <see cref="Start"/> made, for its starter, which is done with it: closes its stdin (<see cref="Stop"/>),
    /// waits up to <paramref name="grace"/> (<see cref="StopGrace"/> when null) for it to exit — on no cancellation token: a
    /// cancelled caller must still not leave a helper sweeping behind it — and disposes it. A helper still alive then is not
    /// abandoned under a free lock: <see cref="Lock"/> stays held until it has exited (<see cref="WorkerLock.KeepUntil"/>, reported
    /// with its pid), and it is disposed then. Answers whether it exited. The caller reads the exit code before this.
    /// </summary>
    public async Task<bool> EndAsync(Process process, TimeSpan? grace = null)
    {
        Stop(process);
        if (await ExitsWithinAsync(process, grace ?? StopGrace))
        {
            process.Dispose();
            return true;
        }
        var pid = process.Id;
        var gone = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            finally
            {
                process.Dispose();
            }
        });
        Lock?.KeepUntil(gone, $"a launch helper run as {User} (sudo pid {pid}) that did not exit when its stdin was closed");
        return false;
    }

    public const string DefaultUser = "_factory";
    public const string DefaultHelperPath = "/usr/local/libexec/dark-factory/factory-worker-launch";
    public const string DefaultSudoPath = "/usr/bin/sudo";
    public const string ReinstallCommand = "sudo scripts/setup-worker-user.sh";

    /// <summary>The <c>helper_version</c> of <c>scripts/factory-worker-launch</c>: an installed helper below it is stale.</summary>
    public const int HelperVersion = 5;

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

    /// <summary>The one variable whose value may span lines: one header per line (the router key, the model class).</summary>
    public const string MultiLineVariable = "ANTHROPIC_CUSTOM_HEADERS";

    /// <summary>
    /// The helper's stdin block: allowlisted <c>KEY=VALUE</c> lines and a terminating empty line. A
    /// <see cref="MultiLineVariable"/> value of several lines goes as one <c>KEY=line</c> line each, which the helper joins back
    /// with line breaks; none of its lines may be empty (that would end the block). Any other line break is refused.
    /// </summary>
    public static string BuildVariableBlock(IReadOnlyDictionary<string, string> variables)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            var lines = key == MultiLineVariable ? value.Split('\n') : [value];
            if (key.Contains('=') || key.IndexOfAny(['\n', '\r']) >= 0 || lines.Any(l => l.IndexOfAny(['\n', '\r']) >= 0)
                || (lines.Length > 1 && lines.Any(l => l.Length == 0)))
            {
                throw new ArgumentException($"Worker variable '{key}' contains '=' in its name, a line break or an empty header line.");
            }
            foreach (var line in lines)
            {
                block.Append(key).Append('=').Append(line).Append('\n');
            }
        }
        return block.Append('\n').ToString();
    }

    /// <summary>
    /// Starts <paramref name="program"/> as the worker user. The caller must keep stdin open for the life of the run, and must hold
    /// <see cref="Lock"/> until the process has exited (the helper's exit is the kill of every <see cref="User"/> process): unless
    /// the calling flow holds it, nothing is started and this throws.
    /// </summary>
    public Process Start(string workingDirectory, string program, IEnumerable<string> args, IReadOnlyDictionary<string, string> variables)
    {
        Lock?.Require($"a run of {program} as {User} through {HelperPath}");
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
    public Task<bool> StopAllAsync(CancellationToken ct) => LockedAsync(() => StopAllLockedAsync(ct), ct);

    /// <summary><see cref="StopAllAsync"/> under the lock: with it held no other run's worker is alive, so every process found is a leftover.</summary>
    private async Task<bool> StopAllLockedAsync(CancellationToken ct)
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

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string workingDirectory, string program, IEnumerable<string> args, IReadOnlyDictionary<string, string> variables, CancellationToken ct) =>
        LockedAsync(() => RunLockedAsync(workingDirectory, program, args, variables, ct), ct);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunLockedAsync(
        string workingDirectory, string program, IEnumerable<string> args, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        var process = Start(workingDirectory, program, args, variables);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            // Also when cancelled: the helper, its stdin closed, is now killing and sweeping the worker user's processes, and
            // the lock this command holds must not be released under that (a waiting run's new worker would be swept).
            await EndAsync(process);
        }
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
    public Task EnsureReadyAsync(WorkerAuth auth, string claudePath, CancellationToken ct) =>
        LockedAsync(async () => { await EnsureReadyLockedAsync(auth, claudePath, ct); return true; }, ct);

    private async Task EnsureReadyLockedAsync(WorkerAuth auth, string claudePath, CancellationToken ct)
    {
        await EnsureHelperCurrentAsync(ct);
        // Shaped like a real launch: the headers of a classed session span two lines (E8).
        var probeVariables = ClaudeWorker.BuildRouterVariables(new Uri("http://127.0.0.1/"), "probe", auth, WorkerModelClass.Mid);
        (int ExitCode, string Stdout, string Stderr) probe;
        try
        {
            probe = await RunAsync("/", "/usr/bin/id", ["-un"], probeVariables, ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Worker sandbox unavailable ({ex.Message}); {Hint}.", ex);
        }
        await EnsureProbedAsync(probe, claudePath, ct);
    }

    /// <summary>
    /// The part of <see cref="EnsureReadyAsync"/> that runs nothing — no helper, so no lock and no kill of the worker user's
    /// processes: the installed helper must be readable and the current one (<see cref="StaleHelperReason"/>). Start-up and
    /// every run's start check this before anything is claimed or recorded.
    /// </summary>
    public async Task EnsureHelperCurrentAsync(CancellationToken ct)
    {
        string installed;
        try
        {
            installed = await File.ReadAllTextAsync(HelperPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Worker sandbox unavailable: cannot read the launch helper {HelperPath} ({ex.Message}); {Hint}.", ex);
        }
        if (StaleHelperReason(installed, User, HelperSource) is { } stale)
        {
            throw new InvalidOperationException(
                $"Stale helper: the installed launch helper {HelperPath} is not the current scripts/factory-worker-launch ({stale}); "
                + $"re-run `{Reinstall}` to install it (that kills every process of every worker user: stop `factory work` first).");
        }
        if (Swift is { } swift)
        {
            await swift.EnsureWrapperCurrentAsync(Reinstall, ct);
        }
    }

    /// <summary>What <see cref="EnsureReadyAsync"/> concludes from the probe's result, then the worker user's Claude Code version.</summary>
    private async Task EnsureProbedAsync((int ExitCode, string Stdout, string Stderr) probe, string claudePath, CancellationToken ct)
    {
        if (probe.ExitCode == 64 && probe.Stderr.Contains("non-allowlisted variable", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The installed launch helper {HelperPath} is stale: it refuses the worker's router variables ({probe.Stderr.Trim()}); "
                + $"re-run `{Reinstall}` to install the current helper.");
        }
        if (probe.ExitCode != 0 || probe.Stdout.Trim() != User)
        {
            throw new InvalidOperationException(
                $"Worker sandbox user '{User}' is not usable through {HelperPath} ({probe.Stderr.Trim()}); {Hint}.");
        }
        if (probe.Stderr.Split('\n').FirstOrDefault(l => l.Contains("refusing the", StringComparison.Ordinal) && l.Contains("uid sweep", StringComparison.Ordinal))
            is { } refusal)
        {
            throw new InvalidOperationException(
                $"The installed launch helper {HelperPath} refuses its {User} uid sweep, so a worker's leftovers would outlive its run "
                + $"({refusal.Trim()}); re-run `{Reinstall}` (it pins {User}'s uid; a {User} whose uid is outside 400-499 must be recreated).");
        }
        var claude = await RunAsync("/", claudePath, ["--version"], ct);
        if (claude.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Cannot run `{claudePath} --version` as {User} through {HelperPath} (exit {claude.ExitCode}: {claude.Stderr.Trim()}); {Hint}.");
        }
        if (OutdatedClaudeReason(claude.Stdout) is { } outdated)
        {
            throw new InvalidOperationException(
                $"Upgrade {User}'s claude: {outdated}, and an older CLI may silently ignore the triage session's read confinement "
                + $"(`--setting-sources \"\"`, `blockReadsOutsideWorkingDirectories`, `dontAsk`); run `sudo -u {User} -H ~{User}/.local/bin/claude update` "
                + $"(or delete ~{User}/.local/bin/claude and re-run `{Reinstall}` to reinstall it).");
        }
        if (Swift is { } swift)
        {
            await swift.EnsureUsableAsync(User, Reinstall, (program, args) => RunAsync("/", program, args, ct));
        }
    }

    /// <summary>
    /// Why <paramref name="versionOutput"/> (what <c>claude --version</c> printed, e.g. <c>2.1.291 (Claude Code)</c>) is not at least
    /// <see cref="MinClaudeVersion"/>, or null.
    /// </summary>
    public static string? OutdatedClaudeReason(string versionOutput)
    {
        var text = versionOutput.Trim();
        var match = Regex.Match(text, @"^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(?![0-9.\-])"); // a pre-release (-beta) sorts before its release: refused
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

    /// <summary>
    /// Owner-side and without root (the owner owns the directory): <c>chmod -N</c> removes every ACL entry — the work root's
    /// inherited read entry for <see cref="User"/> among them, so nothing created in the directory later inherits one — and the
    /// mode becomes <c>0700</c>. Then <c>/bin/ls -led</c> must show it closed (<see cref="IsClosed"/>), else this throws.
    /// </summary>
    public Task CloseToWorkerAsync(string path, CancellationToken ct) =>
        CloseToWorkerAsync(path, ListAclAsync, ct);

    /// <summary><see cref="CloseToWorkerAsync(string, CancellationToken)"/>, the directory read back through <paramref name="list"/> (tests).</summary>
    internal async Task CloseToWorkerAsync(string path, Func<string, CancellationToken, Task<string>> list, CancellationToken ct)
    {
        await CloseAsync(path, ct);
        var listing = await list(path, ct);
        if (!IsClosed(listing))
        {
            throw new InvalidOperationException(
                $"{path} is still open to others than its owner after `chmod -N` and mode 0700 (ls -led: {listing.Trim().ReplaceLineEndings("; ")}), so {User} could reach what is in it.");
        }
    }

    /// <summary>
    /// The one way the owner closes a directory of its own to every sandbox user (sc-25992: the clone locks', the slot locks' and
    /// a slot's MCP session directory all start here): <c>chmod -N</c> removes every ACL entry — the work root's inherited read
    /// entries among them, so nothing made in it later inherits one — and the mode becomes <c>0700</c>. Each caller then reads it
    /// back for what it needs (<see cref="IsClosed"/>, or <see cref="OpenToOthers"/> once its own user's entries are added).
    /// </summary>
    private static async Task CloseAsync(string path, CancellationToken ct)
    {
        await RunChecked("/bin/chmod", ["-N", path], ct);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Whether <paramref name="lsOutput"/> (macOS <c>ls -led</c> of one directory: its long listing line, then one line per ACL
    /// entry, e.g. <c> 0: user:_factory inherited allow list,search</c>) shows a directory only its owner can enter: mode
    /// <c>drwx------</c> and no ACL entry at all, of any user or kind.
    /// </summary>
    public static bool IsClosed(string lsOutput)
    {
        var lines = lsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 1 && Regex.IsMatch(lines[0], @"^drwx------[@ ]");
    }

    /// <summary>
    /// Makes the owner's directory <paramref name="path"/> this sandbox user's alone to read (sc-25990: a slot's MCP session
    /// directory), owner-side and without root: closed like <see cref="CloseToWorkerAsync(string, CancellationToken)"/>
    /// (<c>chmod -N</c> — the work root's inherited read entries, one per slot's user, are gone and nothing made in the directory
    /// later inherits one — and mode <c>0700</c>), then exactly two entries for <see cref="User"/>: <c>search</c> on the directory
    /// itself (it opens a file it is given the name of; it cannot list the directory) and, inherited by every file made in it
    /// and applying to nothing else, <c>read</c>. Then <c>/bin/ls -led</c> must show the directory open to that user and to no
    /// one else (<see cref="OpenToOthers"/>), else this throws.
    /// </summary>
    public Task RestrictToWorkerAsync(string path, CancellationToken ct) => RestrictToWorkerAsync(path, ListAclAsync, ct);

    /// <summary><see cref="RestrictToWorkerAsync(string, CancellationToken)"/>, the directory read back through <paramref name="list"/> (tests).</summary>
    internal async Task RestrictToWorkerAsync(string path, Func<string, CancellationToken, Task<string>> list, CancellationToken ct)
    {
        await CloseAsync(path, ct);
        await RunChecked("/bin/chmod", ["+a", $"user:{User} allow search", path], ct);
        await RunChecked("/bin/chmod", ["+a", $"user:{User} allow {InheritedReadRights}", path], ct);
        var listing = await list(path, ct);
        for (var read = 1; read < AclReads && (OpenToOthers(listing, User) is not null || !Grants(listing, User, "search")); read++)
        {
            // ls prints an entry's UUID instead of its user name while the directory service does not answer.
            await Task.Delay(AclRetry, ct);
            listing = await list(path, ct);
        }
        if (OpenToOthers(listing, User) is { } others)
        {
            throw new InvalidOperationException(
                $"{path} is still open to others than its owner and {User} after `chmod -N`, mode 0700 and {User}'s two entries: {others}.");
        }
        if (!Grants(listing, User, "search"))
        {
            throw new InvalidOperationException(
                $"{path} grants {User} no ACL 'search' entry after `chmod +a` (ls -led: {listing.Trim().ReplaceLineEndings("; ")}).");
        }
    }

    /// <summary>How many times a listing that must name one sandbox user and nobody else is read before it fails (ls names a user by its UUID while the directory service is slow to answer).</summary>
    internal const int AclReads = 5;

    /// <summary>How long such a listing waits before it is read again.</summary>
    internal static readonly TimeSpan AclRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>What a file made in a directory restricted to one sandbox user inherits (<see cref="RestrictToWorkerAsync(string, CancellationToken)"/>): read, and nothing on the directory itself.</summary>
    private const string InheritedReadRights = "read,readattr,readextattr,readsecurity,file_inherit,only_inherit";

    /// <summary>One line of <c>ls -le</c> after the first: an ACL entry — its principal (<c>user:name</c>, <c>group:name</c>, or a bare UUID ls could not name), allow or deny, its rights.</summary>
    private static readonly Regex AclLine = new(@"^\s*[0-9]+:\s+(?<who>\S+)\s+(?:inherited\s+)?(?<kind>allow|deny)\s+(?<rights>\S+)\s*$");

    /// <summary>
    /// Whether <paramref name="lsOutput"/> (macOS <c>ls -led</c>: the long listing line, then one line per ACL entry, e.g.
    /// <c> 0: user:_factory inherited allow read,readattr</c>) has an <c>allow</c> entry for <paramref name="user"/> listing
    /// <paramref name="right"/> and no <c>deny</c> entry for that user listing it.
    /// </summary>
    public static bool Grants(string lsOutput, string user, string right)
    {
        var entries = lsOutput.Split('\n').Select(line => AclLine.Match(line))
            .Where(m => m.Success && m.Groups["who"].Value == $"user:{user}" && m.Groups["rights"].Value.Split(',').Contains(right, StringComparer.Ordinal))
            .Select(m => m.Groups["kind"].Value).ToList();
        return entries.Contains("allow") && !entries.Contains("deny");
    }

    /// <summary>
    /// Why <paramref name="lsOutput"/> (macOS <c>ls -led</c> of one directory or file of the owner's) shows it open to anyone but
    /// its owner and <paramref name="user"/>, or null: its mode gives group and others nothing, and every ACL entry that allows
    /// anything names <paramref name="user"/> or the owner. Fails closed: an empty listing, a first line that is no long listing
    /// line, and a later line that is no ACL entry are each a reason.
    /// </summary>
    public static string? OpenToOthers(string lsOutput, string user)
    {
        var lines = lsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !Regex.IsMatch(lines[0], @"^[d-][r-][w-][xsS-]"))
        {
            return "its `ls -led` listing could not be read";
        }
        if (!Regex.IsMatch(lines[0], @"^[d-][r-][w-][xsS-]------[@+ ]"))
        {
            return $"its mode ({lines[0].Split(' ')[0]}) gives its group or everyone access";
        }
        var others = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var entry = AclLine.Match(line);
            if (!entry.Success)
            {
                return $"an ACL line could not be read ('{line.Trim()}')";
            }
            var who = entry.Groups["who"].Value;
            if (entry.Groups["kind"].Value == "allow" && who != $"user:{user}" && who != $"user:{Environment.UserName}" && !others.Contains(who))
            {
                others.Add(who);
            }
        }
        return others.Count == 0 ? null : $"its ACL also allows {string.Join(", ", others)}";
    }

    /// <summary>A sandbox by itself is the one slot there is: the worktree's root is judged, nothing inside it (<see cref="WorkerSlots.EnsureOnlyWorkerWritesAsync"/>).</summary>
    public Task EnsureOnlyWorkerWritesAsync(string path, CancellationToken ct) =>
        WorkerSlots.EnsureOnlyWriterAsync(path, User, ListAclAsync, AclRetry, ct);

    public async Task DeleteAsWorkerAsync(string path, CancellationToken ct)
    {
        // Best effort: whatever the worker user can't remove, the owner's retry reports.
        await RunAsync("/", "/bin/rm", ["-rf", "--", path], ct);
    }

    public async Task<bool> TryDeleteAsWorkerAsync(string path, CancellationToken ct)
    {
        if (Lock is null)
        {
            await DeleteAsWorkerAsync(path, ct);
            return true;
        }
        return await Lock.TryHoldAsync(() => DeleteAsWorkerAsync(path, ct));
    }

    /// <summary>
    /// macOS <c>/bin/ls -led</c> of <paramref name="path"/>, owner-side: its long listing line, then one line per ACL entry (what
    /// <see cref="IsClosed"/> and <see cref="WorkerSlots.SharedWith"/> read).
    /// </summary>
    internal static Task<string> ListAclAsync(string path, CancellationToken ct) => RunChecked("/bin/ls", ["-led", "--", path], ct, output: true);

    /// <summary>Runs a local program, failing on a non-zero exit; with <paramref name="output"/> its standard output is read and returned (else "").</summary>
    private static async Task<string> RunChecked(string program, IEnumerable<string> args, CancellationToken ct, bool output = false)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = output, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var p = Process.Start(psi)!;
        var stdout = output ? p.StandardOutput.ReadToEndAsync(ct) : Task.FromResult("");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} failed ({p.ExitCode}): {(await stderr).Trim()}");
        }
        return await stdout;
    }
}
