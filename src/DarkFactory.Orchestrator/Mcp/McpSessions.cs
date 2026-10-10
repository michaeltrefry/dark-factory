using System.Diagnostics;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Shortcut;
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
}

/// <summary>
/// <see cref="IMcpSessions"/> over a running <see cref="McpProxy"/>: each session gets a fresh credential (<see cref="McpProxy.Grant"/>)
/// and its <c>--mcp-config</c> written to a file in <paramref name="directory"/> before launch, so the credential is never in argv
/// (which every local user can read through <c>ps</c>). The directory is under the work root (owner-owned, <c>0700</c>; the worker
/// user reads it through the work root's inherited read entry, <c>setup-worker-user.sh</c>), made <c>0700</c> itself, and each file is
/// created <c>0600</c>; it lives outside the session's worktree, so a session confined to its worktree cannot Read it into its
/// transcript. With <paramref name="workerUser"/> (<c>Worker:RunAs</c>; null for <c>none</c>), once the file is written the owner
/// checks that the directory's ACL lets that user search it and the file's lets it read it (<see cref="AclGrants"/>, from
/// <c>/bin/ls -led</c>), else the session is not started (<see cref="FactoryUnavailableException"/>: re-run the setup). The file is
/// deleted and the credential revoked when the session ends: afterwards it opens nothing. Residual: while the session runs, a
/// process that can read the file (the owner, root, or another process of the worker user — none runs then, under the worker run
/// lock) could use the session's granted tools through the proxy.
/// </summary>
public sealed partial class McpProxySessions(McpProxy proxy, string directory, string? workerUser = null,
    Func<string, CancellationToken, Task<string>>? readAcl = null) : IMcpSessions
{
    /// <summary>The work root's directory of session <c>--mcp-config</c> files.</summary>
    public const string DirectoryName = "mcp-sessions";

    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; // 0700
    private const UnixFileMode ConfigFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600

    /// <summary>
    /// Deletes every file a crashed process left in <paramref name="directory"/> (its credentials died with that process's proxy). Run
    /// only while no session of this kind runs (the triage holds the worker run lock).
    /// </summary>
    public static void Sweep(string directory)
    {
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
            }
        }
    }

    public async Task<IMcpSession> OpenAsync(RepoRef repo, McpProfile profile, CancellationToken ct)
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
        var grant = proxy.Grant(repo, profile);
        var path = Path.Combine(Path.GetFullPath(directory), $"{Guid.NewGuid():N}.json");
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
                await EnsureWorkerCanReadAsync(workerUser, Path.GetFullPath(directory), path, readAcl ?? ListAclAsync, ct);
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
    /// work root's inherited ACL, which <c>setup-worker-user.sh</c> sets.
    /// </summary>
    internal static async Task EnsureWorkerCanReadAsync(string user, string dir, string file,
        Func<string, CancellationToken, Task<string>> readAcl, CancellationToken ct)
    {
        foreach (var (path, right) in new[] { (dir, "search"), (file, "read") })
        {
            string acl;
            try
            {
                acl = await readAcl(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FactoryUnavailableException($"the MCP session config: cannot read the ACL of {path}: {ex.Message}", ex);
            }
            if (!AclGrants(acl, user, right))
            {
                var why = $"{path} grants {user} no ACL '{right}' entry, so the worker cannot read its MCP configuration; re-run `sudo scripts/setup-worker-user.sh`";
                throw new FactoryUnavailableException($"the MCP session config: {why}", new InvalidOperationException(why));
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="lsOutput"/> (macOS <c>ls -led</c>: one line per ACL entry, e.g. <c> 0: user:_factory inherited allow
    /// read,readattr</c>) has an <c>allow</c> entry for <paramref name="user"/> listing <paramref name="right"/> and no <c>deny</c> entry
    /// for that user listing it.
    /// </summary>
    public static bool AclGrants(string lsOutput, string user, string right)
    {
        var (allowed, denied) = (false, false);
        foreach (Match m in AclEntry().Matches(lsOutput))
        {
            if (m.Groups["user"].Value != user || !m.Groups["rights"].Value.Split(',').Contains(right, StringComparer.Ordinal))
            {
                continue;
            }
            if (m.Groups["kind"].Value == "deny")
            {
                denied = true;
            }
            else
            {
                allowed = true;
            }
        }
        return allowed && !denied;
    }

    [GeneratedRegex(@"^\s*\d+:\s+user:(?<user>\S+)\s+(?:inherited\s+)?(?<kind>allow|deny)\s+(?<rights>\S+)", RegexOptions.Multiline)]
    private static partial Regex AclEntry();

    /// <summary>The ACL of <paramref name="path"/> as <c>/bin/ls -led</c> prints it (a local program, owner-side).</summary>
    private static async Task<string> ListAclAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/ls") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-led", "--", path })
        {
            psi.ArgumentList.Add(arg);
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"/bin/ls failed ({p.ExitCode}): {(await stderr).Trim()}");
        }
        return await stdout;
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
