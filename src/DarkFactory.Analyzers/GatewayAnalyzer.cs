using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DarkFactory.Analyzers;

/// <summary>
/// The gateway lint (sc-25390, epic E2 "no bypass", Phase 1 E1 "every model call through the router"). All GitHub, Shortcut
/// and router HTTP and every git push go through the orchestrator's gateway, <see cref="GatewayDirectory"/>; this analyzer,
/// run by every build of the orchestrator and the acceptance tests (CI's included), fails the build on:
/// <list type="bullet">
/// <item>DF0001: an outbound network client made outside the gateway — an <c>HttpClient</c> or any <c>HttpMessageInvoker</c>,
/// an <c>HttpMessageHandler</c> (constructed, subclassed, named in <c>typeof</c> or as an explicit type argument such as
/// <c>Activator.CreateInstance&lt;HttpClient&gt;()</c>), <c>IHttpClientFactory.CreateClient</c> or <c>AddHttpClient</c>, a raw
/// <c>Socket</c>/<c>TcpClient</c>/<c>UdpClient</c>, a <c>ClientWebSocket</c>, <c>WebRequest</c>/<c>WebClient</c>, or a
/// subprocess that speaks a network protocol (<see cref="NetworkPrograms"/>: <c>gh</c>, <c>curl</c>, <c>wget</c>, <c>nc</c>,
/// <c>ssh</c>, …, by name or path) started through <c>Process.Start</c> or a <c>ProcessStartInfo</c>, a shell, interpreter or
/// launcher (<see cref="Launchers"/>: <c>bash</c>, <c>env</c>, <c>python</c>, <c>perl</c>, …) started where its arguments or the
/// strings of the member starting it name one, a URL or a network library (<c>bash -c "curl …"</c>), or a program whose name is
/// not a constant unless it is read from one of the known local programs (<see cref="KnownLocalPrograms"/>);</item>
/// <item>DF0002: a git remote write named outside the gateway — text whose subcommand, past git's global options, is
/// <c>push</c>, <c>send-pack</c>, <c>http-push</c> or <c>remote set-url</c>, at the start of the text (so a lone <c>push</c>
/// argument in an argument array, collection or <c>ArgumentList.Add</c>) or after a <c>git</c> word (a command line);</item>
/// <item>DF0003: a model provider's host or key variable anywhere, the gateway and the additional files (embedded manifests,
/// prompts, scripts, Razor markup) included: model calls go to the router only;</item>
/// <item>DF0004: a service host the gateway owns (GitHub's or Shortcut's API) named outside the gateway, or a git remote on
/// github.com (<c>https://github.com/o/r.git</c>, <c>git@github.com:</c>, git's <c>http.https://github.com/</c> credential
/// config): every clone, fetch and push of GitHub, and its installation-token credentials, are the gateway's;</item>
/// <item>DF0005: a <c>SuppressMessage</c> or <c>UnconditionalSuppressMessage</c> attribute whose check id names a gateway rule
/// (<c>DF…</c>), anywhere, Razor components' generated code included. Roslyn honours those attributes even for
/// NotConfigurable rules, so the build fails on the attribute itself; and since such an attribute could hide its own DF0005,
/// <see cref="GatewaySuppressionGuard"/> also turns every one (and any DF rule id quoted in a <c>.razor</c> file) into a
/// compiler <c>#error</c>, which nothing suppresses.</item>
/// </list>
/// Strings are read as the compiler folds them (<c>"api.github" + ".com"</c>, a <c>const</c> made of parts, an interpolated
/// string, its non-constant parts as unknown words), once, at the outermost expression. Every rule is
/// <see cref="WellKnownDiagnosticTags.NotConfigurable"/>: no severity setting, <c>NoWarn</c> or <c>#pragma</c> turns it off;
/// <c>GatewayLintTests</c> also fail on any attempt to suppress a rule or to switch analyzers off.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GatewayAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The gateway: the one folder allowed to make outbound clients and git remote writes ('/'-separated, matched in a file's
    /// path). The lint runs on every build of the orchestrator and of the live acceptance tests (which use the gateway's
    /// clients). The unit test projects (<c>DarkFactory.Orchestrator.Tests</c>, <c>DarkFactory.CrashHost</c>) are exempt: they
    /// make no outbound call, only fake <c>HttpClient</c>s over fake handlers and loopback test servers.
    /// </summary>
    public const string GatewayDirectory = "src/DarkFactory.Orchestrator/Gateway/";

    private const string Category = "Gateway";
    private const string Help = " (sc-25390: all GitHub, Shortcut and router HTTP and every git push go through src/DarkFactory.Orchestrator/Gateway)";

    /// <summary>No severity setting, <c>NoWarn</c> or <c>#pragma</c> applies to a gateway rule.</summary>
    private static readonly string[] NotConfigurable = [WellKnownDiagnosticTags.NotConfigurable];

    public static readonly DiagnosticDescriptor OutboundClient = new(
        "DF0001", "Outbound network client outside the gateway",
        "{0} outside the gateway" + Help, Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: NotConfigurable);

    public static readonly DiagnosticDescriptor GitRemoteWrite = new(
        "DF0002", "git remote write outside the gateway",
        "git remote write ({0}) outside the gateway" + Help, Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: NotConfigurable);

    public static readonly DiagnosticDescriptor ProviderHost = new(
        "DF0003", "Model provider host or key",
        "'{0}' names a model provider: every model call goes through the router only (Phase 1 E1)", Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: NotConfigurable);

    public static readonly DiagnosticDescriptor ServiceHost = new(
        "DF0004", "Service host outside the gateway",
        "'{0}' is a host the gateway owns: use its named client" + Help, Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: NotConfigurable);

    public static readonly DiagnosticDescriptor Suppression = new(
        "DF0005", "Suppression of a gateway rule",
        "{0} suppresses the gateway rule '{1}': no gateway rule may be suppressed" + Help, Category, DiagnosticSeverity.Error,
        isEnabledByDefault: true, customTags: NotConfigurable);

    /// <summary>Base types whose construction (or, for handlers, subclassing) is an outbound network client.</summary>
    private static readonly string[] ClientTypes =
    [
        "System.Net.Http.HttpMessageInvoker",
        "System.Net.Http.HttpMessageHandler",
        "System.Net.Sockets.Socket",
        "System.Net.Sockets.TcpClient",
        "System.Net.Sockets.UdpClient",
        "System.Net.WebSockets.ClientWebSocket",
        "System.Net.WebRequest",
        "System.Net.WebClient",
    ];

    /// <summary>Types whose methods (static or instance) make an outbound client.</summary>
    private static readonly string[] FactoryTypes =
    [
        "System.Net.WebRequest",
        "System.Net.Http.IHttpClientFactory",
        "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions",
    ];

    /// <summary>Programs that speak HTTP, SSH or raw TCP themselves: started outside the gateway, each is an outbound client.</summary>
    public static readonly ImmutableArray<string> NetworkPrograms = ImmutableArray.Create(
        "gh", "curl", "wget", "nc", "ncat", "netcat", "socat", "telnet", "ftp", "sftp", "ssh", "scp", "rsync", "aria2c", "http", "https");

    /// <summary>
    /// Shells, interpreters and launchers: what they run is in their arguments, so one started outside the gateway is an outbound
    /// client when its arguments or the strings of the member starting it name a network program, a URL or a network library
    /// (<see cref="NetworkWord"/>).
    /// </summary>
    public static readonly ImmutableArray<string> Launchers = ImmutableArray.Create(
        "sh", "bash", "zsh", "dash", "ksh", "csh", "tcsh", "fish", "env", "sudo", "doas", "nohup", "xargs", "timeout", "nice", "exec",
        "command", "python", "perl", "ruby", "node", "deno", "bun", "php", "lua", "tclsh", "awk", "gawk", "osascript", "pwsh",
        "powershell", "cmd");

    /// <summary>
    /// The only programs started outside the gateway whose name is not a constant, by the symbol the name is read from (a
    /// property or field <c>Type.Member</c>, a parameter <c>Type.Method(parameter)</c>): each is a local program, known where it is
    /// set. Any other non-constant program fails the lint (DF0001): it could be <c>curl</c>. The member starting one is still read
    /// as a launcher's (<see cref="NetworkWord"/>).
    /// </summary>
    public static readonly ImmutableArray<string> KnownLocalPrograms = ImmutableArray.Create(
        // The new-tests check's sandboxed steps: dotnet restore/build/test (XunitNewTests).
        "DarkFactory.Orchestrator.Gate.TestStep.Program",
        // sudo, through which every sandboxed run starts (WorkerSandbox.DefaultSudoPath).
        "DarkFactory.Orchestrator.Worker.WorkerSandbox.SudoPath",
        // The sandbox's owner-side helper runs (/bin/chmod), a constant at each call site.
        "DarkFactory.Orchestrator.Worker.WorkerSandbox.RunChecked(program)",
        // The Claude Code CLI (Worker:ClaudePath), which the acceptance harness probes with --version.
        "DarkFactory.Orchestrator.FactoryOptions.ClaudePath",
        // The dotnet host that runs `factory work` in the crash acceptance test (DOTNET_HOST_PATH, else dotnet).
        "DarkFactory.AcceptanceTests.CrashRestartTests.DotnetHost");

    /// <summary>
    /// Model provider API hosts and key variables (matched case-insensitively): <c>ProviderMarkers.txt</c>, embedded here and in the
    /// orchestrator (which refuses a <c>Router:BaseUrl</c> naming one at runtime), so both read the one list.
    /// </summary>
    public static readonly ImmutableArray<string> ProviderMarkers = LoadProviderMarkers();

    /// <summary>The embedded name of <c>ProviderMarkers.txt</c> (the same in the orchestrator).</summary>
    public const string ProviderMarkersResource = "provider-markers.txt";

    private static ImmutableArray<string> LoadProviderMarkers()
    {
        using var stream = typeof(GatewayAnalyzer).Assembly.GetManifestResourceStream(ProviderMarkersResource)
            ?? throw new InvalidOperationException($"The analyzer lacks its embedded {ProviderMarkersResource}.");
        using var reader = new System.IO.StreamReader(stream);
        var markers = reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .ToImmutableArray();
        return markers.Length > 0 ? markers : throw new InvalidOperationException($"The analyzer's {ProviderMarkersResource} lists no marker.");
    }

    /// <summary>Hosts only the gateway names (matched case-insensitively).</summary>
    public static readonly ImmutableArray<string> ServiceHosts = ImmutableArray.Create("api.github.com", "uploads.github.com", "api.app.shortcut.com");

    /// <summary>
    /// A git remote on github.com (matched case-insensitively): a repository URL ending <c>.git</c>, an <c>@github.com</c> user
    /// (<c>git@github.com:o/r</c>, a token in a URL), an <c>ssh://</c> or <c>git://</c> URL, or git's per-URL config of
    /// <c>https://github.com/</c> (where the installation token's header goes). Links to github.com pages are not remotes.
    /// </summary>
    private static readonly Regex GitHubRemotePattern = new(
        @"github\.com[:/][^\s]*\.git(?![A-Za-z0-9_.-])|@github\.com\b|(ssh|git)://([^/\s]*@)?github\.com\b|\.https://github\.com",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The github.com git remote <paramref name="text"/> names, if any (<see cref="GitHubRemotePattern"/>).</summary>
    public static string? GitHubRemote(string text) =>
        GitHubRemotePattern.Match(text) is { Success: true } match ? $"github.com git remote {match.Value}" : null;

    /// <summary>A part of a string whose value is not a constant (an interpolation, a concatenated variable): one unknown word.</summary>
    public const char Hole = '\u0001';

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(OutboundClient, GitRemoteWrite, ProviderHost, ServiceHost, Suppression);

    public override void Initialize(AnalysisContext context)
    {
        // Generated code (Razor components, source generators) is linted too: nothing reaches the network around the gateway.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeTypeOf, OperationKind.TypeOf);
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
        context.RegisterOperationAction(AnalyzeText, OperationKind.Literal, OperationKind.Binary, OperationKind.InterpolatedString);
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        context.RegisterAdditionalFileAction(AnalyzeAdditionalFile);
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
    }

    /// <summary>
    /// The gateway rule a <c>SuppressMessage</c>/<c>UnconditionalSuppressMessage</c> attribute suppresses (its check id, a
    /// constant, starting <c>DF</c>), with the attribute's type name; null for any other attribute.
    /// </summary>
    public static (string Attribute, string CheckId)? SuppressedRule(AttributeSyntax attribute, SemanticModel model, CancellationToken ct)
    {
        if (attribute.ArgumentList is not { Arguments.Count: >= 2 } arguments)
        {
            return null;
        }
        var type = (model.GetSymbolInfo(attribute, ct).Symbol as IMethodSymbol)?.ContainingType;
        if (type is null || type.Name is not ("SuppressMessageAttribute" or "UnconditionalSuppressMessageAttribute"))
        {
            return null;
        }
        var positional = arguments.Arguments.Where(a => a.NameEquals is null).ToList();
        var checkId = positional.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "checkId")
            ?? (positional.Count > 1 && positional[1].NameColon is null ? positional[1] : null);
        return checkId is not null && model.GetConstantValue(checkId.Expression, ct) is { HasValue: true, Value: string id }
            && id.TrimStart().StartsWith("DF", StringComparison.OrdinalIgnoreCase)
            ? (type.Name, id)
            : null;
    }

    /// <summary>DF0005: a suppression attribute naming a gateway rule, anywhere (generated code included).</summary>
    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        if (SuppressedRule((AttributeSyntax)context.Node, context.SemanticModel, context.CancellationToken) is { } suppressed)
        {
            context.ReportDiagnostic(Diagnostic.Create(Suppression, context.Node.GetLocation(), suppressed.Attribute, suppressed.CheckId));
        }
    }

    /// <summary>Whether <paramref name="path"/> is a file in the gateway.</summary>
    public static bool InGateway(string? path) =>
        path is not null && path.Replace('\\', '/').IndexOf(GatewayDirectory, StringComparison.Ordinal) >= 0;

    private static bool InGateway(IOperation operation) => InGateway(operation.Syntax.SyntaxTree.FilePath);

    private static string? ClientBase(ITypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = t.OriginalDefinition.ToDisplayString();
            if (ClientTypes.Contains(name))
            {
                return name;
            }
        }
        return null;
    }

    private static void AnalyzeCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (InGateway(creation))
        {
            return;
        }
        if (ClientBase(creation.Type) is { } client)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, creation.Syntax.GetLocation(),
                $"new {creation.Type!.Name} ({client})"));
        }
        if (IsProcessStartInfo(creation.Type) && creation.Arguments.Length > 0)
        {
            ReportProgram(context, creation.Arguments[0].Value, creation.Syntax, creation.Arguments.Skip(1).Select(a => a.Value));
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (InGateway(invocation))
        {
            return;
        }
        var owner = method.ContainingType?.OriginalDefinition.ToDisplayString();
        var isFactory = owner is not null && FactoryTypes.Any(f => f == owner || ImplementsOrDerives(method.ContainingType!, f));
        if (isFactory || method.Name == "AddHttpClient")
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, invocation.Syntax.GetLocation(), $"{method.ContainingType?.Name}.{method.Name}"));
        }
        // A client type named as an explicit type argument: Activator.CreateInstance<HttpClient>(), GetRequiredService<HttpClient>().
        if (HasExplicitTypeArguments(invocation.Syntax) && method.TypeArguments.Select(ClientBase).FirstOrDefault(c => c is not null) is { } client)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, invocation.Syntax.GetLocation(),
                $"{method.ContainingType?.Name}.{method.Name}<{string.Join(", ", method.TypeArguments.Select(t => t.Name))}> ({client})"));
        }
        if (owner == "System.Diagnostics.Process" && method.Name == "Start" && invocation.Arguments.Length > 0
            && invocation.Arguments[0].Parameter?.Type.SpecialType == SpecialType.System_String)
        {
            ReportProgram(context, invocation.Arguments[0].Value, invocation.Syntax, invocation.Arguments.Skip(1).Select(a => a.Value));
        }
    }

    private static bool ImplementsOrDerives(INamedTypeSymbol type, string name)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.OriginalDefinition.ToDisplayString() == name)
            {
                return true;
            }
        }
        return type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == name);
    }

    private static bool HasExplicitTypeArguments(SyntaxNode syntax) =>
        syntax is InvocationExpressionSyntax { Expression: var expression }
        && (expression is GenericNameSyntax
            || expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax }
            || expression is MemberBindingExpressionSyntax { Name: GenericNameSyntax });

    /// <summary><c>typeof(HttpClient)</c> (e.g. for <c>Activator.CreateInstance</c>) outside the gateway.</summary>
    private static void AnalyzeTypeOf(OperationAnalysisContext context)
    {
        var typeOf = (ITypeOfOperation)context.Operation;
        if (!InGateway(typeOf) && ClientBase(typeOf.TypeOperand) is { } client)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, typeOf.Syntax.GetLocation(), $"typeof({typeOf.TypeOperand.Name}) ({client})"));
        }
    }

    /// <summary><c>ProcessStartInfo.FileName = "curl"</c>, an object initializer's member included.</summary>
    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (!InGateway(assignment) && assignment.Target is IPropertyReferenceOperation { Property: { Name: "FileName" } property }
            && IsProcessStartInfo(property.ContainingType))
        {
            ReportProgram(context, assignment.Value, assignment.Syntax, []);
        }
    }

    private static bool IsProcessStartInfo(ITypeSymbol? type) => type?.OriginalDefinition.ToDisplayString() == "System.Diagnostics.ProcessStartInfo";

    /// <summary>
    /// A subprocess started outside the gateway: a network program by name or path; a launcher (<see cref="Launchers"/>) whose
    /// <paramref name="arguments"/> or the strings of the member starting it name a network program, a URL or a network library;
    /// a program whose name is not a constant, unless it is read from one of <see cref="KnownLocalPrograms"/> (whose member's
    /// strings are then read as a launcher's).
    /// </summary>
    private static void ReportProgram(OperationAnalysisContext context, IOperation fileName, SyntaxNode at, IEnumerable<IOperation> arguments)
    {
        var name = Render(fileName);
        if (name.IndexOf(Hole) < 0)
        {
            if (NetworkProgram(name) is { } program)
            {
                context.ReportDiagnostic(Diagnostic.Create(OutboundClient, at.GetLocation(), $"subprocess '{program}'"));
            }
            else if (Launcher(name) is { } launcher && LaunchedNetworkWord(fileName, arguments) is { } word)
            {
                context.ReportDiagnostic(Diagnostic.Create(OutboundClient, at.GetLocation(), $"subprocess '{launcher}' running '{word}'"));
            }
            return;
        }
        if (UnknownProgram(fileName) is { } unknown)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, at.GetLocation(),
                $"subprocess with a non-constant program ({unknown}), not one of the known local programs"));
        }
        else if (LaunchedNetworkWord(fileName, arguments) is { } word)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, at.GetLocation(), $"subprocess '{Describe(fileName)}' running '{word}'"));
        }
    }

    /// <summary>The program <paramref name="fileName"/> names (its last path segment, without <c>.exe</c>).</summary>
    private static string ProgramName(string fileName)
    {
        var name = fileName.Trim();
        name = name.Substring(name.LastIndexOfAny(['/', '\\']) + 1);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
    }

    /// <summary>The network program <paramref name="fileName"/> starts (by name or path), if any.</summary>
    public static string? NetworkProgram(string fileName)
    {
        var name = ProgramName(fileName);
        return NetworkPrograms.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The shell, interpreter or launcher <paramref name="fileName"/> starts (by name or path, a version suffix included), if any.</summary>
    public static string? Launcher(string fileName)
    {
        var name = ProgramName(fileName);
        var bare = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
        return Launchers.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Null when the non-constant program <paramref name="fileName"/> is read from one of <see cref="KnownLocalPrograms"/> (or is a
    /// <c>??</c> of those and constants naming no network program or launcher); else what it is read from.
    /// </summary>
    private static string? UnknownProgram(IOperation fileName) => fileName switch
    {
        IConversionOperation conversion => UnknownProgram(conversion.Operand),
        ICoalesceOperation coalesce => UnknownProgram(coalesce.Value) ?? UnknownProgram(coalesce.WhenNull),
        { ConstantValue: { HasValue: true, Value: string constant } } =>
            NetworkProgram(constant) is null && Launcher(constant) is null ? null : $"'{constant}'",
        _ when KnownLocalPrograms.Contains(Describe(fileName)) => null,
        _ => Describe(fileName),
    };

    /// <summary>The symbol a program name is read from, spelled as <see cref="KnownLocalPrograms"/> spells it.</summary>
    private static string Describe(IOperation operation) => operation switch
    {
        IConversionOperation conversion => Describe(conversion.Operand),
        IPropertyReferenceOperation property => $"{property.Property.ContainingType.OriginalDefinition.ToDisplayString()}.{property.Property.Name}",
        IFieldReferenceOperation field => $"{field.Field.ContainingType.OriginalDefinition.ToDisplayString()}.{field.Field.Name}",
        IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol method } parameter } =>
            $"{method.ContainingType.OriginalDefinition.ToDisplayString()}.{method.Name}({parameter.Name})",
        ILocalReferenceOperation local => $"the local '{local.Local.Name}'",
        _ => operation.Syntax.ToString(),
    };

    /// <summary>
    /// The network program, URL or network library named in <paramref name="arguments"/> (as the compiler folds them) or in any
    /// constant string of the member that starts the launcher (its <c>ArgumentList.Add</c>s, the consts it reads), if any.
    /// </summary>
    private static string? LaunchedNetworkWord(IOperation fileName, IEnumerable<IOperation> arguments)
    {
        foreach (var argument in arguments)
        {
            if (NetworkWord(Render(argument)) is { } word)
            {
                return word;
            }
        }
        var member = fileName.Syntax.AncestorsAndSelf().FirstOrDefault(n => n is MemberDeclarationSyntax or LocalFunctionStatementSyntax);
        if (member is null || fileName.SemanticModel is not { } model)
        {
            return null;
        }
        foreach (var expression in member.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (model.GetConstantValue(expression) is { HasValue: true, Value: string text } && NetworkWord(text) is { } word)
            {
                return word;
            }
        }
        return null;
    }

    /// <summary>URL forms and network libraries a script handed to a shell or interpreter would reach the network with.</summary>
    public static readonly ImmutableArray<string> NetworkMarkers = ImmutableArray.Create(
        "http://", "https://", "ftp://", "ws://", "wss://", "/dev/tcp/", "/dev/udp/", "urllib", "http.client", "requests.",
        "import requests", "socket.", "import socket", "LWP::", "IO::Socket", "Net::", "HTTP::Tiny", "net/http", "open-uri", "fetch(",
        "XMLHttpRequest", "Invoke-WebRequest", "Invoke-RestMethod", "System.Net.");

    private static readonly char[] ShellSeparators = [' ', '\t', '\n', '\r', ';', '|', '&', '(', ')', '<', '>', '\'', '"', '`', '$', '{', '}', '=', ','];

    /// <summary>
    /// The network program (a word of <paramref name="text"/>, split on whitespace and shell punctuation, by name or path) or the
    /// network marker (<see cref="NetworkMarkers"/>) <paramref name="text"/> names, if any.
    /// </summary>
    public static string? NetworkWord(string text)
    {
        foreach (var word in text.Split(ShellSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.IndexOf(Hole) < 0 && NetworkProgram(word) is { } program)
            {
                return program;
            }
        }
        return NetworkMarkers.FirstOrDefault(m => text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A string as the compiler folds it — a literal, a constant, a concatenation or an interpolated string — read once, at its
    /// outermost expression, with each non-constant part as a <see cref="Hole"/>.
    /// </summary>
    private static void AnalyzeText(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        if (!IsText(operation) || IsPartOfText(operation))
        {
            return;
        }
        var text = Render(operation);
        var location = operation.Syntax.GetLocation();
        if (ProviderMarker(text) is { } provider)
        {
            context.ReportDiagnostic(Diagnostic.Create(ProviderHost, location, provider));
        }
        if (InGateway(operation))
        {
            return;
        }
        if (GitWrite(text) is { } write)
        {
            context.ReportDiagnostic(Diagnostic.Create(GitRemoteWrite, location, write));
        }
        if ((ServiceHosts.FirstOrDefault(h => text.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) ?? GitHubRemote(text)) is { } host)
        {
            context.ReportDiagnostic(Diagnostic.Create(ServiceHost, location, host));
        }
    }

    private static bool IsText(IOperation operation) => operation switch
    {
        ILiteralOperation => operation.ConstantValue is { HasValue: true, Value: string },
        IBinaryOperation binary => binary.OperatorKind == BinaryOperatorKind.Add && binary.Type?.SpecialType == SpecialType.System_String,
        IInterpolatedStringOperation => true,
        _ => false,
    };

    /// <summary>Whether <paramref name="operation"/> is read as part of an enclosing string (a concatenation's operand, an interpolation).</summary>
    private static bool IsPartOfText(IOperation operation)
    {
        var parent = operation.Parent;
        while (parent is IConversionOperation)
        {
            parent = parent.Parent;
        }
        return parent is IInterpolatedStringTextOperation or IInterpolationOperation || parent is IBinaryOperation binary && IsText(binary);
    }

    /// <summary>The text of <paramref name="operation"/>: its constant value, or its parts with a <see cref="Hole"/> for each unknown one.</summary>
    private static string Render(IOperation operation)
    {
        if (operation.ConstantValue is { HasValue: true, Value: string constant })
        {
            return constant;
        }
        return operation switch
        {
            IConversionOperation conversion => Render(conversion.Operand),
            IBinaryOperation binary when IsText(binary) => Render(binary.LeftOperand) + Render(binary.RightOperand),
            IInterpolatedStringOperation interpolated => string.Concat(interpolated.Parts.Select(p => p switch
            {
                IInterpolatedStringTextOperation part => Render(part.Text),
                IInterpolationOperation hole => Render(hole.Expression),
                _ => Hole.ToString(),
            })),
            _ => Hole.ToString(),
        };
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (ClientBase(type.BaseType) is not { } client)
        {
            return;
        }
        foreach (var location in type.Locations.Where(l => l.IsInSource && !InGateway(l.SourceTree?.FilePath)))
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, location, $"class {type.Name} : {client}"));
        }
    }

    private static void AnalyzeAdditionalFile(AdditionalFileAnalysisContext context)
    {
        if (context.AdditionalFile.GetText(context.CancellationToken) is not { } text)
        {
            return;
        }
        foreach (var line in text.Lines)
        {
            if (ProviderMarker(line.ToString()) is { } provider)
            {
                context.ReportDiagnostic(Diagnostic.Create(ProviderHost,
                    Location.Create(context.AdditionalFile.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span)), provider));
            }
        }
    }

    /// <summary>The provider marker <paramref name="text"/> names, if any.</summary>
    public static string? ProviderMarker(string text) =>
        ProviderMarkers.FirstOrDefault(m => text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// The git remote write <paramref name="text"/> is or runs, if any: its words (split on whitespace; a word holding a
    /// <see cref="Hole"/> is unknown) name a <c>push</c>, <c>send-pack</c>, <c>http-push</c> or <c>remote set-url</c>
    /// subcommand — or, at the start, a lone <c>set-url</c> — past git's global options (<c>-C &lt;dir&gt;</c>,
    /// <c>--git-dir=…</c>, unknown words), at the start of the text (an argument) or after a <c>git</c> word (a command line).
    /// </summary>
    public static string? GitWrite(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (Subcommand(words, 0) is { } bare)
        {
            return bare;
        }
        for (var i = 0; i < words.Length; i++)
        {
            if ((words[i].Equals("git", StringComparison.OrdinalIgnoreCase) || words[i].EndsWith("/git", StringComparison.OrdinalIgnoreCase))
                && Subcommand(words, i + 1) is { } command && command != "set-url")
            {
                return "git " + command;
            }
        }
        return null;
    }

    /// <summary>The remote write named at <paramref name="i"/> in <paramref name="words"/>, past git's global options.</summary>
    private static string? Subcommand(string[] words, int i)
    {
        i = SkipOptions(words, i);
        if (i >= words.Length)
        {
            return null;
        }
        switch (words[i])
        {
            case "push" or "send-pack" or "http-push" or "set-url":
                return words[i];
            case "remote":
                var next = SkipOptions(words, i + 1);
                return next < words.Length && words[next] == "set-url" ? "remote set-url" : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The index past the options (and unknown words) at <paramref name="i"/>: <c>-C</c>, <c>-c</c> and the long options
    /// written without '=' take the next word as their value.
    /// </summary>
    private static int SkipOptions(string[] words, int i)
    {
        while (i < words.Length && (words[i].StartsWith("-", StringComparison.Ordinal) || words[i].IndexOf(Hole) >= 0))
        {
            i += words[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--exec-path" or "--config-env" or "--super-prefix" ? 2 : 1;
        }
        return i;
    }
}
