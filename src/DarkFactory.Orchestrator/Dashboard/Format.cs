using System.Globalization;

namespace DarkFactory.Orchestrator.Dashboard;

public static class Format
{
    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        return elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h"
            : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m"
            : $"{elapsed.Minutes}m {elapsed.Seconds:00}s";
    }

    /// <summary>The router's cost in USD, or a dash while none is known.</summary>
    public static string Cost(decimal? usd) => usd is { } c ? c.ToString("$0.00##", CultureInfo.InvariantCulture) : "—";
}
