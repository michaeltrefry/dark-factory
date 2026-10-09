using System.Diagnostics;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Polls until something the poll should produce is seen. GitHub's <c>issues?since=</c> listing can lag a just-opened issue
/// or a just-posted comment, so one poll right after the write may not see it: the live issue tests poll again, a bounded
/// number of times, before they fail.
/// </summary>
internal static class PollUntil
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="poll"/>, then checks <paramref name="seen"/>; again every <paramref name="interval"/> until it is seen,
    /// or fails the test naming <paramref name="what"/> once <paramref name="timeout"/> has passed. Returns the polls it took.
    /// </summary>
    public static async Task<int> SeenAsync(Func<CancellationToken, Task> poll, Func<CancellationToken, Task<bool>> seen, string what,
        CancellationToken ct, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var gap = interval ?? DefaultInterval;
        var clock = Stopwatch.StartNew();
        for (var polls = 1; ; polls++)
        {
            await poll(ct);
            if (await seen(ct))
            {
                return polls;
            }
            if (clock.Elapsed >= limit)
            {
                Assert.Fail($"{what} was not seen after {polls} poll(s) over {clock.Elapsed.TotalSeconds:0.#} s "
                    + $"(limit {limit.TotalSeconds:0.#} s): GitHub's issues?since= listing may still lag, or the intake never picked it up.");
            }
            await Task.Delay(gap, ct);
        }
    }
}
