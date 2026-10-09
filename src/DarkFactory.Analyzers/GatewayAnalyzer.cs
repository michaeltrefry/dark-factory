using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
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
/// subprocess that speaks HTTP (<c>gh</c>, <c>curl</c>, <c>wget</c>, <c>nc</c>, by name or path) started through
/// <c>Process.Start</c> or a <c>ProcessStartInfo</c>;</item>
/// <item>DF0002: a git remote write named outside the gateway — text whose subcommand, past git's global options, is
/// <c>push</c>, <c>send-pack</c>, <c>http-push</c> or <c>remote set-url</c>, at the start of the text (so a lone <c>push</c>
/// argument in an argument array, collection or <c>ArgumentList.Add</c>) or after a <c>git</c> word (a command line);</item>
/// <item>DF0003: a model provider's host or key variable anywhere, the gateway and the additional files (embedded manifests,
/// prompts, scripts, Razor markup) included: model calls go to the router only;</item>
/// <item>DF0004: a service host the gateway owns (GitHub's or Shortcut's API) named outside the gateway.</item>
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

    /// <summary>Programs that speak HTTP (or raw TCP) themselves: started outside the gateway, each is an outbound client.</summary>
    public static readonly ImmutableArray<string> NetworkPrograms = ImmutableArray.Create("gh", "curl", "wget", "nc");

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

    /// <summary>A part of a string whose value is not a constant (an interpolation, a concatenated variable): one unknown word.</summary>
    public const char Hole = '\u0001';

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(OutboundClient, GitRemoteWrite, ProviderHost, ServiceHost);

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
            ReportNetworkProgram(context, creation.Arguments[0].Value, creation.Syntax);
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
            ReportNetworkProgram(context, invocation.Arguments[0].Value, invocation.Syntax);
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
            ReportNetworkProgram(context, assignment.Value, assignment.Syntax);
        }
    }

    private static bool IsProcessStartInfo(ITypeSymbol? type) => type?.OriginalDefinition.ToDisplayString() == "System.Diagnostics.ProcessStartInfo";

    private static void ReportNetworkProgram(OperationAnalysisContext context, IOperation fileName, SyntaxNode at)
    {
        if (NetworkProgram(Render(fileName)) is { } program)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, at.GetLocation(), $"subprocess '{program}'"));
        }
    }

    /// <summary>The HTTP-speaking program <paramref name="fileName"/> starts (by name or path), if any.</summary>
    public static string? NetworkProgram(string fileName)
    {
        var name = fileName.Trim();
        name = name.Substring(name.LastIndexOfAny(['/', '\\']) + 1);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4);
        }
        return NetworkPrograms.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
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
        if (ServiceHosts.FirstOrDefault(h => text.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) is { } host)
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
