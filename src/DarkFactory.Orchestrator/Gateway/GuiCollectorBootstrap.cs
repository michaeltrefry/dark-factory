using System.Text;

namespace DarkFactory.Orchestrator.Gateway;

public sealed partial class GuiCollectorDeployment
{
    internal sealed class Bootstrap(string directory, string script) : IDisposable
    {
        public string Directory { get; } = directory;
        public string Script { get; } = script;
        public void Dispose()
        {
            // Only this owner-created, never source-writable directory is removed; no downloaded code executes in it.
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static byte[] BootstrapBytes()
    {
        using var stream = typeof(GuiCollectorDeployment).Assembly.GetManifestResourceStream("setup-gui-collector.py")
            ?? throw new InvalidOperationException("Reviewed collector installer is not compiled into this factory.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    internal static async Task<Bootstrap> StageBootstrapAsync(CancellationToken ct, string? parent = null,
        Func<string, CancellationToken, Task>? close = null)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Compiled root bootstrap staging requires Unix private modes.");
        parent ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dark-factory", "gui-collector-bootstrap");
        parent = Path.GetFullPath(parent);
        for (DirectoryInfo? directory = new(parent); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new InvalidOperationException("Root bootstrap parent must be canonical, never a source-worktree symlink.");
        System.IO.Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var stage = Path.Combine(parent, "bootstrap-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var script = Path.Combine(stage, "setup-gui-collector.py");
        var bootstrap = new Bootstrap(stage, script);
        try
        {
            close ??= Worker.WorkerSandbox.CloseDirectoryToOwnerAsync;
            await close(stage, ct);
            if (new DirectoryInfo(stage).LinkTarget is not null || System.IO.Directory.EnumerateFileSystemEntries(stage).Any())
                throw new InvalidOperationException("Root bootstrap staging must be a fresh owner-private directory.");
            using var output = new FileStream(script, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            output.Write(BootstrapBytes());
            output.Flush(true);
            return bootstrap;
        }
        catch { bootstrap.Dispose(); throw; }
    }
}
