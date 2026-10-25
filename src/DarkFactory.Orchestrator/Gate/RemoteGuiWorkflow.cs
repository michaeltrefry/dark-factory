using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>Explicit trusted-CI deployment, not a grant inferred from a source repository or runner label.</summary>
public sealed record GuiWorkflowSettings(RepoRef Collector, IReadOnlyList<RepoRef> TrustedRepositories, string ConfigurationSha256)
{
    public bool Allows(RepoRef repo) => TrustedRepositories.Any(r => r.Is(repo));
    public static GuiWorkflowSettings? Parse(string? collector, string? repositories, string? hash)
    {
        if (collector is null && repositories is null && hash is null) return null;
        RepoRef Repo(string text, string key) => Regex.IsMatch(text, @"^[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)
            ? RepoRef.Parse(text) : throw new InvalidOperationException(key + " must name an owner/name repository.");
        if (string.IsNullOrWhiteSpace(collector)) throw new InvalidOperationException("Gate:Gui:CollectorRepo is required for GUI CI.");
        if (string.IsNullOrWhiteSpace(repositories)) throw new InvalidOperationException("Gate:Gui:TrustedRepos is required for GUI CI.");
        if (hash is null || !Regex.IsMatch(hash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Gate:Gui:ConfigurationSha256 must pin the 64-character lowercase SHA-256 of the protected GUI executor configuration.");
        return new(Repo(collector, "Gate:Gui:CollectorRepo"), repositories.Split(',').Select(r => Repo(r.Trim(), "Gate:Gui:TrustedRepos"))
            .DistinctBy(r => r.Key).ToArray(), hash);
    }
}

public static class RemoteGuiWorkflow
{
    public const string Path = ".github/workflows/factory-gui-new-tests.yml";
    private static string Resource(string name)
    {
        using var stream = typeof(RemoteGuiWorkflow).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public static string Helper { get; } = Resource("factory-gui-exec");
    public static string Runner { get; } = RenderRunner();
    public static string SourceJob { get; } = RenderSourceJob();
    private static string RenderSourceJob()
    {
        var entry = Runner.LastIndexOf("if __name__ == '__main__':", StringComparison.Ordinal);
        return "#!/usr/bin/python3 -I\n" + Runner[..entry] + "\n" + Resource("factory-gui-source-job.py").Replace("GUI_HELPER_HASH",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Helper))));
    }
    private static string RenderRunner()
    {
        var runner = RemoteIosWorkflow.Runner;
        var entry = runner.LastIndexOf("if __name__ == '__main__':", StringComparison.Ordinal);
        return runner.Insert(entry, Resource("factory-gui-remote.py").Replace("GUI_HELPER_HASH",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Helper)))) + "\n");
    }
    public static string Text { get; } = Resource("factory-gui-new-tests.yml").Replace("          # FACTORY_RUNNER",
        string.Join('\n', Runner.TrimEnd().Split('\n').Select(line => "          " + line)), StringComparison.Ordinal);
    public static bool Trusted(IosSealedWorkflow workflow) => RemoteIosWorkflow.Sha(workflow.Commit)
        && !string.IsNullOrWhiteSpace(workflow.DefaultBranch) && workflow.Content == Text;
}
