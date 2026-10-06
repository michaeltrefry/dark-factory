using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Shortcut;

/// <summary>Parses the story argument of <c>factory run</c>: <c>sc-123</c> or <c>123</c>.</summary>
public static partial class StoryId
{
    [GeneratedRegex(@"^(?:sc-)?(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string? value, out int id)
    {
        id = 0;
        var match = Pattern().Match(value?.Trim() ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out id) && id > 0;
    }

    public static string Format(int id) => $"sc-{id}";

    public static string BranchName(int id) => $"factory/sc-{id}";
}
