using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DarkFactory.Analyzers;

/// <summary>
/// The part of the gateway lint nothing can suppress (sc-25391): Roslyn honours a <c>SuppressMessage</c> attribute even for the
/// NotConfigurable gateway rules, and an attribute naming DF0005 would hide <see cref="GatewayAnalyzer"/>'s own report of it
/// (on its symbol, or everywhere from the assembly). So this generator, in every build that runs the lint, adds a compiler
/// <c>#error</c> (CS1029, which no pragma, attribute or severity setting turns off) for every <c>SuppressMessage</c> or
/// <c>UnconditionalSuppressMessage</c> attribute in the sources whose check id names a gateway rule
/// (<see cref="GatewayAnalyzer.SuppressedRule"/>), and for every gateway rule id quoted in a <c>.razor</c> file (an
/// <c>@attribute [SuppressMessage(…, "DF0001")]</c>: Razor's own generated code is not visible to another generator, and
/// <see cref="GatewayAnalyzer"/> reports DF0005 on it too).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class GatewaySuppressionGuard : IIncrementalGenerator
{
    /// <summary>The file the errors are added in.</summary>
    public const string HintName = "GatewaySuppressionGuard.g.cs";

    /// <summary>A gateway rule id in quotes, as a Razor directive or <c>@code</c> block would write it.</summary>
    private static readonly Regex QuotedRule = new(@"""\s*DF\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var attributes = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is AttributeSyntax { ArgumentList.Arguments.Count: >= 2 },
                static (syntax, ct) => GatewayAnalyzer.SuppressedRule((AttributeSyntax)syntax.Node, syntax.SemanticModel, ct) is { } rule
                    ? $"{Where(syntax.Node.GetLocation())}: {rule.Attribute} suppresses the gateway rule '{rule.CheckId}'"
                    : null)
            .Where(static e => e is not null)
            .Collect();
        var razor = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) => t.GetText(ct)?.Lines.Where(l => QuotedRule.IsMatch(l.ToString()))
                .Select(l => $"{t.Path}({l.LineNumber + 1}): names a gateway rule ({l.ToString().Trim()})").FirstOrDefault())
            .Where(static e => e is not null)
            .Collect();
        context.RegisterSourceOutput(attributes.Combine(razor), static (output, found) => Emit(output, [.. found.Left, .. found.Right]));
    }

    private static string Where(Location location) =>
        location.GetLineSpan() is var span ? $"{span.Path}({span.StartLinePosition.Line + 1})" : "?";

    private static void Emit(SourceProductionContext output, ImmutableArray<string?> found)
    {
        if (found.Length == 0)
        {
            return;
        }
        var source = new StringBuilder("// The gateway lint (sc-25391): no gateway rule may be suppressed.\n");
        foreach (var problem in found.OfType<string>().OrderBy(p => p, StringComparer.Ordinal))
        {
            // One line: a #error directive's message runs to the end of its line.
            source.Append("#error DF0005 ").Append(problem.Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
        }
        output.AddSource(HintName, source.ToString());
    }
}
