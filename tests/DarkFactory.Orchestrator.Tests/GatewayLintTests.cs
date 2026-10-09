using System.Collections.Immutable;
using System.Xml.Linq;
using DarkFactory.Analyzers;
using DarkFactory.Orchestrator.Gateway;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The gateway lint (sc-25390): <see cref="GatewayAnalyzer"/> fails the orchestrator's build — CI's <c>build-test</c> included —
/// on outbound HTTP or a git push outside <c>src/DarkFactory.Orchestrator/Gateway</c> and on a model provider's host anywhere.
/// Each rule is proven to fail on a seeded violation, and the current tree to pass.
/// </summary>
public sealed class GatewayLintTests
{
    private const string Outside = "/repo/src/DarkFactory.Orchestrator/GitHub/Seeded.cs";
    private const string Inside = "/repo/src/DarkFactory.Orchestrator/Gateway/Seeded.cs";

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => MetadataReference.CreateFromFile(p)),
    ];

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DarkFactory.slnx")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
        }
    }

    private static string OrchestratorDir => Path.Combine(RepoRoot, "src", "DarkFactory.Orchestrator");

    /// <summary>The gateway analyzer's diagnostics on <paramref name="files"/> (path, C# source) and <paramref name="additional"/> files.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> Lint(IEnumerable<(string Path, string Code)> files, IEnumerable<(string Path, string Text)>? additional = null)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Code, new CSharpParseOptions(LanguageVersion.Preview), f.Path));
        var compilation = CSharpCompilation.Create("lint", trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var options = new AnalyzerOptions([.. (additional ?? []).Select(a => (AdditionalText)new Text(a.Path, a.Text))]);
        var diagnostics = await compilation.WithAnalyzers([new GatewayAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToList();
    }

    private static Task<IReadOnlyList<Diagnostic>> Lint(string path, string code) => Lint([(path, code)]);

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private static string Wrap(string body) => $$"""
        using System;
        using System.Net;
        using System.Net.Http;
        using System.Net.Sockets;
        using System.Net.WebSockets;
        using Microsoft.Extensions.DependencyInjection;
        namespace DarkFactory.Orchestrator.Seeded;
        public static class Seed
        {
            public static object Run(IHttpClientFactory factory, IServiceCollection services, string branch)
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public async Task A_seeded_http_client_to_github_outside_the_gateway_fails_the_lint_and_inside_it_passes()
    {
        const string seeded = """return new HttpClient { BaseAddress = new Uri("https://api.github.com/") };""";

        var outside = await Lint(Outside, Wrap(seeded));
        Assert.Equal(["DF0001", "DF0004"], outside.Select(d => d.Id));
        Assert.All(outside, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("new HttpClient (System.Net.Http.HttpMessageInvoker) outside the gateway", outside[0].GetMessage());

        Assert.Empty(await Lint(Inside, Wrap(seeded)));
    }

    [Theory]
    [InlineData("HttpClient h = new(); return h;", "new HttpClient")]
    [InlineData("return new HttpClient(new HttpClientHandler());", "new HttpClient")]
    [InlineData("return new SocketsHttpHandler();", "new SocketsHttpHandler")]
    [InlineData("return new HttpMessageInvoker(new SocketsHttpHandler());", "new HttpMessageInvoker")]
    [InlineData("return new TcpClient(\"example.com\", 443);", "new TcpClient")]
    [InlineData("return new UdpClient();", "new UdpClient")]
    [InlineData("return new Socket(SocketType.Stream, ProtocolType.Tcp);", "new Socket")]
    [InlineData("return new ClientWebSocket();", "new ClientWebSocket")]
    [InlineData("return WebRequest.Create(\"https://example.com\");", "WebRequest.Create")]
    [InlineData("return WebRequest.CreateHttp(\"https://example.com\");", "WebRequest.CreateHttp")]
    [InlineData("return new WebClient();", "new WebClient")]
    [InlineData("return factory.CreateClient(\"github\");", "IHttpClientFactory.CreateClient")]
    [InlineData("return services.AddHttpClient(\"github\");", ".AddHttpClient")]
    public async Task Every_outbound_client_form_outside_the_gateway_fails_the_lint(string body, string named)
    {
        var diagnostics = await Lint(Outside, Wrap(body));
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal("DF0001", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains(named, StringComparison.Ordinal));
        Assert.DoesNotContain(await Lint(Inside, Wrap(body)), d => d.Id == "DF0001");
    }

    [Fact]
    public async Task A_handler_or_client_subclass_outside_the_gateway_fails_the_lint()
    {
        const string code = """
            using System.Net.Http;
            namespace DarkFactory.Orchestrator.Seeded;
            public sealed class Spy : DelegatingHandler { }
            public sealed class Raw : HttpMessageHandler
            {
                protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, System.Threading.CancellationToken ct) => null!;
            }
            public sealed class MyClient : HttpClient { }
            """;
        var diagnostics = await Lint(Outside, code);
        Assert.Equal(["class Spy : System.Net.Http.HttpMessageHandler", "class Raw : System.Net.Http.HttpMessageHandler", "class MyClient : System.Net.Http.HttpMessageInvoker"],
            diagnostics.Select(d => d.GetMessage().Split(" outside")[0]));
        Assert.Empty(await Lint(Inside, code));
    }

    [Theory]
    [InlineData("""return new[] { "-C", "w", "push", "origin", "HEAD" };""", "push")]
    [InlineData("""return new[] { "remote", "set-url", "origin", "https://x" };""", "set-url")]
    [InlineData("""return $"git  push origin {branch}";""", "git push")]
    [InlineData("""return "git remote set-url origin x";""", "remote set-url")]
    public async Task A_git_remote_write_outside_the_gateway_fails_the_lint(string body, string named)
    {
        var diagnostic = Assert.Single(await Lint(Outside, Wrap(body)));
        Assert.Equal("DF0002", diagnostic.Id);
        Assert.Contains($"git remote write ({named})", diagnostic.GetMessage());
        Assert.Empty(await Lint(Inside, Wrap(body)));
    }

    [Fact]
    public async Task Git_reads_and_prose_about_pushes_pass_the_lint()
    {
        Assert.Empty(await Lint(Outside, Wrap("""return new[] { "fetch", "--prune", "origin", "a push from outside the factory", "pushed" };""")));
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages")]
    [InlineData("https://API.OpenAI.com/v1")]
    [InlineData("https://generativelanguage.googleapis.com")]
    [InlineData("ANTHROPIC_API_KEY")]
    public async Task A_model_provider_host_or_key_fails_the_lint_even_in_the_gateway_and_in_additional_files(string text)
    {
        var body = $"return \"{text}\";";
        Assert.Equal("DF0003", Assert.Single(await Lint(Outside, Wrap(body))).Id);
        Assert.Equal("DF0003", Assert.Single(await Lint(Inside, Wrap(body))).Id);

        var inFile = Assert.Single(await Lint([(Inside, "namespace N;")], [("/repo/factory/prompts/review.md", $"line one\nsee {text} here\n")]));
        Assert.Equal("DF0003", inFile.Id);
        Assert.Equal(1, inFile.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task The_gateways_service_hosts_outside_it_fail_the_lint()
    {
        Assert.Equal("DF0004", Assert.Single(await Lint(Outside, Wrap("""return "https://api.app.shortcut.com/api/v3/";"""))).Id);
        // Links to github.com pages (not the API) are not outbound calls.
        Assert.Empty(await Lint(Outside, Wrap("""return $"https://github.com/{branch}/pull/1";""")));
    }

    /// <summary>The orchestrator's sources and the additional files its build lints (but the launch scripts, which only SafeHelper reads in tests).</summary>
    private static (List<(string, string)> Code, List<(string, string)> Additional) Tree()
    {
        var code = Directory.EnumerateFiles(OrchestratorDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (f, File.ReadAllText(f)))
            .ToList();
        var additional = Directory.EnumerateFiles(Path.Combine(OrchestratorDir, "GitHub"), "*.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "factory", "prompts"), "*.md"))
            .Concat(Directory.EnumerateFiles(OrchestratorDir, "*.razor", SearchOption.AllDirectories))
            .Select(f => (f, File.ReadAllText(f)))
            .ToList();
        Assert.Contains(code, c => c.f.EndsWith(Path.Combine("Gateway", "OutboundHttp.cs"), StringComparison.Ordinal));
        return (code, additional);
    }

    [Fact]
    public async Task The_current_tree_passes_the_lint_and_the_same_tree_with_a_seeded_client_fails_it()
    {
        var (code, additional) = Tree();
        Assert.Empty(await Lint(code, additional));

        var seeded = Path.Combine(OrchestratorDir, "GitHub", "Seeded.cs");
        code.Add((seeded, "namespace DarkFactory.Orchestrator.GitHub; static class Seeded { static readonly System.Net.Http.HttpClient Http = new() { BaseAddress = new(\"https://api.github.com/\") }; }"));
        var failed = await Lint(code, additional);
        Assert.Equal(["DF0001", "DF0004"], failed.Select(d => d.Id).Order());
        Assert.All(failed, d => Assert.Equal(seeded, d.Location.SourceTree?.FilePath));
    }

    [Fact]
    public void Every_build_of_the_orchestrator_runs_the_lint_and_ci_builds_and_tests_in_build_test()
    {
        var project = XDocument.Load(Path.Combine(OrchestratorDir, "DarkFactory.Orchestrator.csproj"));
        var analyzer = Assert.Single(project.Descendants("ProjectReference"),
            r => (string?)r.Attribute("Include") == "../DarkFactory.Analyzers/DarkFactory.Analyzers.csproj");
        Assert.Equal("Analyzer", (string?)analyzer.Attribute("OutputItemType"));
        Assert.All(new GatewayAnalyzer().SupportedDiagnostics, d =>
        {
            Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity);
            Assert.True(d.IsEnabledByDefault);
        });

        // Nothing switches a rule off: no NoWarn, WarningsNotAsErrors, .editorconfig or ruleset naming DF0001…DF0004.
        var configs = Directory.EnumerateFiles(RepoRoot)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "tests"), "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (f.EndsWith("proj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".targets", StringComparison.Ordinal)
                    || f.EndsWith(".editorconfig", StringComparison.Ordinal) || f.EndsWith(".globalconfig", StringComparison.Ordinal) || f.EndsWith(".ruleset", StringComparison.Ordinal)));
        Assert.All(configs, f => Assert.DoesNotMatch(@"DF000\d", File.ReadAllText(f)));
        Assert.All(Directory.EnumerateFiles(OrchestratorDir, "*.cs", SearchOption.AllDirectories),
            f => Assert.DoesNotMatch(@"(pragma\s+warning\s+disable|SuppressMessage)[^\n]*DF000\d", File.ReadAllText(f)));

        // CI's required build-test job builds (the lint runs) and tests (this file and the gate-check coverage run).
        var ci = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));
        var job = ci[ci.IndexOf("\n  build-test:", StringComparison.Ordinal)..];
        Assert.Contains("run: dotnet build", job);
        Assert.Contains("run: dotnet test", job);
    }

    [Fact]
    public void Push_arguments_and_git_credentials_come_from_the_gateway_unchanged()
    {
        Assert.Equal(["--git-dir=g", "--work-tree=w", "push", "--force", "origin", "HEAD:refs/heads/factory/sc-1"],
            GitRemoteWrites.Push(["--git-dir=g", "--work-tree=w"], "factory/sc-1", force: true));
        Assert.Equal(["--git-dir=g", "push", "origin", "HEAD:refs/heads/factory/sc-1"], GitRemoteWrites.Push(["--git-dir=g"], "factory/sc-1", force: false));
        var env = GitRemoteWrites.Credentials("tok");
        Assert.Equal($"AUTHORIZATION: basic {Convert.ToBase64String("x-access-token:tok"u8.ToArray())}", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_1"]);
        Assert.Equal(new Uri("https://api.github.com/"), OutboundHttp.GitHubApi().BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(7), OutboundHttp.RouterApi(new Uri("http://localhost:8080/"), TimeSpan.FromSeconds(7)).Timeout);
    }
}
