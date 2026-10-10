using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DarkFactory.Analyzers;
using DarkFactory.Orchestrator.Gateway;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The gateway lint (sc-25390): <see cref="GatewayAnalyzer"/> fails the build of the orchestrator and the acceptance tests — CI's
/// <c>build-test</c> included — on outbound HTTP or a git push outside <c>src/DarkFactory.Orchestrator/Gateway</c> and on a
/// model provider's host anywhere. Each rule is proven to fail on a seeded violation, suppressing a rule or switching
/// analyzers off is refused, and the current tree passes.
/// </summary>
public sealed class GatewayLintTests
{
    private const string Outside = "/repo/src/DarkFactory.Orchestrator/GitHub/Seeded.cs";
    private const string Inside = "/repo/src/DarkFactory.Orchestrator/Gateway/Seeded.cs";

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            // The orchestrator's own assembly is left out: the tree lint compiles its sources.
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) && Path.GetFileName(p) != "factory.dll")
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

    private static string AcceptanceDir => Path.Combine(RepoRoot, "tests", "DarkFactory.AcceptanceTests");

    private static CSharpCompilation Compile(string name, IEnumerable<(string Path, string Code)> files, IEnumerable<MetadataReference>? extra = null,
        OutputKind kind = OutputKind.DynamicallyLinkedLibrary) =>
        CSharpCompilation.Create(name,
            files.Select(f => CSharpSyntaxTree.ParseText(f.Code, new CSharpParseOptions(LanguageVersion.Preview), f.Path)),
            [.. References, .. extra ?? []],
            new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>
    /// The gateway analyzer's diagnostics on <paramref name="compilation"/> and <paramref name="additional"/> files, suppressed
    /// ones included: a <c>#pragma</c> or <c>SuppressMessage</c> hides nothing from this lint.
    /// </summary>
    private static async Task<IReadOnlyList<Diagnostic>> Lint(Compilation compilation, IEnumerable<(string Path, string Text)>? additional = null)
    {
        var options = new AnalyzerOptions([.. (additional ?? []).Select(a => (AdditionalText)new Text(a.Path, a.Text))]);
        var diagnostics = await compilation.WithAnalyzers([new GatewayAnalyzer()],
                new CompilationWithAnalyzersOptions(options, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: true))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToList();
    }

    private static Task<IReadOnlyList<Diagnostic>> Lint(IEnumerable<(string Path, string Code)> files, IEnumerable<(string Path, string Text)>? additional = null) =>
        Lint(Compile("lint", files), additional);

    private static Task<IReadOnlyList<Diagnostic>> Lint(string path, string code) => Lint([(path, code)]);

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private static string Wrap(string body) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Net;
        using System.Net.Http;
        using System.Net.Sockets;
        using System.Net.WebSockets;
        using Microsoft.Extensions.DependencyInjection;
        namespace DarkFactory.Orchestrator.Seeded;
        public static class Seed
        {
            public static object Run(IHttpClientFactory factory, IServiceCollection services, IServiceProvider provider, string branch)
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
    [InlineData("return Activator.CreateInstance(typeof(HttpClient))!;", "typeof(HttpClient) (System.Net.Http.HttpMessageInvoker)")]
    [InlineData("return Activator.CreateInstance(typeof(SocketsHttpHandler))!;", "typeof(SocketsHttpHandler) (System.Net.Http.HttpMessageHandler)")]
    [InlineData("return Activator.CreateInstance<HttpClient>();", "Activator.CreateInstance<HttpClient> (System.Net.Http.HttpMessageInvoker)")]
    [InlineData("return provider.GetRequiredService<HttpClient>();", "GetRequiredService<HttpClient>")]
    [InlineData("return Process.Start(\"gh\", \"api repos/o/r/pulls\")!;", "subprocess 'gh'")]
    [InlineData("return Process.Start(\"/usr/bin/curl\", \"-s -X POST x\")!;", "subprocess 'curl'")]
    [InlineData("return new ProcessStartInfo(\"wget\", \"-q x\");", "subprocess 'wget'")]
    [InlineData("return new ProcessStartInfo { FileName = \"/usr/bin/nc\" };", "subprocess 'nc'")]
    [InlineData("var psi = new ProcessStartInfo(); psi.FileName = \"c\" + \"url\"; return psi;", "subprocess 'curl'")]
    public async Task Every_outbound_client_form_outside_the_gateway_fails_the_lint(string body, string named)
    {
        var diagnostics = await Lint(Outside, Wrap(body));
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal("DF0001", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains(named, StringComparison.Ordinal));
        Assert.DoesNotContain(await Lint(Inside, Wrap(body)), d => d.Id == "DF0001");
    }

    [Fact]
    public async Task Other_subprocesses_and_client_types_used_but_not_made_pass_the_lint()
    {
        Assert.Empty(await Lint(Outside, Wrap("""
            Process.Start("git", "status")?.Dispose();
            Process.Start(new ProcessStartInfo("/usr/bin/security") { FileName = "/usr/bin/security" })?.Dispose();
            var names = new List<HttpClient>();
            return names.Count + typeof(string).Name;
            """)));
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
    [InlineData("""return "git remote set-url origin x";""", "git remote set-url")]
    [InlineData("""return "push --force origin x";""", "push")]
    [InlineData("""return $"-C {branch} push origin";""", "push")]
    [InlineData("""return "git -C dir push";""", "git push")]
    [InlineData("""return "git --git-dir=x push";""", "git push")]
    [InlineData("""return "git -c user.name=x --work-tree w remote -v set-url origin x";""", "git remote set-url")]
    [InlineData("""return "/usr/bin/git send-pack x";""", "git send-pack")]
    [InlineData("""return "git -C " + branch + " push";""", "git push")]
    [InlineData("""return Process.Start("git", "pu" + "sh")!;""", "push")]
    [InlineData("""const string verb = "pu" + "sh"; return verb;""", "push")]
    [InlineData("""var psi = new ProcessStartInfo("git"); psi.ArgumentList.Add("push"); return psi;""", "push")]
    [InlineData("""string[] args = ["-C", branch, "push"]; return args;""", "push")]
    [InlineData("""return new List<string> { "-C", branch, "send-pack" };""", "send-pack")]
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
        Assert.Empty(await Lint(Outside, Wrap("""
            return new[]
            {
                "fetch", "--prune", "origin", "a push from outside the factory", "pushed", $"the push to {branch} failed",
                "git log --grep push", "git remote -v", "remote add origin x", "git -C dir status",
            };
            """)));
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

    [Theory]
    [InlineData("""return "https://api.github" + ".com/";""", "DF0004", "api.github.com")]
    [InlineData("""const string host = "api.github" + ".com"; return host;""", "DF0004", "api.github.com")]
    [InlineData("""return $"https://{"api.github"}.com/repos/{branch}";""", "DF0004", "api.github.com")]
    [InlineData("""return "x" + "https://api.app.shortcut.com/" + branch;""", "DF0004", "api.app.shortcut.com")]
    [InlineData("""return "api.anthr" + "opic.com";""", "DF0003", "api.anthropic.com")]
    public async Task A_string_folded_from_parts_is_read_whole_and_reported_once(string body, string id, string named)
    {
        var diagnostic = Assert.Single(await Lint(Outside, Wrap(body)));
        Assert.Equal(id, diagnostic.Id);
        Assert.Contains(named, diagnostic.GetMessage());
    }

    [Fact]
    public async Task A_constant_field_made_of_parts_fails_the_lint()
    {
        var diagnostic = Assert.Single(await Lint(Outside, """
            namespace DarkFactory.Orchestrator.Seeded;
            public static class Hosts
            {
                public const string GitHub = "https://api.github" + ".com/";
            }
            """));
        Assert.Equal("DF0004", diagnostic.Id);
    }

    [Fact]
    public async Task No_pragma_hides_a_gateway_violation_and_this_lint_sees_through_suppress_message()
    {
        // Every rule is NotConfigurable: no pragma, NoWarn or severity setting applies to it.
        Assert.All(new GatewayAnalyzer().SupportedDiagnostics, d => Assert.Contains(WellKnownDiagnosticTags.NotConfigurable, d.CustomTags));

        const string code = """
            using System.Net.Http;
            namespace DarkFactory.Orchestrator.Seeded;
            public static class Hidden
            {
            #pragma warning disable
                public static HttpClient Bare() => new();
            #pragma warning restore
            #pragma warning disable DF0001
                public static HttpClient Named() => new();
            #pragma warning restore DF0001
                [System.Diagnostics.CodeAnalysis.SuppressMessage("Gateway", "DF0001")]
                public static HttpClient Attributed() => new();
            }
            """;
        var diagnostics = await Lint(Compile("lint", [(Outside, code)]));
        Assert.Equal(["DF0001", "DF0001", "DF0005", "DF0001"], diagnostics.Select(d => d.Id));
        // The pragmas suppress nothing.
        Assert.Equal([false, false], diagnostics.Take(2).Select(d => d.IsSuppressed));
        // SuppressMessage still would (Roslyn applies it to NotConfigurable rules too), so the attribute itself fails the build
        // (DF0005, and the guard's #error: Every_suppression_attribute_naming_a_gateway_rule_fails_the_build); this lint reports
        // suppressed diagnostics anyway, and any attribute naming the gateway lint is refused (Every_way_to_switch_the_lint_off_is_refused).
        Assert.False(diagnostics[2].IsSuppressed);
        Assert.True(diagnostics[3].IsSuppressed);
        Assert.Equal(3, SwitchOffs([(Outside, code)]).Count);
    }

    // ---- What would switch the lint off ----

    /// <summary>Settings in project, MSBuild, analyzer-config and CI files that switch a gateway rule or analyzers off.</summary>
    private static readonly (Regex Pattern, string What)[] ConfigSwitchOffs =
    [
        (new(@"DF\d{4}"), "names a gateway rule (DF…)"),
        (new(@"dotnet_analyzer_diagnostic\."), "sets analyzer severities in bulk (dotnet_analyzer_diagnostic.)"),
        (new(@"category-Gateway", RegexOptions.IgnoreCase), "configures the Gateway category"),
        (new(@"<RunAnalyzers\s*>", RegexOptions.IgnoreCase), "sets <RunAnalyzers>"),
        (new(@"<RunAnalyzersDuringBuild\s*>", RegexOptions.IgnoreCase), "sets <RunAnalyzersDuringBuild>"),
        (new(@"RunAnalyzers(DuringBuild)?\s*=", RegexOptions.IgnoreCase), "sets RunAnalyzers on a command line"),
        (new(@"<Analyzer\b[^>]*\bRemove\s*=", RegexOptions.IgnoreCase), "removes an analyzer (<Analyzer Remove>)"),
    ];

    /// <summary>
    /// What in <paramref name="files"/> (path, text) would switch the gateway lint off: in C#, a <c>#pragma warning disable</c>
    /// without a code list or naming a DF rule, an attribute (<c>SuppressMessage</c>, aliased or not) whose arguments name the
    /// Gateway category or a DF rule, a <c>DiagnosticSuppressor</c>; elsewhere, <see cref="ConfigSwitchOffs"/>.
    /// </summary>
    private static List<string> SwitchOffs(IEnumerable<(string Path, string Text)> files)
    {
        var problems = new List<string>();
        foreach (var (path, text) in files)
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal))
            {
                problems.AddRange(ConfigSwitchOffs.Where(c => c.Pattern.IsMatch(text)).Select(c => $"{path} {c.What}"));
                continue;
            }
            var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
            foreach (var pragma in root.DescendantNodes(descendIntoTrivia: true).OfType<PragmaWarningDirectiveTriviaSyntax>()
                .Where(p => p.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)))
            {
                if (pragma.ErrorCodes.Count == 0)
                {
                    problems.Add($"{path} has a bare #pragma warning disable (it would hide every rule)");
                }
                else if (pragma.ErrorCodes.Any(c => c.ToString().StartsWith("DF", StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{path} has a #pragma warning disable naming a DF rule");
                }
            }
            // The arguments' own tokens plus any text an inactive #if branch holds (always a token's leading trivia: trailing
            // trivia ends at the line's end, before any directive): comments and directives (a generated #line path) name nothing.
            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>()
                .Where(a => a.ArgumentList is { } args && Regex.IsMatch(string.Concat(args.DescendantTokens().SelectMany(t =>
                        Disabled(t.LeadingTrivia).Append(t.Text))),
                    @"Gateway|DF\d", RegexOptions.IgnoreCase)))
            {
                problems.Add($"{path} has an attribute naming the gateway lint: {attribute}");
            }
            if (text.Contains("DiagnosticSuppressor", StringComparison.Ordinal))
            {
                problems.Add($"{path} declares a DiagnosticSuppressor");
            }
        }
        return problems;
    }

    private static IEnumerable<string> Disabled(SyntaxTriviaList trivia) =>
        trivia.Where(r => r.IsKind(SyntaxKind.DisabledTextTrivia)).Select(r => r.ToString());

    [Theory]
    [InlineData("src/X/Seeded.cs", "#pragma warning disable\nclass C { }\n#pragma warning restore\n", "bare #pragma warning disable")]
    [InlineData("src/X/Seeded.cs", "#pragma   warning disable // everything\nclass C { }\n", "bare #pragma warning disable")]
    [InlineData("src/X/Seeded.cs", "#pragma warning disable CS0168, DF0001\nclass C { }\n", "#pragma warning disable naming a DF rule")]
    [InlineData("src/X/Seeded.cs", "[System.Diagnostics.CodeAnalysis.SuppressMessage(\n    \"Gateway\",\n    \"Anything\")]\nclass C { }\n", "attribute naming the gateway lint")]
    [InlineData("src/X/Seeded.cs", "using S = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;\n[assembly: S(\"Other\", \"DF0001:Outbound\")]\n", "attribute naming the gateway lint")]
    [InlineData("src/X/Seeded.cs", "[System.Diagnostics.CodeAnalysis.SuppressMessage(\n#if RELEASE\n\"Gateway\", \"DF0001\"\n#else\n\"x\", \"y\"\n#endif\n)]\nclass C { }\n", "attribute naming the gateway lint")]
    [InlineData("src/X/Suppressor.cs", "class S : Microsoft.CodeAnalysis.Diagnostics.DiagnosticSuppressor { }\n", "DiagnosticSuppressor")]
    [InlineData(".editorconfig", "[*.cs]\ndotnet_diagnostic.DF0001.severity = none\n", "names a gateway rule")]
    [InlineData("src/X/X.csproj", "<Project><PropertyGroup><NoWarn>$(NoWarn);DF0002</NoWarn></PropertyGroup></Project>", "names a gateway rule")]
    [InlineData(".globalconfig", "is_global = true\ndotnet_analyzer_diagnostic.severity = none\n", "dotnet_analyzer_diagnostic.")]
    [InlineData("src/.editorconfig", "[*.cs]\ndotnet_analyzer_diagnostic.CATEGORY-Gateway.severity = none\n", "Gateway category")]
    [InlineData("Directory.Build.props", "<Project><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Project>", "<RunAnalyzers>")]
    [InlineData("src/X/X.csproj", "<Project><PropertyGroup><RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild></PropertyGroup></Project>", "<RunAnalyzersDuringBuild>")]
    [InlineData(".github/workflows/ci.yml", "      - run: dotnet build -p:RunAnalyzers=false\n", "RunAnalyzers on a command line")]
    [InlineData("Directory.Build.rsp", "/p:RunAnalyzersDuringBuild = false\n", "RunAnalyzers on a command line")]
    [InlineData("Directory.Build.targets", "<Project><ItemGroup><Analyzer Remove=\"@(Analyzer)\" /></ItemGroup></Project>", "<Analyzer Remove>")]
    [InlineData("src/X/Seeded.razor", "@attribute [System.Diagnostics.CodeAnalysis.SuppressMessage(\"Other\", \"DF0001\")]\n<p>x</p>\n", "names a gateway rule")]
    [InlineData("src/X/obj/generated/Seeded_razor.g.cs", "[System.Diagnostics.CodeAnalysis.SuppressMessage(\"Gateway\", \"Anything\")]\npublic partial class Seeded { }\n", "attribute naming the gateway lint")]
    public void Every_way_to_switch_the_lint_off_is_refused(string path, string text, string what)
    {
        Assert.Contains(SwitchOffs([(path, text)]), p => p.Contains(what, StringComparison.Ordinal));
    }

    [Fact]
    public void A_generated_line_directive_or_comment_naming_a_path_is_not_an_attribute_naming_the_lint()
    {
        // As the Razor generator writes a route: the #line path (a worktree such as agent-…df78…, a Gateway/ folder) and the
        // comment are trivia, not the attribute's arguments.
        static string Route(string value) => $$"""
            [global::Microsoft.AspNetCore.Components.RouteAttribute(
                // language=Route,Component Gateway DF0001
            #nullable restore
            #line (1,7)-(1,10) "/Users/x/.claude/worktrees/agent-afe083c7e9df78b09/Gateway/Pages/Pipeline.razor"
            {{value}}
            #line default
            #line hidden
            #nullable disable
                )]
            public partial class Pipeline { }
            """;
        const string path = "src/X/obj/generated/Pipeline_razor.g.cs";
        Assert.Empty(SwitchOffs([(path, Route("\"/\""))]));
        // The same shape whose argument itself names a rule is still refused.
        Assert.Contains(SwitchOffs([(path, Route("\"/DF0001\""))]), p => p.Contains("attribute naming the gateway lint", StringComparison.Ordinal));
    }

    /// <summary>The files a build of the linted projects reads, but bin/obj.</summary>
    private static IEnumerable<string> Files(string dir, string pattern = "*") =>
        Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// The C# sources the lint runs on (the Razor components as written — a DF rule id in one is refused like in a config file —
    /// and as the build generated them, read as C#), every source an analyzer could be shipped in, and every build and CI config.
    /// </summary>
    private static List<(string, string)> SwitchOffCandidates()
    {
        bool IsConfig(string f) => f.EndsWith("proj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal)
            || f.EndsWith(".targets", StringComparison.Ordinal) || f.EndsWith(".editorconfig", StringComparison.Ordinal)
            || f.EndsWith(".globalconfig", StringComparison.Ordinal) || f.EndsWith(".ruleset", StringComparison.Ordinal)
            || f.EndsWith(".rsp", StringComparison.Ordinal);
        return Directory.EnumerateFiles(RepoRoot).Where(IsConfig)
            .Concat(Files(Path.Combine(RepoRoot, "src")).Where(f => IsConfig(f) || f.EndsWith(".cs", StringComparison.Ordinal)
                || f.EndsWith(".razor", StringComparison.Ordinal)))
            .Concat(Files(Path.Combine(RepoRoot, "tests")).Where(IsConfig))
            .Concat(Files(AcceptanceDir, "*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, ".github", "workflows")))
            .Select(f => (f, File.ReadAllText(f)))
            .Concat(RazorGenerated())
            .ToList();
    }

    // ---- The current tree ----

    private const string OrchestratorUsings = """
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Net.Http.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DarkFactory.AcceptanceTests")]
        """;

    private const string AcceptanceUsings = """
        global using DarkFactory.Orchestrator.Gateway;
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Xunit;
        """;

    /// <summary>
    /// Where the build of the orchestrator this test run uses left its generated sources (<c>EmitCompilerGeneratedFiles</c>):
    /// <c>obj/&lt;configuration&gt;/&lt;tfm&gt;/generated</c>, the configuration and target framework read off this test's own output folder.
    /// </summary>
    private static string GeneratedDir
    {
        get
        {
            var output = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            return Path.Combine(OrchestratorDir, "obj", output.Parent!.Name, output.Name, "generated");
        }
    }

    /// <summary>The Razor components' generated sources as the build wrote them: one per <c>.razor</c> file, or this fails.</summary>
    private static List<(string, string)> RazorGenerated()
    {
        var generated = Directory.EnumerateFiles(GeneratedDir, "*_razor.g.cs", SearchOption.AllDirectories).ToList();
        Assert.Equal(Files(OrchestratorDir, "*.razor").Count(), generated.Count);
        return [.. generated.Select(f => (f, File.ReadAllText(f)))];
    }

    /// <summary>
    /// The orchestrator's sources (with its implicit usings), every source its build generated (the Razor components', the regex
    /// generator's: the build lints them), and the additional files its build lints (but the launch scripts, which only SafeHelper
    /// reads in tests).
    /// </summary>
    private static (List<(string, string)> Code, List<(string, string)> Additional) Tree()
    {
        var code = Files(OrchestratorDir, "*.cs").Select(f => (f, File.ReadAllText(f))).ToList();
        code.AddRange(RazorGenerated());
        code.AddRange(Directory.EnumerateFiles(GeneratedDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_razor.g.cs", StringComparison.Ordinal)).Select(f => (f, File.ReadAllText(f))));
        code.Add((Path.Combine(OrchestratorDir, "obj", "GlobalUsings.g.cs"), OrchestratorUsings));
        var additional = Directory.EnumerateFiles(Path.Combine(OrchestratorDir, "GitHub"), "*.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "factory", "prompts"), "*.md"))
            .Concat(Files(OrchestratorDir, "*.razor"))
            .Select(f => (f, File.ReadAllText(f)))
            .ToList();
        Assert.Contains(code, c => c.f.EndsWith(Path.Combine("Gateway", "OutboundHttp.cs"), StringComparison.Ordinal));
        return (code, additional);
    }

    /// <summary>The acceptance tests' sources (with their implicit usings and the linked <c>OwnProcess.cs</c>).</summary>
    private static List<(string, string)> AcceptanceTree()
    {
        var code = Files(AcceptanceDir, "*.cs").Select(f => (f, File.ReadAllText(f))).ToList();
        var ownProcess = Path.Combine(RepoRoot, "tests", "DarkFactory.Orchestrator.Tests", "OwnProcess.cs");
        code.Add((ownProcess, File.ReadAllText(ownProcess)));
        code.Add((Path.Combine(AcceptanceDir, "obj", "GlobalUsings.g.cs"), AcceptanceUsings));
        return code;
    }

    [Fact]
    public async Task The_current_tree_passes_the_lint_and_the_same_tree_with_a_seeded_client_fails_it()
    {
        var (code, additional) = Tree();
        var orchestrator = Compile("factory", code, kind: OutputKind.ConsoleApplication);
        // Bound as the build binds it, or a violation could hide behind an unresolved type.
        AssertNone(CompileErrors(orchestrator));
        Assert.Empty(await Lint(orchestrator, additional));

        var acceptance = Compile("DarkFactory.AcceptanceTests", AcceptanceTree(), [orchestrator.ToMetadataReference()]);
        AssertNone(CompileErrors(acceptance));
        Assert.Empty(await Lint(acceptance));

        // A client to GitHub's API in GitHub/, behind a bare pragma: reported, and the pragma refused.
        var seeded = Path.Combine(OrchestratorDir, "GitHub", "Seeded.cs");
        const string source = """
            namespace DarkFactory.Orchestrator.GitHub;
            #pragma warning disable
            static class Seeded { static readonly HttpClient Http = new() { BaseAddress = new("https://api.github.com/") }; }
            #pragma warning restore
            """;
        code.Add((seeded, source));
        var failed = await Lint(Compile("factory", code, kind: OutputKind.ConsoleApplication), additional);
        Assert.Equal(["DF0001", "DF0004"], failed.Select(d => d.Id).Order());
        Assert.All(failed, d => Assert.Equal(seeded, d.Location.SourceTree?.FilePath));
        Assert.Contains(SwitchOffs([(seeded, source)]), p => p.Contains("bare #pragma warning disable", StringComparison.Ordinal));

        var seededTest = Path.Combine(AcceptanceDir, "Seeded.cs");
        var failedTest = await Lint(Compile("DarkFactory.AcceptanceTests",
            [.. AcceptanceTree(), (seededTest, """namespace DarkFactory.AcceptanceTests; static class Seeded { static readonly HttpClient Http = new(); }""")],
            [orchestrator.ToMetadataReference()]));
        Assert.Equal("DF0001", Assert.Single(failedTest).Id);
    }

    private static void AssertNone(List<string> problems) => Assert.True(problems.Count == 0, string.Join("\n", problems));

    /// <summary>
    /// The compile errors of <paramref name="compilation"/>: it holds the generated sources too (the Razor components, the
    /// <c>[GeneratedRegex]</c> bodies), so it binds every type as the build does and a violation cannot hide behind an unresolved one.
    /// </summary>
    private static List<string> CompileErrors(Compilation compilation) =>
        [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())];

    [Fact]
    public void Every_build_of_the_linted_projects_runs_the_lint_and_ci_builds_and_tests_in_build_test()
    {
        foreach (var csproj in new[] { Path.Combine(OrchestratorDir, "DarkFactory.Orchestrator.csproj"), Path.Combine(AcceptanceDir, "DarkFactory.AcceptanceTests.csproj") })
        {
            var project = XDocument.Load(csproj);
            var analyzer = Assert.Single(project.Descendants("ProjectReference"),
                r => ((string?)r.Attribute("Include"))?.EndsWith("/DarkFactory.Analyzers/DarkFactory.Analyzers.csproj", StringComparison.Ordinal) == true);
            Assert.Equal("Analyzer", (string?)analyzer.Attribute("OutputItemType"));
            Assert.Equal("false", (string?)analyzer.Attribute("ReferenceOutputAssembly"));
            // `-p:RunAnalyzers=false` on a command line fails the build.
            var guard = Assert.Single(project.Descendants("Target"), t => (string?)t.Attribute("Name") == "RequireGatewayLint");
            Assert.Equal("CoreCompile", (string?)guard.Attribute("BeforeTargets"));
            Assert.Equal("'$(RunAnalyzers)' == 'false' or '$(RunAnalyzersDuringBuild)' == 'false'",
                (string?)Assert.Single(guard.Elements("Error")).Attribute("Condition"));
        }
        Assert.All(new GatewayAnalyzer().SupportedDiagnostics, d =>
        {
            Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity);
            Assert.True(d.IsEnabledByDefault);
            Assert.Contains(WellKnownDiagnosticTags.NotConfigurable, d.CustomTags);
        });

        // Nothing switches a rule or the analyzers off — the Razor components, as written and as generated, included.
        var candidates = SwitchOffCandidates();
        AssertNone(SwitchOffs(candidates));
        var scanned = candidates.Select(c => c.Item1).ToHashSet(StringComparer.Ordinal);
        Assert.All(Files(OrchestratorDir, "*.razor").Concat(RazorGenerated().Select(g => g.Item1)), f => Assert.Contains(f, scanned));

        // CI's required build-test job builds (the lint runs) and tests (this file and the gate-check coverage run).
        var ci = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));
        var job = ci[ci.IndexOf("\n  build-test:", StringComparison.Ordinal)..];
        Assert.Contains("run: dotnet build", job);
        Assert.Contains("run: dotnet test", job);
    }

    // ---- sc-25391: suppressions fail the build, Razor included ----

    [Theory]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("Gateway", "DF0001")] public static int M() => 1;""", "SuppressMessageAttribute", "DF0001")]
    [InlineData("""[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("x", "df0004:Host")] public static int M() => 1;""", "UnconditionalSuppressMessageAttribute", "df0004:Host")]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage(checkId: "DF" + "0003", category: "x")] public static int M() => 1;""", "SuppressMessageAttribute", "DF0003")]
    [InlineData("""[System.Diagnostics.CodeAnalysis.SuppressMessage("x", "DF0005", Justification = "hide myself")] public static int M() => 1;""", "SuppressMessageAttribute", "DF0005")]
    public async Task Every_suppression_attribute_naming_a_gateway_rule_is_its_own_error_inside_the_gateway_too(string member, string attribute, string rule)
    {
        var code = $"namespace DarkFactory.Orchestrator.Seeded; public static class Hidden {{ {member} }}";
        foreach (var path in new[] { Outside, Inside })
        {
            var diagnostic = Assert.Single(await Lint(path, code));
            Assert.Equal(("DF0005", DiagnosticSeverity.Error), (diagnostic.Id, diagnostic.Severity));
            Assert.Contains($"{attribute} suppresses the gateway rule '{rule}'", diagnostic.GetMessage());
        }
    }

    [Fact]
    public async Task An_aliased_assembly_level_suppression_is_reported_and_other_suppressions_are_not()
    {
        var aliased = Assert.Single(await Lint(Outside, """
            using S = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;
            [assembly: S("Other", "DF0002:Push")]
            namespace DarkFactory.Orchestrator.Seeded;
            """));
        Assert.Equal("DF0005", aliased.Id);
        Assert.Empty(await Lint(Outside, """
            namespace DarkFactory.Orchestrator.Seeded;
            public static class Fine
            {
                [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060")]
                public static int M(int unused) => 1;
            }
            """));
    }

    /// <summary>Runs <see cref="GatewaySuppressionGuard"/> on <paramref name="code"/> and <paramref name="razor"/>; returns the compile errors after it.</summary>
    private static List<Diagnostic> Guarded(string code, params (string Path, string Text)[] razor)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new GatewaySuppressionGuard().AsSourceGenerator()],
            [.. razor.Select(r => (AdditionalText)new Text(r.Path, r.Text))], new CSharpParseOptions(LanguageVersion.Preview));
        driver.RunGeneratorsAndUpdateCompilation(Compile("guarded", [(Outside, code)]), out var output, out var generatorErrors);
        Assert.Empty(generatorErrors);
        return [.. output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    [Fact]
    public void The_suppression_guard_turns_every_gateway_suppression_into_a_compiler_error_that_nothing_suppresses()
    {
        // Each attribute hides the other's DF0005 and the client's DF0001 from the analyzer: the guard's #error still fails the build.
        var hidden = Guarded("""
            using System.Diagnostics.CodeAnalysis;
            namespace DarkFactory.Orchestrator.Seeded;
            public static class Hidden
            {
                [SuppressMessage("x", "DF0001")]
                [SuppressMessage("x", "DF0005")]
                public static System.Net.Http.HttpClient Make() => new();
            }
            """);
        Assert.Equal(["CS1029", "CS1029"], hidden.Select(d => d.Id));
        Assert.Contains(hidden, d => d.GetMessage().Contains("DF0005", StringComparison.Ordinal) && d.GetMessage().Contains("'DF0001'", StringComparison.Ordinal));
        Assert.Contains(hidden, d => d.GetMessage().Contains("'DF0005'", StringComparison.Ordinal));

        // A .razor file quoting a gateway rule id (an @attribute [SuppressMessage(…)], whatever its alias): its own #error.
        var razor = Assert.Single(Guarded("namespace N;", ("/repo/src/X/Seeded.razor", "@using S = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute\n@attribute [S(\"x\", \"DF0001\")]\n<p>x</p>\n")));
        Assert.Equal("CS1029", razor.Id);
        Assert.Contains("Seeded.razor(2): names a gateway rule", razor.GetMessage());

        Assert.Empty(Guarded("""
            namespace DarkFactory.Orchestrator.Seeded;
            public static class Fine { [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060")] public static int M(int unused) => 1; }
            """, ("/repo/src/X/Fine.razor", "<p>DF0001 in prose is not a suppression</p>\n")));
    }

    /// <summary>
    /// The real build (sc-25391): a throwaway Razor class library with the analyzer and its guard, built by <c>dotnet build</c> (no
    /// node reuse, no compiler server). Clean, it builds; with a Razor component making an HTTP client, one suppressing a gateway
    /// rule, and a C# member hiding its own DF0005, the build fails on each.
    /// </summary>
    [Fact]
    public async Task A_razor_component_or_source_that_makes_a_client_or_suppresses_a_gateway_rule_fails_the_real_build()
    {
        var dir = Directory.CreateTempSubdirectory("df-lint-razor-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Seeded.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk.Razor">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
                  <ItemGroup><Analyzer Include="{typeof(GatewayAnalyzer).Assembly.Location}" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "Clean.razor"), "<p>clean</p>\n");
            var clean = await DotnetBuild(dir);
            Assert.True(clean.ExitCode == 0, clean.Output);

            await File.WriteAllTextAsync(Path.Combine(dir, "Client.razor"), "<p>x</p>\n@code { private readonly System.Net.Http.HttpClient _http = new(); }\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "Suppressed.razor"),
                "@attribute [System.Diagnostics.CodeAnalysis.SuppressMessage(\"Gateway\", \"DF0001\")]\n<p>x</p>\n@code { private readonly System.Net.Http.HttpClient _http = new(); }\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "Hidden.cs"), """
                using System.Diagnostics.CodeAnalysis;
                namespace Seeded;
                public static class Hidden
                {
                    [SuppressMessage("x", "DF0001")]
                    [SuppressMessage("x", "DF0005")]
                    public static System.Net.Http.HttpClient Make() => new();
                }
                """);
            var seeded = await DotnetBuild(dir);
            Assert.NotEqual(0, seeded.ExitCode);
            var errors = seeded.Output.Split('\n').Where(l => l.Contains(": error ", StringComparison.Ordinal)).ToList();
            // The Razor component's own code is linted in the build (its generated source)...
            Assert.Contains(errors, l => l.Contains("Client", StringComparison.Ordinal) && l.Contains("error DF0001", StringComparison.Ordinal));
            // ...its suppression attribute is an error (the analyzer on the generated code, and the guard on the markup)...
            Assert.Contains(errors, l => l.Contains("error DF0005", StringComparison.Ordinal) && l.Contains("'DF0001'", StringComparison.Ordinal));
            Assert.Contains(errors, l => l.Contains("error CS1029", StringComparison.Ordinal) && l.Contains("Suppressed.razor(1)", StringComparison.Ordinal));
            // ...and a member whose attributes hide each other's reports still fails the build, through the guard's #error.
            Assert.Contains(errors, l => l.Contains("error CS1029", StringComparison.Ordinal) && l.Contains("Hidden.cs(5)", StringComparison.Ordinal));
            Assert.Contains(errors, l => l.Contains("error CS1029", StringComparison.Ordinal) && l.Contains("Hidden.cs(6)", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary><c>dotnet build</c> in <paramref name="dir"/>, with no build node or compiler server left running; its exit code and output.</summary>
    private static async Task<(int ExitCode, string Output)> DotnetBuild(string dir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "build", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-clp:NoSummary" })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    // ---- sc-25391: subprocesses whose program is not a constant, or a shell running a network tool ----

    [Theory]
    [InlineData("return Process.Start(branch)!;", "non-constant program (DarkFactory.Orchestrator.Seeded.Seed.Run(branch))")]
    [InlineData("return new ProcessStartInfo(branch);", "non-constant program (DarkFactory.Orchestrator.Seeded.Seed.Run(branch))")]
    [InlineData("var psi = new ProcessStartInfo(); psi.FileName = branch; return psi;", "non-constant program")]
    [InlineData("var program = \"cu\" + branch; return Process.Start(program)!;", "non-constant program (the local 'program')")]
    [InlineData("return Process.Start(Environment.GetEnvironmentVariable(\"TOOL\") ?? \"dotnet\")!;", "non-constant program (Environment.GetEnvironmentVariable(\"TOOL\"))")]
    [InlineData("return Process.Start(\"bash\", \"-c \\\"curl -s https://x\\\"\")!;", "subprocess 'bash' running 'curl'")]
    [InlineData("return Process.Start(\"/usr/bin/env\", \"curl x\")!;", "subprocess 'env' running 'curl'")]
    [InlineData("return Process.Start(\"ssh\", \"host ls\")!;", "subprocess 'ssh'")]
    [InlineData("return Process.Start(\"python3\", \"-c \\\"import urllib.request\\\"\")!;", "subprocess 'python' running 'urllib'")]
    [InlineData("var psi = new ProcessStartInfo(\"perl\"); psi.ArgumentList.Add(\"-MLWP::Simple\"); return psi;", "subprocess 'perl' running 'LWP::'")]
    [InlineData("var psi = new ProcessStartInfo(\"/bin/sh\"); psi.ArgumentList.Add(\"-c\"); psi.ArgumentList.Add(\"wget -q \" + branch); return psi;", "subprocess 'sh' running 'wget'")]
    [InlineData("return new ProcessStartInfo(\"bash\") { Arguments = \"-c 'nc host 80'\" };", "subprocess 'bash' running 'nc'")]
    public async Task A_subprocess_whose_program_is_not_a_constant_or_a_shell_running_a_network_tool_fails_the_lint(string body, string named)
    {
        var diagnostics = await Lint(Outside, Wrap(body));
        Assert.Contains(diagnostics, d => d.Id == "DF0001" && d.GetMessage().Contains(named, StringComparison.Ordinal));
        Assert.DoesNotContain(await Lint(Inside, Wrap(body)), d => d.Id == "DF0001");
    }

    [Fact]
    public async Task Shells_running_local_commands_and_the_known_local_programs_pass_the_lint()
    {
        Assert.Empty(await Lint(Outside, Wrap("""
            Process.Start("bash", "-c 'ls -la'")?.Dispose();
            Process.Start(new ProcessStartInfo("/usr/bin/perl") { ArgumentList = { "-e", "print 1" } })?.Dispose();
            return Process.Start("git", branch)!;
            """)));
        // A program read from one of the known local programs (here the gate's test steps) is not reported; with a network
        // program as its fallback, or a network tool in its member's strings, it is.
        const string known = """
            using System.Diagnostics;
            namespace DarkFactory.Orchestrator.Gate
            {
                public sealed record TestStep(string? Program);
                public static class Runs
                {
                    public static Process Start(TestStep step) => Process.Start(new ProcessStartInfo(PROGRAM) { ArgumentList = { ARG } })!;
                }
            }
            """;
        Assert.Empty(await Lint([(Outside, known.Replace("PROGRAM", "step.Program").Replace("ARG", "\"test\""))]));
        Assert.Contains("non-constant program ('curl')",
            Assert.Single(await Lint([(Outside, known.Replace("PROGRAM", "step.Program ?? \"curl\"").Replace("ARG", "\"test\""))])).GetMessage());
        Assert.Contains("subprocess 'DarkFactory.Orchestrator.Gate.TestStep.Program' running 'curl'",
            Assert.Single(await Lint([(Outside, known.Replace("PROGRAM", "step.Program").Replace("ARG", "\"curl https://x\""))])).GetMessage());
        Assert.Contains("DarkFactory.Orchestrator.Gate.TestStep.Program", GatewayAnalyzer.KnownLocalPrograms);
    }

    // ---- sc-25391: a github.com git remote outside the gateway ----

    [Theory]
    [InlineData("""return $"https://github.com/{branch}/r.git";""", "github.com/\u0001/r.git")]
    [InlineData("""return "git@github.com:o/r.git";""", "@github.com")]
    [InlineData("""return "http.https://github.com/.extraheader";""", ".https://github.com")]
    [InlineData("""return "ssh://git@github.com/o/r";""", "ssh://git@github.com")]
    [InlineData("""return $"https://x-access-token:{branch}@github.com/o/r";""", "@github.com")]
    public async Task A_github_git_remote_or_its_credential_config_outside_the_gateway_fails_the_lint(string body, string named)
    {
        var diagnostic = Assert.Single(await Lint(Outside, Wrap(body)));
        Assert.Equal("DF0004", diagnostic.Id);
        Assert.Contains($"github.com git remote {named}", diagnostic.GetMessage());
        Assert.Empty(await Lint(Inside, Wrap(body)));
    }

    [Fact]
    public async Task Links_to_github_pages_and_noreply_addresses_are_not_remotes()
    {
        Assert.Empty(await Lint(Outside, Wrap("""
            return new[]
            {
                $"https://github.com/{branch}/pull/1", "https://github.com/o/r/tree/main", "https://github.com/o/r.github.io",
                "dark-factory@users.noreply.github.com", "https://github.com/settings/apps/new",
            };
            """)));
    }

    [Fact]
    public void Push_arguments_and_git_credentials_come_from_the_gateway_unchanged()
    {
        // sc-25391: the github.com remote, clone and fetch are the gateway's too; GitWorkspace runs them as before.
        var repo = new DarkFactory.Orchestrator.Shortcut.RepoRef("acme", "widgets");
        Assert.Equal("https://github.com/acme/widgets.git", GitRemoteReads.GitHubRemote(repo));
        Assert.Equal(GitRemoteReads.GitHubRemote(repo), DarkFactory.Orchestrator.Git.GitWorkspace.GitHubRemote(repo));
        Assert.Equal(["clone", "r", "d"], GitRemoteReads.Clone("r", "d"));
        Assert.Equal(["clone", "--depth", "1", "r", "d"], GitRemoteReads.Clone("r", "d", shallow: true));
        Assert.Equal(["fetch", "--prune", "origin"], GitRemoteReads.Fetch());
        Assert.Equal(["remote", "set-head", "origin", "--auto"], GitRemoteReads.RefreshHead());

        Assert.Equal(["--git-dir=g", "--work-tree=w", "push", "--force", "origin", "HEAD:refs/heads/factory/sc-1"],
            GitRemoteWrites.Push(["--git-dir=g", "--work-tree=w"], "factory/sc-1", force: true));
        Assert.Equal(["--git-dir=g", "push", "origin", "HEAD:refs/heads/factory/sc-1"], GitRemoteWrites.Push(["--git-dir=g"], "factory/sc-1", force: false));
        Assert.Equal(["push", "origin", ":refs/heads/x"], GitRemoteWrites.PushRef("origin", ":refs/heads/x"));
        var env = GitRemoteWrites.Credentials("tok");
        Assert.Equal($"AUTHORIZATION: basic {Convert.ToBase64String("x-access-token:tok"u8.ToArray())}", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_1"]);
        Assert.Equal(new Uri("https://api.github.com/"), OutboundHttp.GitHubApi().BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(7), OutboundHttp.RouterApi(new Uri("http://localhost:8080/"), TimeSpan.FromSeconds(7)).Timeout);
    }
}
