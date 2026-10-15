using System.Text;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>
/// A recipe's steps as commands (sc-26232): for the gate's test runs (<see cref="TestSteps"/>: <see cref="TestStep"/>s in the
/// <see cref="TestPhase.Restore"/> phase, run sandboxed by <see cref="SandboxTestRunner"/> like every other step) and for a worker's
/// <c>factory-prepare</c> (<see cref="WorkerPlan"/>). A step with variables runs as <c>/usr/bin/env NAME=value … program args</c>:
/// the launch helper passes no variable but the router's (it builds the environment from scratch), so the recipe's go on env's
/// command line — data, never a shell string. The program is the recipe's own (an absolute path, a cache path, or a plain name
/// resolved on the launch helper's fixed PATH): recipe argv is data run through the existing <see cref="TestStep.Program"/> seam,
/// which the gateway lint's <c>KnownLocalPrograms</c> names. Which program it is, is not judged (any can reach the network).
/// </summary>
public static class PrepareSteps
{
    /// <summary>env(1), through which a step with variables runs.</summary>
    public const string EnvPath = "/usr/bin/env";

    /// <summary>The first field of a worker plan (<see cref="WorkerPlan"/>), naming its format.</summary>
    public const string PlanHeader = "factory-prepare-plan 1";

    private static readonly Regex ArtifactPlaceholder = new(@"\$\{artifact:(?<name>[^}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The recipe's steps for the gate: placeholders of artifacts expanded to their cached paths now, <see cref="PrepareRecipe.WorktreePlaceholder"/>
    /// left for the runner (<see cref="TestStep.ExpandsWorktree"/>, the run's own worktree).
    /// </summary>
    public static IReadOnlyList<TestStep> TestSteps(PrepareRecipe recipe, IReadOnlyDictionary<string, string> artifacts) =>
        recipe.Steps.Select(step =>
        {
            var (program, args) = CommandLine(step, value => ExpandArtifacts(value, artifacts));
            return new TestStep(TestPhase.Restore, program, args)
            {
                WorkingDirectory = step.WorkingDirectory,
                Timeout = step.Timeout,
                ExpandsWorktree = true,
                Label = $"prepare step {step.Name}",
            };
        }).ToList();

    /// <summary>The program and arguments of <paramref name="step"/>, each value passed through <paramref name="expand"/>.</summary>
    internal static (string Program, IReadOnlyList<string> Args) CommandLine(PrepareStep step, Func<string, string> expand)
    {
        var argv = step.Argv.Select(expand).ToList();
        if (step.Environment.Count == 0)
        {
            return (argv[0], argv.Skip(1).ToList());
        }
        return (EnvPath, step.Environment.Select(kv => $"{kv.Key}={expand(kv.Value)}").Concat(argv).ToList());
    }

    /// <summary><paramref name="value"/> with each <c>${artifact:…}</c> replaced by its cached path; <c>${worktree}</c> kept.</summary>
    public static string ExpandArtifacts(string value, IReadOnlyDictionary<string, string> artifacts) =>
        ArtifactPlaceholder.Replace(value, m => artifacts.TryGetValue(m.Groups["name"].Value, out var path)
            ? path
            : throw new PrepareRecipeException($"{PrepareRecipe.Path}: no cached path for ${{artifact:{m.Groups["name"].Value}}}"));

    /// <summary>
    /// Inserts the recipe's <paramref name="prepare"/> steps into a strategy's <paramref name="steps"/>: after its leading
    /// <see cref="TestPhase.Toolchain"/> probes (a missing toolchain is still reported as such), before its restore and build.
    /// </summary>
    public static IReadOnlyList<TestStep> Before(IReadOnlyList<TestStep> prepare, IReadOnlyList<TestStep> steps)
    {
        if (prepare.Count == 0)
        {
            return steps;
        }
        var leading = steps.TakeWhile(s => s.Phase == TestPhase.Toolchain).Count();
        return [.. steps.Take(leading), .. prepare, .. steps.Skip(leading)];
    }

    /// <summary>
    /// The plan a worker's <c>factory-prepare</c> runs, every placeholder expanded (<paramref name="worktree"/>, the cached
    /// <paramref name="artifacts"/>): NUL-terminated fields — <see cref="PlanHeader"/>, the step count, then per step its name, its
    /// directory (relative to the repository root), the count of <c>NAME=value</c> variables and each, the count of arguments and
    /// each (the program first).
    /// </summary>
    public static string WorkerPlan(PrepareRecipe recipe, string worktree, IReadOnlyDictionary<string, string> artifacts)
    {
        var text = new StringBuilder();
        void Field(string value) => text.Append(value.Contains('\0') ? throw new PrepareRecipeException("a value holds a NUL") : value).Append('\0');
        Field(PlanHeader);
        Field(recipe.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var step in recipe.Steps)
        {
            string Expand(string v) => PrepareRecipe.Expand(v, worktree, artifacts);
            Field(step.Name);
            Field(step.WorkingDirectory);
            Field(step.Environment.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var (name, value) in step.Environment)
            {
                Field($"{name}={Expand(value)}");
            }
            Field(step.Argv.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var arg in step.Argv)
            {
                Field(Expand(arg));
            }
        }
        return text.ToString();
    }
}
