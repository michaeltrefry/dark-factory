using System.Diagnostics;

namespace DarkFactory.AcceptanceTests;

/// <summary>The live issue tests' poll-until-seen helper itself (not live).</summary>
public class PollUntilTests
{
    [Fact]
    public async Task It_polls_again_until_the_listing_catches_up()
    {
        var polls = 0;

        var took = await PollUntil.SeenAsync(_ => { polls++; return Task.CompletedTask; }, _ => Task.FromResult(polls >= 3), "issue #1",
            TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(10));

        Assert.Equal((3, 3), (took, polls));
    }

    [Fact]
    public async Task It_fails_naming_what_was_not_seen_once_the_timeout_has_passed()
    {
        var polls = 0;
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => PollUntil.SeenAsync(_ => { polls++; return Task.CompletedTask; },
            _ => Task.FromResult(false), "issue #7 in o/r", TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromMilliseconds(400), interval: TimeSpan.FromMilliseconds(50)));

        Assert.StartsWith($"issue #7 in o/r was not seen after {polls} poll(s)", ex.Message);
        Assert.Contains("limit 0.4 s", ex.Message);
        // It kept polling for the whole limit, and stopped soon after it.
        Assert.InRange(polls, 3, 20);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
    }
}
