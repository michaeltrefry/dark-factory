using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The factory's git remote reads of GitHub (sc-25391): the github.com remote of a repository and the arguments of every clone and
/// fetch of it. They authenticate with an installation token through <see cref="GitRemoteWrites.Credentials"/> (git's
/// environment-based config, never argv, remotes or files); <c>GitWorkspace</c> runs them. The gateway lint (DF0004) fails the build
/// on a github.com git remote — a <c>.git</c> URL, an <c>@github.com</c> user, git's <c>http.https://github.com/</c> config — named
/// anywhere else in the orchestrator or the acceptance tests.
/// </summary>
public static class GitRemoteReads
{
    /// <summary><c>https://github.com/&lt;owner&gt;/&lt;name&gt;.git</c>.</summary>
    public static string GitHubRemote(RepoRef repo) => $"https://github.com/{repo.Owner}/{repo.Name}.git";

    /// <summary><c>clone &lt;remote&gt; &lt;directory&gt;</c>, shallow (<c>--depth 1</c>) when <paramref name="shallow"/>.</summary>
    public static string[] Clone(string remote, string directory, bool shallow = false) =>
        ["clone", .. (shallow ? ["--depth", "1"] : Array.Empty<string>()), remote, directory];

    /// <summary><c>fetch --prune origin</c>.</summary>
    public static string[] Fetch() => ["fetch", "--prune", "origin"];

    /// <summary><c>remote set-head origin --auto</c>: refreshes <c>origin/HEAD</c> from the remote.</summary>
    public static string[] RefreshHead() => ["remote", "set-head", "origin", "--auto"];
}
