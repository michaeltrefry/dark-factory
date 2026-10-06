using System.Collections;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

public class StreamJsonTests
{
    [Fact]
    public void Captures_session_id_from_init_and_successful_result()
    {
        var state = new StreamJsonState();
        state.Accept("""{"type":"system","subtype":"init","session_id":"0b7c6a3e-1111-2222-3333-444455556666","tools":[]}""");
        state.Accept("""{"type":"assistant","message":{"content":[]},"session_id":"0b7c6a3e-1111-2222-3333-444455556666"}""");
        state.Accept("""{"type":"result","subtype":"success","is_error":false,"session_id":"0b7c6a3e-1111-2222-3333-444455556666","total_cost_usd":0.01}""");

        Assert.Equal("0b7c6a3e-1111-2222-3333-444455556666", state.SessionId);
        Assert.True(state.SawResult);
        Assert.False(state.ResultIsError);
        Assert.Equal("success", state.ResultSubtype);
    }

    [Fact]
    public void Flags_error_results_and_ignores_noise()
    {
        var state = new StreamJsonState();
        state.Accept("");
        state.Accept("not json");
        state.Accept("{broken");
        state.Accept("[1,2]");
        state.Accept("""{"type":"result","subtype":"error_max_turns","is_error":true,"result":"Not logged in","session_id":"s-1"}""");

        Assert.Equal("s-1", state.SessionId);
        Assert.True(state.ResultIsError);
        Assert.Equal("error_max_turns", state.ResultSubtype);
        Assert.Equal("Not logged in", state.ResultText);
    }

    [Fact]
    public void Without_result_line_nothing_is_reported_as_seen()
    {
        var state = new StreamJsonState();
        state.Accept("""{"type":"system","subtype":"init","session_id":"s-2"}""");
        Assert.False(state.SawResult);
    }
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class ClaudeWorkerTests
{
    private static readonly Uri Router = new("http://localhost:8080/");

    private static Hashtable ParentEnvironment() => new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = "/Users/someone",
        ["USER"] = "someone",
        ["ANTHROPIC_API_KEY"] = "sk-ant-provider",
        ["ANTHROPIC_AUTH_TOKEN"] = "provider-token",
        ["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com",
        ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Other: leak",
        ["OPENAI_API_KEY"] = "sk-openai",
        ["AWS_SECRET_ACCESS_KEY"] = "aws",
        ["GH_TOKEN"] = "ghp_owner",
        ["GITHUB_TOKEN"] = "ghp_owner",
        ["SHORTCUT_API_TOKEN"] = "sc",
        ["FACTORY_ROUTER_KEY"] = "rk_parent",
        ["CLAUDECODE"] = "1",
        ["CLAUDE_CODE_SESSION_ID"] = "parent-session",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth",
    };

    [Fact]
    public void Environment_carries_only_router_url_and_router_key_header()
    {
        var env = ClaudeWorker.BuildEnvironment(ParentEnvironment(), Router, "rk_worker", WorkerAuth.ClaudeLogin);

        Assert.Equal("http://localhost:8080", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("X-Weave-Router-Key: rk_worker", env["ANTHROPIC_CUSTOM_HEADERS"]);
        Assert.Equal("/usr/bin:/bin", env["PATH"]);
        Assert.Equal("/Users/someone", env["HOME"]);

        Assert.Equal(["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "HOME", "PATH", "USER"], env.Keys.Order());
        Assert.DoesNotContain(env.Values, v => v.Contains("sk-") || v.Contains("ghp_") || v.Contains("oauth") || v.Contains("leak"));
    }

    [Fact]
    public void Router_key_auth_mode_uses_the_router_key_as_api_key_and_nothing_else()
    {
        var env = ClaudeWorker.BuildEnvironment(ParentEnvironment(), Router, "rk_worker", WorkerAuth.RouterKey);

        Assert.Equal("rk_worker", env["ANTHROPIC_API_KEY"]);
        Assert.Equal(["ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "HOME", "PATH", "USER"], env.Keys.Order());
        Assert.DoesNotContain(env.Values, v => v.Contains("sk-") || v.Contains("ghp_") || v.Contains("oauth") || v.Contains("leak"));
    }

    [Fact]
    public void Arguments_request_stream_json_and_exclude_git_and_gh_tools()
    {
        var args = ClaudeWorker.BuildArguments("do the story");

        Assert.Equal(["-p", "do the story", "--output-format", "stream-json", "--verbose"], args.Take(5));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("project,local", args[args.ToList().IndexOf("--setting-sources") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("git") || a.Contains("gh ") || a == "--dangerously-skip-permissions");
    }

    [Fact]
    public async Task Runs_process_in_worktree_and_reports_session_from_stream()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var envDump = Path.Combine(dir, "env.txt");
        var script = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            env > "{{envDump}}"
            pwd >> "{{envDump}}"
            echo '{"type":"system","subtype":"init","session_id":"sess-abc"}'
            echo 'progress noise'
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"sess-abc"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-should-not-leak");
        try
        {
            var worker = new ClaudeWorker(script, Router, "rk_worker", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1));
            var result = await worker.RunAsync(dir, "prompt", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("sess-abc", result.SessionId);
            var dumped = File.ReadAllText(envDump);
            Assert.Contains("ANTHROPIC_CUSTOM_HEADERS=X-Weave-Router-Key: rk_worker", dumped);
            Assert.DoesNotContain("sk-ant-should-not-leak", dumped);
            Assert.Contains(new DirectoryInfo(dir).Name, dumped);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    /// <summary>
    /// S5 probe: even when the orchestrator holds the App private key (here via the env-var form of
    /// <c>GitHub:PrivateKeyPem</c>), the launched worker sees it in neither its environment, its
    /// argv nor its worktree.
    /// </summary>
    [Fact]
    public async Task Worker_never_receives_the_app_private_key_in_env_args_or_worktree()
    {
        var pem = System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem();
        var keyBody = pem.Split('\n')[1];
        var worktree = Directory.CreateTempSubdirectory("df-worker-wt-").FullName;
        File.WriteAllText(Path.Combine(worktree, "Program.cs"), "// worker code\n");
        var probeDir = Directory.CreateTempSubdirectory("df-worker-probe-").FullName;
        var dump = Path.Combine(probeDir, "probe.txt");
        var script = Path.Combine(probeDir, "fake-claude.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            env > "{{dump}}"
            printf '%s\n' "$@" >> "{{dump}}"
            grep -rIl -e "PRIVATE KEY" -e "{{keyBody[..32]}}" . > "{{dump}}.files"
            echo '{"type":"system","subtype":"init","session_id":"sess-key"}'
            echo '{"type":"result","subtype":"success","is_error":false,"session_id":"sess-key"}'
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("GitHub__PrivateKeyPem", pem);
        try
        {
            var result = await new ClaudeWorker(script, Router, "rk_worker", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1))
                .RunAsync(worktree, "implement the story", CancellationToken.None);

            Assert.True(result.Succeeded);
            var probe = File.ReadAllText(dump);
            Assert.Contains("ANTHROPIC_BASE_URL=", probe); // the dump ran
            Assert.Contains("implement the story", probe);
            Assert.DoesNotContain("PRIVATE KEY", probe);
            Assert.DoesNotContain(keyBody, probe);
            Assert.DoesNotContain("GitHub__", probe);
            Assert.Equal("", File.ReadAllText($"{dump}.files")); // no file in the worktree carries the key
        }
        finally
        {
            Environment.SetEnvironmentVariable("GitHub__PrivateKeyPem", null);
        }
    }

    [Fact]
    public async Task Nonzero_exit_is_not_success()
    {
        var dir = Directory.CreateTempSubdirectory("df-worker-").FullName;
        var script = Path.Combine(dir, "fake-claude.sh");
        File.WriteAllText(script, "#!/bin/sh\necho '{\"type\":\"system\",\"session_id\":\"s\"}'\necho boom >&2\nexit 3\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await new ClaudeWorker(script, Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1)).RunAsync(dir, "p", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("boom", result.StderrTail);
    }
}
