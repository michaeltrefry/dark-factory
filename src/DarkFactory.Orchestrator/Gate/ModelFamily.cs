namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Which vendor's model family a model id belongs to, so the reviewer can be pinned to a family the implementer did not
/// use. Deterministic and fail-closed: an id it does not recognise has no family (null), and the pipeline then refuses
/// to treat it as "different".
/// </summary>
public static class ModelFamily
{
    // Prefixes of the model id (after any "provider/" prefix), lowercased, mapped to their family.
    private static readonly (string Prefix, string Family)[] Prefixes =
    [
        ("claude", "anthropic"), ("opus", "anthropic"), ("sonnet", "anthropic"), ("haiku", "anthropic"),
        ("gpt", "openai"), ("chatgpt", "openai"), ("codex", "openai"), ("o1", "openai"), ("o3", "openai"), ("o4", "openai"),
        ("gemini", "google"), ("gemma", "google"),
        ("grok", "xai"),
        ("deepseek", "deepseek"),
        ("qwen", "alibaba"),
        ("kimi", "moonshot"), ("moonshot", "moonshot"),
        ("glm", "zhipu"),
        ("llama", "meta"),
        ("mistral", "mistral"), ("codestral", "mistral"), ("devstral", "mistral"),
    ];

    /// <summary>The family of <paramref name="model"/>, or null when it is not recognised.</summary>
    public static string? Of(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }
        var id = model.Trim().ToLowerInvariant();
        id = id[(id.LastIndexOf('/') + 1)..];
        foreach (var (prefix, family) in Prefixes)
        {
            // "o1" must not match "opus"-like ids by accident: a prefix ends at the id's end or a separator.
            if (id.StartsWith(prefix, StringComparison.Ordinal)
                && (id.Length == prefix.Length || !char.IsLetter(id[prefix.Length]) || prefix.Length > 2))
            {
                return family;
            }
        }
        return null;
    }
}

/// <summary>Why no different-family reviewer could be chosen; the item escalates with it.</summary>
public sealed class ReviewerChoiceException(string message) : Exception(message);

public static class ReviewerChoice
{
    /// <summary>
    /// The first of <paramref name="candidates"/> whose family is none of the implementer's. Throws
    /// <see cref="ReviewerChoiceException"/> when the implementer's models are unknown, any of them has no known family,
    /// or every candidate shares a family with them.
    /// </summary>
    public static string Choose(IReadOnlyList<string> candidates, IReadOnlyCollection<string> implementerModels)
    {
        var families = ImplementerFamilies(implementerModels);
        return candidates.FirstOrDefault(c => ModelFamily.Of(c) is { } f && !families.Contains(f))
            ?? throw new ReviewerChoiceException(
                $"No configured reviewer model ({string.Join(", ", candidates)}) is of a family other than the implementer's "
                + $"({string.Join(", ", families)}); set Review:Models.");
    }

    /// <summary>The implementer's families; throws <see cref="ReviewerChoiceException"/> when any is unknown.</summary>
    public static IReadOnlySet<string> ImplementerFamilies(IReadOnlyCollection<string> implementerModels)
    {
        if (implementerModels.Count == 0)
        {
            throw new ReviewerChoiceException(
                "The implementer's model is unknown (its session reported none), so no reviewer of a different family can be chosen.");
        }
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in implementerModels)
        {
            families.Add(ModelFamily.Of(model)
                ?? throw new ReviewerChoiceException(
                    $"The implementer used model '{model}', whose family is unknown, so no reviewer can be proven to be of a different family."));
        }
        return families;
    }
}
