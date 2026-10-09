namespace DarkFactory.Orchestrator.Router;

/// <summary>
/// The router's model classes (epic E8, owner decision 2026-10-09): a call names a class with <see cref="Header"/> and never a
/// model id, the router picks the model inside it, and every proxied answer names the served model's class in
/// <see cref="ResponseHeader"/>. The router refuses a class sent with <c>x-weave-force-model</c> (400
/// <c>model_class_conflicts_with_force_model</c>), and answers 503 <see cref="Unavailable"/> when no model of the class can
/// serve (it never falls back to another class); a class whose models a spent subscription refuses answers that 429.
/// </summary>
public static class ModelClass
{
    public const string High = "high";
    public const string Mid = "mid";
    public const string Low = "low";

    /// <summary>The request header that restricts the router's pick to one class.</summary>
    public const string Header = "x-weave-model-class";

    /// <summary>The response header naming the class of the model that served the call.</summary>
    public const string ResponseHeader = "X-Weave-Model-Class";

    /// <summary>The router's answer (message prefix / error code) when no model of the requested class can serve the call.</summary>
    public const string Unavailable = "model_class_unavailable";

    /// <summary>
    /// The Messages API's required body <c>model</c> for a class request. The router's in-class pick serves the call; the
    /// value is a name no catalog model has, so no passthrough lane can serve it verbatim (probed 2026-10-09: accepted, served
    /// in class).
    /// </summary>
    public const string RequestModel = "default";

    /// <summary>The served class the response names (trimmed, lower case), or null when the header is missing or blank.</summary>
    public static string? Served(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ResponseHeader, out var values)
        && values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } value
            ? value.Trim().ToLowerInvariant()
            : null;

    /// <summary>Whether a router error answer is its "no model of the class can serve" refusal (a usage refusal, never the caller's fault).</summary>
    public static bool IsUnavailable(string body) => body.Contains(Unavailable, StringComparison.OrdinalIgnoreCase);
}
