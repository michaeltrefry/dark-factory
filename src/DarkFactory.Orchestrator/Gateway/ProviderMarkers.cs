namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The model provider API hosts and key variables the gateway lint refuses (DF0003), read at runtime from the same file the
/// analyzer embeds (<c>src/DarkFactory.Analyzers/ProviderMarkers.txt</c>, embedded here as <see cref="Resource"/>): the lint sees
/// only the code, so a provider host given through configuration (<c>Router:BaseUrl</c>) is refused here (Phase 1 E1: every
/// model call goes through the router only).
/// </summary>
public static class ProviderMarkers
{
    public const string Resource = "provider-markers.txt";

    public static readonly IReadOnlyList<string> All = Load();

    /// <summary>The provider marker <paramref name="text"/> names (case-insensitively), if any.</summary>
    public static string? In(string text) => All.FirstOrDefault(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static List<string> Load()
    {
        using var stream = typeof(ProviderMarkers).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The orchestrator lacks its embedded {Resource}.");
        using var reader = new StreamReader(stream);
        var markers = reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        return markers.Count > 0 ? markers : throw new InvalidOperationException($"The orchestrator's {Resource} lists no marker.");
    }
}
