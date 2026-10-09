using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DarkFactory.Analyzers;

/// <summary>
/// The gateway lint (sc-25390, epic E2 "no bypass", Phase 1 E1 "every model call through the router"). All GitHub, Shortcut
/// and router HTTP and every git push go through the orchestrator's gateway, <see cref="GatewayDirectory"/>; this analyzer,
/// run by every build of the orchestrator (CI's included), fails the build on:
/// <list type="bullet">
/// <item>DF0001: an outbound network client made outside the gateway — an <c>HttpClient</c> or any <c>HttpMessageInvoker</c>,
/// an <c>HttpMessageHandler</c> (constructed or subclassed), <c>IHttpClientFactory.CreateClient</c> or <c>AddHttpClient</c>, a raw
/// <c>Socket</c>/<c>TcpClient</c>/<c>UdpClient</c>, a <c>ClientWebSocket</c>, <c>WebRequest</c>/<c>WebClient</c>;</item>
/// <item>DF0002: a git remote write named outside the gateway — a <c>push</c> or <c>set-url</c> argument, or text running
/// <c>git push</c> / <c>git remote set-url</c>;</item>
/// <item>DF0003: a model provider's host or key variable anywhere, the gateway and the additional files (embedded manifests,
/// prompts, scripts, Razor markup) included: model calls go to the router only;</item>
/// <item>DF0004: a service host the gateway owns (GitHub's or Shortcut's API) named outside the gateway.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GatewayAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The gateway: the one folder allowed to make outbound clients and git remote writes ('/'-separated, matched in a file's path).</summary>
    public const string GatewayDirectory = "src/DarkFactory.Orchestrator/Gateway/";

    private const string Category = "Gateway";
    private const string Help = " (sc-25390: all GitHub, Shortcut and router HTTP and every git push go through src/DarkFactory.Orchestrator/Gateway)";

    public static readonly DiagnosticDescriptor OutboundClient = new(
        "DF0001", "Outbound network client outside the gateway",
        "{0} outside the gateway" + Help, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GitRemoteWrite = new(
        "DF0002", "git remote write outside the gateway",
        "git remote write ({0}) outside the gateway" + Help, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ProviderHost = new(
        "DF0003", "Model provider host or key",
        "'{0}' names a model provider: every model call goes through the router only (Phase 1 E1)", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ServiceHost = new(
        "DF0004", "Service host outside the gateway",
        "'{0}' is a host the gateway owns: use its named client" + Help, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

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

    /// <summary>Model provider API hosts and key variables (matched case-insensitively).</summary>
    public static readonly ImmutableArray<string> ProviderMarkers = ImmutableArray.Create(
        "api.anthropic.com", "api.openai.com", "openai.azure.com", "generativelanguage.googleapis.com",
        "aiplatform.googleapis.com", "bedrock-runtime", "api.mistral.ai", "api.cohere.ai", "api.cohere.com", "api.groq.com",
        "openrouter.ai", "api.x.ai", "api.deepseek.com", "api.together.xyz", "api.fireworks.ai",
        "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "sk-ant-");

    /// <summary>Hosts only the gateway names (matched case-insensitively).</summary>
    public static readonly ImmutableArray<string> ServiceHosts = ImmutableArray.Create("api.github.com", "uploads.github.com", "api.app.shortcut.com");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(OutboundClient, GitRemoteWrite, ProviderHost, ServiceHost);

    public override void Initialize(AnalysisContext context)
    {
        // Generated code (Razor components, source generators) is linted too: nothing reaches the network around the gateway.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeLiteral, OperationKind.Literal);
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
        if (!InGateway(creation) && ClientBase(creation.Type) is { } client)
        {
            context.ReportDiagnostic(Diagnostic.Create(OutboundClient, creation.Syntax.GetLocation(),
                $"new {creation.Type!.Name} ({client})"));
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

    private static void AnalyzeLiteral(OperationAnalysisContext context)
    {
        if (context.Operation.ConstantValue is not { HasValue: true, Value: string text })
        {
            return;
        }
        var location = context.Operation.Syntax.GetLocation();
        if (ProviderMarker(text) is { } provider)
        {
            context.ReportDiagnostic(Diagnostic.Create(ProviderHost, location, provider));
        }
        if (InGateway(context.Operation))
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

    /// <summary>The git remote write <paramref name="text"/> is or runs, if any: a <c>push</c>/<c>set-url</c> argument, or a command line.</summary>
    public static string? GitWrite(string text)
    {
        var trimmed = text.Trim();
        if (trimmed is "push" or "set-url")
        {
            return trimmed;
        }
        var words = string.Join(" ", trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var command in new[] { "git push", "remote set-url" })
        {
            if (words.IndexOf(command, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return command;
            }
        }
        return null;
    }
}
