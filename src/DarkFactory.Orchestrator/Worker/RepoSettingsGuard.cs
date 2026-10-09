using System.Text.Json;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// The worktree's own Claude settings must not change what a worker session sends to the router (E8, E5). An implementing worker
/// loads the target repo's <c>.claude/settings.json</c> and <c>.claude/settings.local.json</c> (<c>--setting-sources project,local</c>),
/// and their <c>env</c> outranks the environment the factory gives it: an <c>env.ANTHROPIC_CUSTOM_HEADERS</c> would drop the model
/// class or add <c>x-weave-force-model</c>, an <c>env.ANTHROPIC_BASE_URL</c> or <c>apiKeyHelper</c> would change the endpoint or the
/// credential, <c>model</c> would pin a model. The factory's own <c>--settings</c> outranks project settings but can only add values
/// (it cannot unset a project <c>env</c> without putting the router key in argv), so the orchestrator checks the files instead.
/// <para>
/// Fail closed, by allowlist: a settings file may hold only <see cref="AllowedKeys"/> at its top level — keys that cannot change the
/// model, the endpoint, the headers or the credentials. Anything else (<c>env</c>, <c>model</c>, <c>apiKeyHelper</c>, an unknown or
/// future key), a file that is not a JSON object, cannot be read, is a symlink or not a regular file, or a <c>.claude</c> that is a
/// symlink, refuses the session. Nothing follows a link: the check never reads through one.
/// </para>
/// </summary>
public static class RepoSettingsGuard
{
    /// <summary>The top-level settings keys a worktree's Claude settings may hold: none changes the model, endpoint, headers or credentials.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema", "permissions", "hooks", "disableAllHooks", "includeCoAuthoredBy", "includeGitInstructions", "attribution",
        "cleanupPeriodDays", "outputStyle", "statusLine", "spinnerTipsEnabled", "respectGitignore", "alwaysThinkingEnabled",
        "enableAllProjectMcpServers", "enabledMcpjsonServers", "disabledMcpjsonServers",
    };

    /// <summary>Why a worker session may not start in <paramref name="worktree"/>, or null when its Claude settings are safe.</summary>
    public static string? Refusal(string worktree)
    {
        var directory = Path.Combine(worktree, ".claude");
        if (IsLink(directory))
        {
            return ".claude is a symlink";
        }
        if (!Directory.Exists(directory))
        {
            return null;
        }
        foreach (var file in Taint.RepoSettingsFiles)
        {
            // Never follows a link, never blocks on a FIFO, bounded in size (SafeFile).
            var read = SafeFile.Read(Path.Combine(worktree, file));
            if (read.IsMissing)
            {
                continue;
            }
            if (read.Refusal is { } refusal)
            {
                return $"{file} {refusal}";
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(read.Text!);
            }
            catch (JsonException ex)
            {
                return $"{file} cannot be read as JSON ({ex.GetType().Name})";
            }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return $"{file} is not a JSON object";
                }
                var refused = document.RootElement.EnumerateObject().Select(p => p.Name).Where(k => !AllowedKeys.Contains(k)).Distinct().ToList();
                if (refused.Count > 0)
                {
                    return $"{file} sets {string.Join(", ", refused.Take(5).Select(Shown))}, which could change the model, endpoint, headers or "
                        + "credentials a worker sends";
                }
            }
        }
        return null;
    }

    /// <summary>A key as the refusal names it (it is the repo's text and lands in a board comment): plain characters only, bounded.</summary>
    private static string Shown(string key) =>
        $"'{new string(key.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '$').Take(64).ToArray())}'";

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
