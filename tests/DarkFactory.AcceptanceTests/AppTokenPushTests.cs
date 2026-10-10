using System.Text.Json;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// sc-25176 AC, live: a per-run App token for the sandbox can push factory/*, but the
/// sandbox rulesets (`factory github-repo protect`) reject main, and the token is useless
/// against any other repository the App is installed on. Needs the registered + installed App and FACTORY_E2E=1.
/// </summary>
public class AppTokenPushTests
{
    private static readonly RepoRef Sandbox = Harness.Options.DefaultRepo;

    /// <summary>
    /// Another repo the App IS installed on but this run's token is not scoped to (FACTORY_E2E_OTHER_REPO).
    /// The final check skips unless the App is installed there, since a push to a repo outside the
    /// installation would be refused even with an unscoped token.
    /// </summary>
    private static readonly RepoRef OtherRepo =
        RepoRef.Parse(Environment.GetEnvironmentVariable("FACTORY_E2E_OTHER_REPO") ?? "michaeltrefry/dark-factory");

    [Fact]
    public async Task Worker_token_pushes_factory_branches_only_on_its_own_repo()
    {
        Harness.RequireOptIn();
        var appId = Harness.RequireSecret(o => o.GitHubAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        var ct = TestContext.Current.CancellationToken;

        using var github = OutboundHttp.GitHubApi();
        var app = new GitHubApp(github, appId, appKey, TimeProvider.System);
        var token = await app.CreateInstallationTokenAsync(Sandbox, ct);
        Assert.True(token.ExpiresAt <= DateTimeOffset.UtcNow + GitHubApp.MaxTokenLifetime + TimeSpan.FromMinutes(1));
        var auth = GitRemoteWrites.Credentials(token.Token);

        // GitHub's own view of the token's scope: exactly the sandbox, whatever else the App is installed on.
        using (var scope = GitHubApp.Request(HttpMethod.Get, "installation/repositories", "Bearer", token.Token))
        using (var scopeResponse = await github.SendAsync(scope, ct))
        {
            var body = await scopeResponse.Content.ReadAsStringAsync(ct);
            Assert.True(scopeResponse.IsSuccessStatusCode, $"GET /installation/repositories: {(int)scopeResponse.StatusCode} {body}");
            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(1, root.GetProperty("total_count").GetInt32());
            var only = Assert.Single(root.GetProperty("repositories").EnumerateArray());
            Assert.Equal(Sandbox.FullName, only.GetProperty("full_name").GetString(), ignoreCase: true);
        }

        var dir = Directory.CreateTempSubdirectory("df-e2e-push-").FullName;
        await GitWorkspace.RunGitAsync(dir, auth, GitRemoteReads.Clone(GitRemoteReads.GitHubRemote(Sandbox), "repo", shallow: true), ct);
        var repo = Path.Combine(dir, "repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "e2e-push-probe.txt"), $"{Guid.NewGuid()}\n", ct);
        await GitWorkspace.RunGitAsync(repo, null, ["add", "-A"], ct);
        await GitWorkspace.RunGitAsync(repo, null, ["-c", "user.name=dark-factory-e2e", "-c", "user.email=e2e@invalid", "commit", "-m", "e2e push probe"], ct);

        var branch = $"refs/heads/factory/e2e-push-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await GitWorkspace.RunGitAsync(repo, auth, GitRemoteWrites.PushRef("origin", $"HEAD:{branch}"), ct);
        try
        {
            await GitWorkspace.RunGitAsync(repo, auth, GitRemoteWrites.PushRef("origin", $":{branch}"), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup of the probe branch; it must not mask a real assertion failure.
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
            () => GitWorkspace.RunGitAsync(repo, auth, GitRemoteWrites.PushRef("origin", "HEAD:refs/heads/main"), ct));
        Assert.Contains("rule", main.Message, StringComparison.OrdinalIgnoreCase);

        // Only meaningful where the App is installed: there, refusal comes from the token's repo scope alone.
        using (var installed = GitHubApp.Request(HttpMethod.Get, $"repos/{OtherRepo.Owner}/{OtherRepo.Name}/installation", "Bearer", app.CreateJwt()))
        using (var installedResponse = await github.SendAsync(installed, ct))
        {
            if (!installedResponse.IsSuccessStatusCode)
            {
                Assert.Skip($"Set FACTORY_E2E_OTHER_REPO to a repo other than {Sandbox} that the App is installed on; " +
                    $"it is not installed on {OtherRepo} ({(int)installedResponse.StatusCode}), so a refused push there proves nothing about scoping.");
            }
        }
        Assert.NotEqual(Sandbox, OtherRepo);
        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => GitWorkspace.RunGitAsync(repo, auth,
            GitRemoteWrites.PushRef(GitRemoteReads.GitHubRemote(OtherRepo), $"HEAD:{branch}"), ct));
        Assert.True(other.Message.Contains("403") || other.Message.Contains("denied", StringComparison.OrdinalIgnoreCase),
            $"the push to {OtherRepo} failed for a reason other than access denial: {other.Message}");
    }
}
