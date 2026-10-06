using System.Text;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// sc-25176 AC, live: a per-run App token for the sandbox can push factory/*, but the
/// sandbox rulesets (`factory github-repo protect`) reject main, and the token is useless
/// against any other repository. Needs the registered + installed App and FACTORY_E2E=1.
/// </summary>
public class AppTokenPushTests
{
    private static readonly RepoRef Sandbox = Harness.Options.DefaultRepo;

    /// <summary>Another repo the owner has but the token is not scoped to (override with FACTORY_E2E_OTHER_REPO).</summary>
    private static readonly RepoRef OtherRepo =
        RepoRef.Parse(Environment.GetEnvironmentVariable("FACTORY_E2E_OTHER_REPO") ?? "michaeltrefry/dark-factory");

    [Fact]
    public async Task Worker_token_pushes_factory_branches_only_on_its_own_repo()
    {
        Harness.RequireOptIn();
        var appId = Harness.RequireSecret(o => o.GitHubAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        var ct = TestContext.Current.CancellationToken;

        using var github = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        var token = await new GitHubApp(github, appId, appKey, TimeProvider.System).CreateInstallationTokenAsync(Sandbox, ct);
        Assert.True(token.ExpiresAt <= DateTimeOffset.UtcNow + GitHubApp.MaxTokenLifetime + TimeSpan.FromMinutes(1));
        var auth = TokenEnvironment(token.Token);

        var dir = Directory.CreateTempSubdirectory("df-e2e-push-").FullName;
        await GitWorkspace.RunGitAsync(dir, auth, ["clone", "--depth", "1", GitWorkspace.GitHubRemote(Sandbox), "repo"], ct);
        var repo = Path.Combine(dir, "repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "e2e-push-probe.txt"), $"{Guid.NewGuid()}\n", ct);
        await GitWorkspace.RunGitAsync(repo, null, ["add", "-A"], ct);
        await GitWorkspace.RunGitAsync(repo, null, ["-c", "user.name=dark-factory-e2e", "-c", "user.email=e2e@invalid", "commit", "-m", "e2e push probe"], ct);

        var branch = $"refs/heads/factory/e2e-push-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        try
        {
            await GitWorkspace.RunGitAsync(repo, auth, ["push", "origin", $"HEAD:{branch}"], ct);
        }
        finally
        {
            await GitWorkspace.RunGitAsync(repo, auth, ["push", "origin", $":{branch}"], CancellationToken.None);
        }

        // Never risk a real commit on main: only attempt the push once GitHub confirms rules guard it.
        using (var rules = GitHubApp.Request(HttpMethod.Get, $"repos/{Sandbox.Owner}/{Sandbox.Name}/rules/branches/main", "Bearer", token.Token))
        using (var rulesResponse = await github.SendAsync(rules, ct))
        {
            var body = await rulesResponse.Content.ReadAsStringAsync(ct);
            Assert.True(rulesResponse.IsSuccessStatusCode && body.Contains("\"pull_request\"") && body.Contains("\"update\""),
                $"main on {Sandbox} is not guarded by the factory rulesets (run `factory github-repo protect {Sandbox}`): {(int)rulesResponse.StatusCode} {body}");
        }
        var main = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GitWorkspace.RunGitAsync(repo, auth, ["push", "origin", "HEAD:refs/heads/main"], ct));
        Assert.Contains("rule", main.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorkspace.RunGitAsync(repo, auth,
            ["push", GitWorkspace.GitHubRemote(OtherRepo), $"HEAD:{branch}"], ct));
    }

    /// <summary>Same env-config auth the orchestrator uses (<see cref="GitWorkspace"/>): never argv or files.</summary>
    private static Dictionary<string, string> TokenEnvironment(string token) => new()
    {
        ["GIT_CONFIG_COUNT"] = "2",
        ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
        ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"))}",
        ["GIT_CONFIG_KEY_1"] = "credential.helper",
        ["GIT_CONFIG_VALUE_1"] = "",
    };
}
