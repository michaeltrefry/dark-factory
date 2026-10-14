using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Mcp;

/// <summary>One session's access to the loopback MCP proxy: its <c>--mcp-config</c> file. Disposing it revokes the credential and deletes the file.</summary>
public interface IMcpSession : IAsyncDisposable
{
    /// <summary>The absolute path of the session's <c>--mcp-config</c> file.</summary>
    string ConfigPath { get; }

    /// <summary>The MCP server names the session is given (<see cref="McpServers"/>).</summary>
    IReadOnlyList<string> Servers { get; }

    /// <summary>The upstream tools the session is granted, as allow rules (<c>mcp__&lt;server&gt;__&lt;tool&gt;</c>).</summary>
    IReadOnlyList<string> ToolRules { get; }
}

/// <summary>Opens a session's access to the loopback MCP proxy (sc-25707): planning and triage sessions only.</summary>
public interface IMcpSessions
{
    /// <summary>Opens a session about <paramref name="repo"/> granted exactly <paramref name="profile"/>'s tools.</summary>
    Task<IMcpSession> OpenAsync(RepoRef repo, McpProfile profile, CancellationToken ct);

    /// <summary>
    /// Opens a session about every repository of <paramref name="repos"/> (sc-25874: a planner's, the only profile granted more than
    /// one, <see cref="McpProxy.Grant(IReadOnlyList{RepoRef}, McpProfile)"/>).
    /// </summary>
    Task<IMcpSession> OpenAsync(IReadOnlyList<RepoRef> repos, McpProfile profile, CancellationToken ct) => repos.Count == 1
        ? OpenAsync(repos[0], profile, ct)
        : throw new NotSupportedException("These MCP sessions are about exactly one repository.");
}

/// <summary>
/// <see cref="IMcpSessions"/> over a running <see cref="McpProxy"/>: each session gets a fresh credential (<see cref="McpProxy.Grant"/>)
/// and its <c>--mcp-config</c> written to a file in <paramref name="directory"/> before launch, so the credential is never in argv
/// (which every local user can read through <c>ps</c>). The directory is under the work root (owner-owned, <c>0700</c>; the worker
/// user reads it through the work root's inherited read entry, <c>setup-worker-user.sh</c>), made <c>0700</c> itself, and each file is
/// created <c>0600</c>; it lives outside the session's worktree, so a session confined to its worktree cannot Read it into its
/// transcript. With <paramref name="workerUser"/> (<c>Worker:RunAs</c>; null for <c>none</c>), once the file is written the owner
/// checks that the directory's ACL lets that user search it and the file's lets it read it (<see cref="Worker.WorkerSandbox.Grants"/>, from
/// <c>/bin/ls -led</c>, <see cref="Worker.WorkerSandbox.ListAclAsync"/>: the one ACL reader and parser, sc-25992), else the session is not started (<see cref="FactoryUnavailableException"/>: re-run the setup). The file is
/// deleted and the credential revoked when the session ends: afterwards it opens nothing. Residual: while the session runs, a
/// process that can read the file (the owner, root, or another process of the worker user — none runs then, under the worker run
/// lock) could use the session's granted tools through the proxy. With <paramref name="owner"/> (the item whose run opens the
/// sessions, e.g. <c>sc-7</c>) each file is named <c>&lt;owner&gt;.&lt;guid&gt;.json</c>, so another run's <see cref="SweepAsync"/>
/// knows whose run lock guards it (sc-25987); without one the file is nobody's and any sweep deletes it.
/// <para>With several worker slots (sc-25990, E7) each slot has its own directory (<see cref="DirectoryOf"/>) that only its own
/// user and the owner can read: the work root's inherited read entry names every slot's user, so <paramref name="restrict"/>
/// (<see cref="Worker.WorkerSandbox.RestrictToWorkerAsync(string, CancellationToken)"/>: owner-side, no root) first strips the
/// directory of every entry and grants <paramref name="workerUser"/> alone search on it and read on the files made in it; and
/// once the file is written both must read back (<c>/bin/ls -led</c>) open to that user and to nobody else but the owner
/// (<see cref="Worker.WorkerSandbox.OpenToOthers"/>: no other slot's user, no other principal, no group or other mode bit).
/// Either failing is a broken set-up, not an item's failure: the session is not started and its file and credential are gone
/// (<see cref="FactoryUnavailableException"/>). So the residual above is per slot: the other process could only be one of the
/// same slot's user, and none runs while the session holds that slot's lock; another slot's worker cannot read the file.
/// Without <paramref name="restrict"/> (one slot, as before slots) the directory is as it always was.</para>
/// </summary>
public sealed class McpProxySessions(McpProxy proxy, string directory, string? workerUser = null,
    Func<string, CancellationToken, Task<string>>? readAcl = null, ItemRef? owner = null,
    Func<string, CancellationToken, Task>? restrict = null) : IMcpSessions
{
    /// <summary>The work root's directory of session <c>--mcp-config</c> files (slot 1's; slot n's: <see cref="DirectoryOf"/>).</summary>
    public const string DirectoryName = "mcp-sessions";

    /// <summary>
    /// Worker slot <paramref name="slot"/>'s directory of session files under <paramref name="workRoot"/> (sc-25990):
    /// <c>mcp-sessions</c> for slot 1, where it was before slots, then <c>mcp-sessions-2</c>, … — each read by its own slot's
    /// user and the owner only, and swept only by a session of that slot.
    /// </summary>
    public static string DirectoryOf(string workRoot, int slot) => Path.Combine(workRoot, Worker.WorkerSlots.DirectoryOf(DirectoryName, slot));

    /// <summary>How long a listing that must name one slot's user alone waits before it is read again (tests give their own).</summary>
    internal TimeSpan? AclRetry { get; init; }

    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; // 0700
    private const UnixFileMode ConfigFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600

    /// <summary>
    /// Deletes every file a crashed process left in <paramref name="directory"/> (its credentials died with that process's proxy),
    /// and none a live run's session is using (sc-25987, E6): a file is deleted only under <paramref name="live"/>'s claim on the
    /// item it is named for (<see cref="ItemNaming.OwnerOfSessionFile"/>), and left alone while another run holds that item's run lock.
    /// </summary>
    public static async Task SweepAsync(string directory, Ledger.LiveRuns live, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory).ToList())
        {
            await using var claim = await live.TryClaimAsync(ItemNaming.OwnerOfSessionFile(Path.GetFileName(file)), ct);
            if (claim is not null)
            {
                File.Delete(file);
            }
        }
    }

    public Task<IMcpSession> OpenAsync(RepoRef repo, McpProfile profile, CancellationToken ct) => OpenAsync([repo], profile, ct);

    public async Task<IMcpSession> OpenAsync(IReadOnlyList<RepoRef> repos, McpProfile profile, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, DirectoryMode);
            File.SetUnixFileMode(directory, DirectoryMode);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }
        if (restrict is not null)
        {
            // Before anything is written into it: this slot's user alone reads what is made here from now on.
            try
            {
                await restrict(Path.GetFullPath(directory), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FactoryUnavailableException(
                    $"the MCP session config: {Path.GetFullPath(directory)} could not be made {workerUser ?? "its slot's user"}'s alone to read: {ex.Message}", ex);
            }
        }
        var grant = proxy.Grant(repos, profile);
        var path = Path.Combine(Path.GetFullPath(directory), $"{(owner is null ? "" : $"{owner}.")}{Guid.NewGuid():N}.json");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = ConfigFileMode;
            }
            await using (var stream = new FileStream(path, options))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(grant.McpConfigJson().AsMemory(), ct);
            }
            if (workerUser is not null)
            {
                await EnsureWorkerCanReadAsync(workerUser, Path.GetFullPath(directory), path, readAcl ?? Worker.WorkerSandbox.ListAclAsync, ct, alone: restrict is not null,
                    AclRetry);
            }
            return new Session(grant, path);
        }
        catch
        {
            grant.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Fails (<see cref="FactoryUnavailableException"/>) unless <paramref name="user"/> holds an ACL <c>search</c> entry on
    /// <paramref name="dir"/> and a <c>read</c> entry on <paramref name="file"/>: the 0700/0600 modes leave the worker user only the
    /// work root's inherited ACL, which <c>setup-worker-user.sh</c> sets. With <paramref name="alone"/> (a slot's own directory,
    /// sc-25990) it also fails unless each is open to nobody but <paramref name="user"/> and the owner
    /// (<see cref="Worker.WorkerSandbox.OpenToOthers"/>): another slot's user must not be able to read this session's credential.
    /// </summary>
    internal static async Task EnsureWorkerCanReadAsync(string user, string dir, string file,
        Func<string, CancellationToken, Task<string>> readAcl, CancellationToken ct, bool alone = false, TimeSpan? retry = null)
    {
        foreach (var (path, right) in new[] { (dir, "search"), (file, "read") })
        {
            var acl = "";
            // One retry rule with every other sandbox ACL check (Worker.WorkerSandbox.RestrictToWorkerAsync): ls prints an entry's
            // UUID instead of its user name while the directory service does not answer, so a listing that does not show what it
            // must is read again, up to WorkerSandbox.AclReads times, before it fails the session.
            for (var read = 1; ; read++)
            {
                try
                {
                    acl = await readAcl(path, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new FactoryUnavailableException($"the MCP session config: cannot read the ACL of {path}: {ex.Message}", ex);
                }
                if ((Worker.WorkerSandbox.Grants(acl, user, right) && (!alone || Worker.WorkerSandbox.OpenToOthers(acl, user) is null))
                    || read >= Worker.WorkerSandbox.AclReads)
                {
                    break;
                }
                await Task.Delay(retry ?? Worker.WorkerSandbox.AclRetry, ct);
            }
            if (!Worker.WorkerSandbox.Grants(acl, user, right))
            {
                var why = $"{path} grants {user} no ACL '{right}' entry, so the worker cannot read its MCP configuration; re-run `sudo scripts/setup-worker-user.sh`";
                throw new FactoryUnavailableException($"the MCP session config: {why}", new InvalidOperationException(why));
            }
            if (alone && Worker.WorkerSandbox.OpenToOthers(acl, user) is { } others)
            {
                var why = $"{path} must be readable by {user} (its worker slot's user) and the owner alone, but {others}, "
                    + $"so another slot's worker could read this session's MCP credential (ls -led: {acl.Trim().ReplaceLineEndings("; ")})";
                throw new FactoryUnavailableException($"the MCP session config: {why}", new InvalidOperationException(why));
            }
        }
    }

    private sealed class Session(McpProxyGrant grant, string path) : IMcpSession
    {
        public string ConfigPath => path;
        public IReadOnlyList<string> Servers => grant.Servers;
        public IReadOnlyList<string> ToolRules => grant.ToolRules;

        public ValueTask DisposeAsync()
        {
            grant.Dispose();
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
