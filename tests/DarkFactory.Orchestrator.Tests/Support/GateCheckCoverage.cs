using System.Reflection;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// Marks a test that makes the named gate check (<see cref="GateCheckCoverage.Registry"/>) fail: it asserts the gate blocks, or
/// the policy is refused, because of that check (sc-25390, E6: every gate check has a can-fail test).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class FailsGateCheckAttribute(string check) : Attribute
{
    public string Check { get; } = check;
}

/// <summary>One test method that claims to make gate checks fail, as found in the test assembly.</summary>
/// <param name="Runs">Why it would not run in <c>dotnet test</c> (not an xUnit fact or theory, skipped, explicit, not public), or null when it runs.</param>
public sealed record GateCheckTest(string Name, IReadOnlyList<string> Checks, string? Runs);

/// <summary>
/// The registry of gate checks and the CI check that each has at least one running test that makes it fail
/// (<see cref="FailsGateCheckAttribute"/>). Run by <c>GateCheckCoverageTests</c> in CI's <c>build-test</c>: removing the
/// failing-case test of any check, skipping it or tagging a check that does not exist fails CI.
/// </summary>
public static class GateCheckCoverage
{
    /// <summary>The checks a tier requires (<see cref="GatePolicy.FloorChecks"/>): a policy dropping one is refused.</summary>
    public const string FloorChecks = "policy-floor:checks";

    /// <summary>The paths the sealed tier must cover (<see cref="GatePolicy.MustBeSealed"/>): a policy unsealing one is refused.</summary>
    public const string FloorSealedPaths = "policy-floor:sealed-paths";

    /// <summary>The code floor of paths that call the security review in (<see cref="RiskyPaths"/>), whatever their tier.</summary>
    public const string FloorSecurityReviewPaths = "policy-floor:security-review-paths";

    /// <summary>Every gate check: the named checks a tier can require (<see cref="GateChecks.All"/>) and the policy floors.</summary>
    public static readonly IReadOnlyList<string> Registry = [.. GateChecks.All, FloorChecks, FloorSealedPaths, FloorSecurityReviewPaths];

    /// <summary>Every method of <paramref name="types"/> carrying <see cref="FailsGateCheckAttribute"/>.</summary>
    public static IReadOnlyList<GateCheckTest> Find(IEnumerable<Type> types) =>
        types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => (Type: t, Method: m)))
            .Select(x => (x.Type, x.Method, Checks: x.Method.GetCustomAttributes<FailsGateCheckAttribute>().Select(a => a.Check).ToList()))
            .Where(x => x.Checks.Count > 0)
            .Select(x => new GateCheckTest($"{x.Type.FullName}.{x.Method.Name}", x.Checks, WhyNotRun(x.Type, x.Method)))
            .ToList();

    private static string? WhyNotRun(Type type, MethodInfo method)
    {
        var fact = method.GetCustomAttribute<FactAttribute>(inherit: true);
        return fact is null ? "not an xUnit [Fact] or [Theory]"
            : fact.Skip is not null || fact.SkipUnless is not null || fact.SkipWhen is not null ? "skipped"
            : fact.Explicit ? "explicit (not run by default)"
            : !method.IsPublic || !type.IsPublic && !type.IsNestedPublic ? "not public"
            : type.IsAbstract && !type.IsSealed ? "on an abstract class"
            : null;
    }

    /// <summary>
    /// What is wrong with the coverage of <paramref name="registry"/> by <paramref name="tests"/>: a registered check with no
    /// running test that makes it fail, a test that would not run, a test naming a check that is not registered. Empty: covered.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<string> registry, IReadOnlyList<GateCheckTest> tests)
    {
        var problems = new List<string>();
        problems.AddRange(tests.Where(t => t.Runs is not null).Select(t => $"{t.Name} would not run in CI: {t.Runs}"));
        problems.AddRange(tests.SelectMany(t => t.Checks.Where(c => !registry.Contains(c)).Select(c => $"{t.Name} names '{c}', which is not a registered gate check")));
        problems.AddRange(registry.Where(c => !tests.Any(t => t.Runs is null && t.Checks.Contains(c)))
            .Select(c => $"gate check '{c}' has no running test that makes it fail ([FailsGateCheck(\"{c}\")])"));
        return problems;
    }
}
