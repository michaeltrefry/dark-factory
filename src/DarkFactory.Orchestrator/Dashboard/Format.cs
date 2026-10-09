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

    /// <summary>The router's cost in USD, or N/A while none is measured (never shown as $0, E5).</summary>
    public static string Cost(decimal? usd) => usd is { } c ? c.ToString("$0.00##", CultureInfo.InvariantCulture) : "N/A";

    /// <summary>
    /// An item's spend: the sum of its sessions' measured router costs (<paramref name="measured"/>), labelled with how many of its
    /// <paramref name="sessions"/> it covers when some are not measured (yet) — <c>$1.50 of 1/2 measured</c> — and N/A when none is
    /// (E5, as <see cref="Ledger.LedgerReport.Cost"/>).
    /// </summary>
    public static string ItemCost(decimal? measured, IReadOnlyList<SessionLink> sessions)
    {
        var known = sessions.Count(s => s.CostUsd is not null);
        return measured is null ? "N/A"
            : known < sessions.Count ? $"{Cost(measured)} of {known}/{sessions.Count} measured"
            : Cost(measured);
    }
}
