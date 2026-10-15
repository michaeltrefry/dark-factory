using System.Security.Cryptography;
using System.Text;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>
/// The factory's <c>factory-prepare</c> command (sc-26232; <c>scripts/factory-prepare</c>): installed root-owned and unedited by
/// <c>setup-worker-user.sh</c> at <see cref="InstalledPath"/> (one copy for every slot: it pins nothing per user), the one way an
/// implementing worker prepares its worktree from the repository's recipe — allowed by exactly one rule naming that absolute path
/// (<see cref="AllowRule"/>, only with <c>Worker:Prepare</c>). It asks the harness, through the session's request spool, to fetch
/// the recipe's artifacts (it downloads nothing itself and no worker gains a network-reaching tool), waits for the answer, and runs
/// the recipe's steps as the worker. With <c>Worker:Prepare</c> on, start-up and every run's start require the installed copy
/// to be the repo's (<see cref="EnsureCurrentAsync"/>).
/// </summary>
public static class PrepareTool
{
    public const string InstalledPath = "/usr/local/libexec/dark-factory/factory-prepare";

    /// <summary>The worker's allow rule: the installed absolute path (a plain name would resolve through the worker's own writable <c>~/.local/bin</c>).</summary>
    public const string AllowRule = $"Bash({InstalledPath}:*)";

    private static readonly Lazy<string> Compiled = new(() =>
    {
        using var stream = typeof(PrepareTool).Assembly.GetManifestResourceStream("factory-prepare")
            ?? throw new InvalidOperationException("factory-prepare is not compiled into the orchestrator.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Why <paramref name="installed"/> is not the repo's <c>scripts/factory-prepare</c> (by SHA-256; <paramref name="source"/> null: the compiled-in copy), or null.</summary>
    public static string? StaleReason(string installed, string? source = null)
    {
        var (have, want) = (Sha256(installed), Sha256(source ?? Compiled.Value));
        return have == want ? null : $"its text differs from the repo's scripts/factory-prepare (sha256 {have[..12]}…, expected {want[..12]}…)";
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Reads the installed copy at <paramref name="path"/>; throws naming the setup command when it is missing or stale.</summary>
    public static async Task EnsureCurrentAsync(string reinstall, CancellationToken ct, string path = InstalledPath, string? source = null)
    {
        string installed;
        try
        {
            installed = await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Worker:Prepare: cannot read the installed factory-prepare {path} ({ex.Message}); run `{reinstall}` to install it, or set Worker:Prepare=false.", ex);
        }
        if (StaleReason(installed, source) is { } stale)
        {
            throw new InvalidOperationException($"Worker:Prepare: the installed factory-prepare {path} is stale ({stale}); re-run `{reinstall}` to install it.");
        }
    }
}
