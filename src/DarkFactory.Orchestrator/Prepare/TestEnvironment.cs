using System.Text;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>Commit-owned test inputs, never credentials or owner cache paths. Mirrored by the sealed remote engine and gate wrapper.</summary>
public static class TestEnvironment
{
    public const int MaxVariables = 32;
    public const int MaxValueChars = 1024;
    public const int MaxBytes = 8192;
    private const string Reserved = "PATH HOME TMPDIR TMP TEMP USER LOGNAME SHELL IFS ENV BASH_ENV PS4 SHELLOPTS BASHOPTS CDPATH PWD OLDPWD ZDOTDIR DEVELOPER_DIR SDKROOT XCODE_XCCONFIG_FILE CFFIXED_USER_HOME MSBUILDDISABLENODEREUSE DOTNET_CLI_USE_MSBUILD_SERVER USESHAREDCOMPILATION";
    private static readonly string[] Prefixes = ["DYLD_", "LD_", "ANTHROPIC_", "OPENAI_", "ROUTER_", "FACTORY_", "GITHUB_", "ACTIONS_", "RUNNER_", "TEST_RUNNER_", "BASH_FUNC_"];
    private static readonly string[] Secrets = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH", "API_KEY", "PRIVATE_KEY", "ACCESS_KEY"];
    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > MaxVariables) throw Fail("test_env", $"at most {MaxVariables} variables are allowed");
        var bytes = 0;
        foreach (var (key, value) in values)
        {
            var upper = key.ToUpperInvariant();
            if (key.Any(c => c < 32 || c > 126) || !Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)
                || Reserved.Split(' ').Contains(upper, StringComparer.Ordinal) || Prefixes.Any(upper.StartsWith)
                || Secrets.Any(upper.Contains)) throw Fail(key, "reserved, loader, shell, provider or credential name");
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxBytes) throw Fail(key, $"test_env exceeds {MaxBytes} bytes");
            if (value.Length > MaxValueChars || value.Any(c => c < 32 || c > 126) || value.Contains("__CURRENT_VALUE__", StringComparison.Ordinal)
                || !ValidValue(value)) throw Fail(key, "expected a bounded literal, fixed executable path or ${worktree}/ path (no owner paths, controls or traversal)");
        }
    }

    private static bool ValidValue(string value)
    {
        if (value.StartsWith(PrepareRecipe.WorktreePlaceholder + "/", StringComparison.Ordinal))
        {
            var path = value[(PrepareRecipe.WorktreePlaceholder.Length + 1)..];
            return Regex.IsMatch(path, "^[A-Za-z0-9_. /-]+$", RegexOptions.CultureInvariant) && path.Split('/').All(p => p is not "" and not "." and not "..");
        }
        return Regex.IsMatch(value, "^(?:(?:/usr/bin|/bin|/usr/local/bin|/opt/homebrew/bin)/[A-Za-z0-9_+.-]+|[A-Za-z0-9 _.,:+@%=-]*)$", RegexOptions.CultureInvariant)
            && !value.EndsWith("/.", StringComparison.Ordinal) && !value.EndsWith("/..", StringComparison.Ordinal);
    }

    // Canonical destinations, not merely allowlisted symlink spellings. Homebrew's Intel/ARM Cellar and the .NET
    // installation are legitimate tool roots; Apple's signed Cryptex aliases are explicit, never all of /System or /usr/local.
    private static readonly string[] TrustedExecutableRoots = ["/bin", "/usr/bin", "/usr/local/bin", "/usr/local/libexec", "/usr/local/Cellar", "/usr/local/share/dotnet", "/opt/homebrew/bin", "/opt/homebrew/Cellar", "/opt/homebrew/libexec", "/opt/homebrew/Library/Homebrew", "/System/Cryptexes/App/usr/bin", "/System/Volumes/Preboot/Cryptexes/OS/usr/bin"];

    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string> values, string worktree) =>
        Resolve(values, worktree, SandboxTestRunner.RealPath, TrustedExecutableRoots);

    // Filesystem injection for isolated tests; no configuration or descriptor can change the production roots/resolver.
    internal static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string> values, string worktree,
        Func<string, string> canonicalExecutable, IReadOnlyList<string> trustedExecutableRoots)
    {
        Validate(values);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var root = SandboxTestRunner.RealPath(worktree).TrimEnd('/');
        foreach (var (key, value) in values)
        {
            var resolved = value.Replace(PrepareRecipe.WorktreePlaceholder, root, StringComparison.Ordinal);
            if (value.StartsWith(PrepareRecipe.WorktreePlaceholder, StringComparison.Ordinal))
            {
                var current = root;
                foreach (var part in value[(PrepareRecipe.WorktreePlaceholder.Length + 1)..].Split('/'))
                {
                    current = System.IO.Path.Combine(current, part);
                    if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                        throw Fail(key, "workspace path contains a symbolic link");
                }
                if (!SandboxTestRunner.RealPath(resolved).StartsWith(root + "/", StringComparison.Ordinal))
                    throw Fail(key, "workspace path resolves outside the selected worktree");
            }
            else if (resolved.StartsWith('/'))
            {
                try
                {
                    resolved = canonicalExecutable(resolved);
                    if (!trustedExecutableRoots.Any(trusted => resolved.StartsWith(trusted.TrimEnd('/') + "/", StringComparison.Ordinal)))
                        throw Fail(key, "fixed executable resolves outside trusted system/tool installation roots");
                    if (!File.Exists(resolved) || (File.GetUnixFileMode(resolved) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                        throw Fail(key, "fixed executable does not exist or is not executable");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { throw Fail(key, "fixed executable cannot be resolved and verified"); }
            }
            result.Add(key, resolved);
        }
        return result;
    }

    private static PrepareRecipeException Fail(string key, string why) => new($"{PrepareRecipe.Path}: test_env.{key}: {why}.");
}
