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
/// <param name="Method">The test method (null for a hand-made entry).</param>
public sealed record GateCheckTest(string Name, IReadOnlyList<string> Checks, string? Runs, MethodInfo? Method = null);

/// <summary>
/// The registry of gate checks and the CI check that each has at least one running test dedicated to it — tagged
/// <see cref="FailsGateCheckAttribute"/> with that check alone — that makes it fail. Run by <c>GateCheckCoverageTests</c> in
/// CI's <c>build-test</c>: removing a check's dedicated failing-case test, skipping it or tagging a check that does not exist
/// fails CI.
/// </summary>
public static class GateCheckCoverage
{
    /// <summary>The checks a tier requires (<see cref="GatePolicy.FloorChecks"/>): a policy dropping one is refused.</summary>
    public const string FloorChecks = GateCheckSeam.FloorChecks;

    /// <summary>The paths the sealed tier must cover (<see cref="GatePolicy.MustBeSealed"/>): a policy unsealing one is refused.</summary>
    public const string FloorSealedPaths = GateCheckSeam.FloorSealedPaths;

    /// <summary>The code floor of paths that call the security review in (<see cref="RiskyPaths"/>), whatever their tier.</summary>
    public const string FloorSecurityReviewPaths = GateCheckSeam.FloorSecurityReviewPaths;

    /// <summary><see cref="MergeGate"/> blocks when the base's policy is unreadable, missing or invalid.</summary>
    public const string PreconditionPolicy = GateCheckSeam.PreconditionPolicy;

    /// <summary><see cref="MergeGate"/> blocks a PR that is closed, merged or a draft.</summary>
    public const string PreconditionPrOpen = GateCheckSeam.PreconditionPrOpen;

    /// <summary><see cref="MergeGate"/> blocks when the head's diff is unreadable or leaves files out (the tiers of its paths are unknown).</summary>
    public const string PreconditionDiffComplete = GateCheckSeam.PreconditionDiffComplete;

    /// <summary><see cref="MergeGate"/> escalates every change touching a sealed path, whatever the review, the PR or the plan claim.</summary>
    public const string SealedEscalation = GateCheckSeam.SealedEscalation;

    /// <summary>
    /// Every gate check: the named checks a tier can require (<see cref="GateChecks.All"/>), the policy floors, and the merge
    /// gate's fail-closed preconditions and sealed-path escalation.
    /// </summary>
    public static readonly IReadOnlyList<string> Registry =
    [
        .. GateChecks.All, FloorChecks, FloorSealedPaths, FloorSecurityReviewPaths,
        PreconditionPolicy, PreconditionPrOpen, PreconditionDiffComplete, SealedEscalation,
    ];

    /// <summary>Every method of <paramref name="types"/> carrying <see cref="FailsGateCheckAttribute"/>.</summary>
    public static IReadOnlyList<GateCheckTest> Find(IEnumerable<Type> types) =>
        types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => (Type: t, Method: m)))
            .Select(x => (x.Type, x.Method, Checks: x.Method.GetCustomAttributes<FailsGateCheckAttribute>().Select(a => a.Check).ToList()))
            .Where(x => x.Checks.Count > 0)
            .Select(x => new GateCheckTest($"{x.Type.FullName}.{x.Method.Name}", x.Checks, WhyNotRun(x.Type, x.Method), x.Method))
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
    /// running test dedicated to it (tagged with that check alone: a test tagged with several checks may also fail on one of
    /// the others, so it never counts), a test that would not run, a test naming a check that is not registered. Empty: covered.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<string> registry, IReadOnlyList<GateCheckTest> tests)
    {
        var problems = new List<string>();
        problems.AddRange(tests.Where(t => t.Runs is not null).Select(t => $"{t.Name} would not run in CI: {t.Runs}"));
        problems.AddRange(tests.SelectMany(t => t.Checks.Where(c => !registry.Contains(c)).Select(c => $"{t.Name} names '{c}', which is not a registered gate check")));
        problems.AddRange(registry.Where(c => !tests.Any(t => t.Runs is null && t.Checks.Count == 1 && t.Checks[0] == c))
            .Select(c => $"gate check '{c}' has no running test that makes it fail on its own ([FailsGateCheck(\"{c}\")] as its only tag)"));
        return problems;
    }

    /// <summary>
    /// Runs every dedicated test of <paramref name="tests"/> (running, tagged with one check of <paramref name="registry"/>) as xUnit
    /// would — each data row, a fresh instance, its async set-up and disposal — once as it is and once with its check disabled
    /// (<see cref="GateCheckSeam.Disable"/>: the check stops blocking). What is wrong: a row that fails as it is (so the run here
    /// proves nothing), or a row that does not fail an assertion with its check disabled (it does not test that check: e.g. an
    /// empty test, or one asserting something else). Empty: every tagged test fails when its check is broken.
    /// </summary>
    public static async Task<IReadOnlyList<string>> BrokenCheckProblems(IReadOnlyList<string> registry, IReadOnlyList<GateCheckTest> tests)
    {
        var problems = new List<string>();
        foreach (var test in tests.Where(t => t.Runs is null && t.Checks.Count == 1 && registry.Contains(t.Checks[0]) && t.Method is not null))
        {
            var check = test.Checks[0];
            var rows = await RowsAsync(test.Method!);
            if (rows.Count == 0)
            {
                problems.Add($"{test.Name} has no data row to run");
            }
            foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            {
                var name = rows.Count == 1 ? test.Name : $"{test.Name} row {i}";
                if (await InvokeAsync(test.Method!, row) is { } failure)
                {
                    problems.Add($"{name} fails with every check on, so its run here proves nothing: {failure.Message}");
                    continue;
                }
                Exception? broken;
                using (GateCheckSeam.Disable(check))
                {
                    broken = await InvokeAsync(test.Method!, row);
                }
                if (broken is null)
                {
                    problems.Add($"{name} still passes with '{check}' disabled: it does not make that check fail");
                }
                else if (broken is not Xunit.Sdk.IAssertionException)
                {
                    problems.Add($"{name} with '{check}' disabled throws {broken.GetType().Name} rather than failing an assertion: {broken.Message}");
                }
            }
        }
        return problems;
    }

    /// <summary>The argument rows xUnit would run <paramref name="method"/> with: one empty row for a fact, each data attribute's rows for a theory.</summary>
    private static async Task<List<object?[]>> RowsAsync(MethodInfo method)
    {
        var data = method.GetCustomAttributes().OfType<Xunit.v3.IDataAttribute>().ToList();
        if (data.Count == 0)
        {
            return [[]];
        }
        var rows = new List<object?[]>();
        foreach (var attribute in data)
        {
            rows.AddRange(attribute is Xunit.v3.MemberDataAttributeBase member
                ? MemberRows(method, member)
                : (await attribute.GetData(method, new Xunit.Sdk.DisposalTracker())).Select(r => r.GetData()));
        }
        return rows;
    }

    /// <summary>
    /// The rows of a <c>[MemberData]</c>: its static property, field or method (on <c>MemberType</c>, else the test's class) read
    /// here, each item a theory data row or an argument array (its <c>GetData</c> outside a test run yields none).
    /// </summary>
    private static IEnumerable<object?[]> MemberRows(MethodInfo method, Xunit.v3.MemberDataAttributeBase member)
    {
        var type = member.MemberType ?? method.DeclaringType!;
        const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var value = type.GetProperty(member.MemberName, Static)?.GetValue(null)
            ?? type.GetField(member.MemberName, Static)?.GetValue(null)
            ?? type.GetMethods(Static).Single(m => m.Name == member.MemberName && m.GetParameters().Length == (member.Arguments?.Length ?? 0))
                .Invoke(null, member.Arguments);
        foreach (var item in (System.Collections.IEnumerable)value!)
        {
            yield return item switch
            {
                ITheoryDataRow row => row.GetData(),
                object?[] args => args,
                _ => throw new InvalidOperationException($"{type.Name}.{member.MemberName} yields a {item?.GetType().Name ?? "null"}, not a data row."),
            };
        }
    }

    /// <summary>Runs one row of a test method on a fresh instance as xUnit would; the exception it failed with, or null when it passed.</summary>
    private static async Task<Exception?> InvokeAsync(MethodInfo method, object?[] row)
    {
        object? instance = null;
        try
        {
            instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
            if (instance is IAsyncLifetime lifetime)
            {
                await lifetime.InitializeAsync();
            }
            var parameters = method.GetParameters();
            var args = parameters.Select((p, i) => i < row.Length ? row[i] : p.DefaultValue).ToArray();
            switch (method.Invoke(instance, args))
            {
                case Task task:
                    await task;
                    break;
                case ValueTask valueTask:
                    await valueTask;
                    break;
            }
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ex.InnerException;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
