namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Not live (sc-25992): where a throwaway ledger starts its Kanban keys (<see cref="E2e.KanbanKeyStart"/>). The live P4b runs of
/// 2026-10-14 each started at <c>kb-1</c>, so P4b-AT4 pushed to the branch P4b-AT1 had merged (<c>factory/kb-1</c>) and left
/// its draft pull request there, and a re-run of P4b-AT2 would have reused the escalated run's <c>factory/kb-2</c> and adopted
/// its open pull request.
/// </summary>
public class KanbanKeyStartTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Runs_a_second_apart_start_their_keys_apart_and_far_from_1()
    {
        var now = new DateTimeOffset(2026, 10, 14, 11, 12, 0, TimeSpan.Zero);
        var first = E2e.KanbanKeyStart(now);
        Assert.True(first > 1_000_000, $"{first}");
        Assert.Equal(first + 1, E2e.KanbanKeyStart(now.AddSeconds(1)));
    }

    [Fact]
    public void The_start_stays_an_int_for_decades_and_is_never_below_1()
    {
        Assert.Equal(1, E2e.KanbanKeyStart(Epoch));
        Assert.Equal(1, E2e.KanbanKeyStart(Epoch.AddDays(-1)));
        Assert.True(E2e.KanbanKeyStart(new DateTimeOffset(2094, 1, 1, 0, 0, 0, TimeSpan.Zero)) > 0);
    }
}
