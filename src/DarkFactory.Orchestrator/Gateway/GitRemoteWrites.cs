using System.Text;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The factory's only git remote writes and git credentials (E2, sc-25390): the arguments of every <c>git push</c> (a
/// worker's branch, the merge queue's base-update push) and the environment that authenticates git to GitHub with an
/// installation token (through git's environment-based config, so the token never appears in argv, remotes or files).
/// <c>GitWorkspace</c> runs them; the gateway lint (DF0002) fails the build on a <c>push</c> or <c>remote set-url</c>
/// argument anywhere else in the orchestrator or the acceptance tests.
/// </summary>
public static class GitRemoteWrites
{
    /// <summary>
    /// <c>git &lt;tree&gt; push [--force] origin HEAD:refs/heads/&lt;branch&gt;</c>: <paramref name="tree"/> are the global options
    /// naming the git dir and work tree.
    /// </summary>
    public static string[] Push(IReadOnlyList<string> tree, string branch, bool force) =>
        [.. tree, "push", .. (force ? ["--force"] : Array.Empty<string>()), "origin", $"HEAD:refs/heads/{branch}"];

    /// <summary>
    /// <c>push &lt;remote&gt; &lt;refspec&gt;</c>: the live acceptance test's probe pushes (and deletes, <c>:&lt;ref&gt;</c>) of an
    /// App token's reach (<c>AppTokenPushTests</c>).
    /// </summary>
    public static string[] PushRef(string remote, string refspec) => ["push", remote, refspec];

    /// <summary>
    /// Environment that sends <paramref name="token"/> (an installation token) to github.com as basic auth and switches off
    /// any credential helper, so the owner's own identity is never substituted.
    /// </summary>
    public static Dictionary<string, string> Credentials(string token)
    {
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"));
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}",
            // Don't let the owner's credential helper substitute their own identity.
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = "",
        };
    }
}
