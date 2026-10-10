using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// The router model class every worker session names (E8, sc-25659): the router picks the model inside the class
/// (<c>x-weave-model-class</c>), the worker never pins a model, and the factory never falls back to another class. The choice is
/// this deterministic code, never a model's: the implementer and its fix rounds run on the item's coding class
/// (<see cref="Coding"/>), the CI fixer on <see cref="CiFix"/>, issue triage on <see cref="Triage"/>.
/// </summary>
public static class WorkerModelClass
{
    /// <summary>The router's request header naming the class (<see cref="Router.ModelClass.Header"/>).</summary>
    public const string Header = Router.ModelClass.Header;

    public const string High = Router.ModelClass.High;
    public const string Mid = Router.ModelClass.Mid;
    public const string Low = Router.ModelClass.Low;

    /// <summary>The label that makes a story simple whatever its estimate.</summary>
    public const string SimpleLabel = "simple";

    /// <summary>The CI-heal fixer's class.</summary>
    public const string CiFix = Mid;

    /// <summary>The issue triage worker's class.</summary>
    public const string Triage = Low;

    /// <summary>Whether <paramref name="modelClass"/> is one the router takes (exactly <c>high</c>, <c>mid</c> or <c>low</c>).</summary>
    public static bool IsValid(string? modelClass) => modelClass is High or Mid or Low;

    /// <summary>
    /// A story is simple when it carries the <see cref="SimpleLabel"/> label (any case) or an estimate of 1 or 2 points; otherwise
    /// (no estimate included) it is complex.
    /// </summary>
    public static bool IsSimple(WorkStory story) =>
        story.Estimate is 1 or 2 || (story.Labels ?? []).Any(l => string.Equals(l, SimpleLabel, StringComparison.OrdinalIgnoreCase));

    /// <summary>The item's coding class (the implementer and its review and conflict fix rounds): <c>low</c> when simple, else <c>mid</c>.</summary>
    public static string Coding(WorkStory story) => IsSimple(story) ? Low : Mid;
}
