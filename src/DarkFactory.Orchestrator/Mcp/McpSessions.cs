using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Mcp;

/// <summary>One session's access to the loopback MCP proxy: its <c>--mcp-config</c> file. Disposing it revokes the credential and deletes the file.</summary>
public interface IMcpSession : IAsyncDisposable
{
    /// <summary>The absolute path of the session's <c>--mcp-config</c> file.</summary>
    string ConfigPath { get; }

    /// <summary>The MCP server names the session is given (<see cref="McpServers"/>).</summary>
    IReadOnlyList<string> Servers { get; }
}

/// <summary>Opens a session's access to the loopback MCP proxy (sc-25707): planning and triage sessions only.</summary>
public interface IMcpSessions
{
    Task<IMcpSession> OpenAsync(RepoRef repo, CancellationToken ct);
}

/// <summary>
/// <see cref="IMcpSessions"/> over a running <see cref="McpProxy"/>: each session gets a fresh credential (<see cref="McpProxy.Grant"/>)
/// and its <c>--mcp-config</c> written to a file in <paramref name="directory"/> before launch, so the credential is never in argv
/// (which every local user can read through <c>ps</c>). The directory is under the work root (owner-owned, <c>0700</c>; the worker
/// user reads it through the work root's inherited read entry, <c>setup-worker-user.sh</c>), made <c>0700</c> itself, and each file is
/// created <c>0600</c>; it lives outside the session's worktree, so a session confined to its worktree cannot Read it into its
/// transcript. The file is deleted and the credential revoked when the session ends: afterwards it opens nothing. Residual: while
/// the session runs, a process that can read the file (the owner, root, or another process of the worker user — none runs then,
/// under the worker run lock) could use the read-only allowlist through the proxy.
/// </summary>
public sealed class McpProxySessions(McpProxy proxy, string directory) : IMcpSessions
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

    public async Task<IMcpSession> OpenAsync(RepoRef repo, CancellationToken ct)
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
        var grant = proxy.Grant(repo);
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
            return new Session(grant, path);
        }
        catch
        {
            grant.Dispose();
            File.Delete(path);
            throw;
        }
    }

    private sealed class Session(McpProxyGrant grant, string path) : IMcpSession
    {
        public string ConfigPath => path;
        public IReadOnlyList<string> Servers => grant.Servers;

        public ValueTask DisposeAsync()
        {
            grant.Dispose();
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
