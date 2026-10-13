namespace DarkFactory.Orchestrator.Git;

/// <summary>
/// Isolation for the owner's git on repository content a PR controls (the clones and every worktree: fetch, checkout,
/// merge, commit, push). Git then reads only the clone's own config, which the factory alone writes: not the owner's system or
/// global config (whose hooks path, filter or merge drivers, rerere or fsmonitor a PR's <c>.gitattributes</c> or files could
/// otherwise select or reach), no hooks (a relative <c>core.hooksPath</c> would resolve inside the worktree), no fsmonitor,
/// no rerere, and no LFS filter (nothing a PR names is run to smudge or clean a file). Nor does any call leave a process behind
/// (sc-25987): <c>fetch</c>, <c>commit</c> and <c>merge</c> end with automatic maintenance (<c>gc --auto</c>: <c>pack-refs</c>,
/// <c>worktree prune</c>), which git by default detaches — it would outlive the call, and so the holder of the clone's
/// <see cref="CloneLock"/>, and write the shared clone's refs and worktree list with no lock held. <c>gc.autoDetach=false</c> and
/// <c>maintenance.autoDetach=false</c> (git 2.47+, which otherwise falls back to the former) keep it inside the call, under
/// the lock; maintenance still runs. <see cref="GitWorkspace"/> applies it
/// to every git call it makes; nothing else in the factory runs git as the owner.
/// </summary>
public static class OwnerGit
{
    /// <summary>Environment for every owner-side git call: no system config, an empty global config, no LFS smudging.</summary>
    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_LFS_SKIP_SMUDGE"] = "1",
    };

    /// <summary>Global options (ahead of the subcommand) for every owner-side git call.</summary>
    public static readonly IReadOnlyList<string> ConfigArgs =
    [
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.fsmonitor=false",
        "-c", "rerere.enabled=false",
        "-c", "filter.lfs.smudge=",
        "-c", "filter.lfs.clean=",
        "-c", "filter.lfs.process=",
        "-c", "filter.lfs.required=false",
        "-c", "gc.autoDetach=false",
        "-c", "maintenance.autoDetach=false",
    ];

    /// <summary><paramref name="env"/> added to <see cref="Environment"/> (it may not override it), and <see cref="ConfigArgs"/> ahead of <paramref name="args"/>.</summary>
    public static (Dictionary<string, string> Env, string[] Args) Isolate(IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args)
    {
        var merged = new Dictionary<string, string>(Environment);
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            if (Environment.ContainsKey(key))
            {
                throw new ArgumentException($"{key} is set by the owner-side git isolation and cannot be overridden.", nameof(env));
            }
            merged[key] = value;
        }
        return (merged, [.. ConfigArgs, .. args]);
    }
}
