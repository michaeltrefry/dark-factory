using DarkFactory.Orchestrator.Ledger;
using static DarkFactory.Orchestrator.Ledger.WorkState;

namespace DarkFactory.Orchestrator.Tests;

public class LifecycleTests
{
    private static readonly TransitionContext None = new();
    private static readonly WorkState[] All = Enum.GetValues<WorkState>();
    private static readonly WorkState[] NonTerminal = All.Where(s => !Lifecycle.IsTerminal(s)).ToArray();

    private static bool Legal(WorkState from, WorkState to, TransitionContext? context = null) =>
        Lifecycle.Check(from, to, context ?? None) is null;

    [Theory]
    [InlineData(Intake, Plan)]
    [InlineData(Intake, Implement)]
    [InlineData(Plan, Implement)]
    [InlineData(Implement, Review)]
    [InlineData(Review, Fixing)]
    [InlineData(Fixing, Review)]
    [InlineData(Review, CI)]
    [InlineData(CI, CIHealing)]
    [InlineData(CIHealing, CI)]
    [InlineData(CI, MergeGate)]
    [InlineData(MergeGate, Merge)]
    [InlineData(CI, Review)] // a push voided the verdict (E3)
    [InlineData(MergeGate, Review)]
    [InlineData(MergeGate, Escalated)]
    [InlineData(Merge, Watch)]
    [InlineData(Watch, Done)]
    [InlineData(Watch, Intake)]
    [InlineData(Watch, Implement)]
    [InlineData(Escalated, Intake)]
    public void Spec_lifecycle_steps_are_legal(WorkState from, WorkState to) => Assert.True(Legal(from, to), Lifecycle.Check(from, to, None));

    [Theory]
    [InlineData(Intake, Merge)]
    [InlineData(Intake, Review)]
    [InlineData(Implement, CI)]
    [InlineData(Implement, Intake)]
    [InlineData(Review, Merge)]
    [InlineData(Review, Implement)]
    [InlineData(CI, Merge)]
    [InlineData(Fixing, CI)]
    [InlineData(Merge, Done)]
    [InlineData(Escalated, Implement)]
    [InlineData(Implement, Implement)]
    public void Shortcuts_and_backwards_steps_are_illegal(WorkState from, WorkState to)
    {
        Assert.False(Legal(from, to));
        var ex = Assert.Throws<IllegalTransitionException>(() => Lifecycle.Ensure(from, to, None));
        Assert.Contains($"{from} → {to}", ex.Message);
    }

    [Fact]
    public void Pause_and_cancel_are_reachable_from_every_non_terminal_state()
    {
        Assert.All(NonTerminal, s => Assert.True(Legal(s, Cancelled), $"{s} → Cancelled"));
        Assert.All(NonTerminal.Where(s => s != Paused), s => Assert.True(Legal(s, Paused), $"{s} → Paused"));
        Assert.False(Legal(Paused, Paused));
    }

    [Fact]
    public void Terminal_states_go_nowhere()
    {
        Assert.Equal([Done, Cancelled], All.Where(Lifecycle.IsTerminal));
        Assert.All(All, to => Assert.False(Legal(Done, to)));
        Assert.All(All, to => Assert.False(Legal(Cancelled, to)));
    }

    [Fact]
    public void Paused_returns_only_to_the_state_it_paused_from()
    {
        foreach (var from in NonTerminal.Where(s => s != Paused))
        {
            var context = new TransitionContext(PausedFrom: from);
            Assert.All(All.Where(s => s != from && s != Cancelled), to => Assert.False(Legal(Paused, to, context), $"Paused({from}) → {to}"));
            Assert.True(Legal(Paused, from, context));
        }
        Assert.False(Legal(Paused, Implement)); // unknown origin
    }

    [Fact]
    public void Any_active_state_can_escalate_but_not_twice_or_while_paused()
    {
        Assert.All(NonTerminal.Where(s => s is not Paused and not Escalated), s => Assert.True(Legal(s, Escalated), $"{s} → Escalated"));
        Assert.False(Legal(Escalated, Escalated));
        Assert.False(Legal(Paused, Escalated, new TransitionContext(PausedFrom: Implement)));
    }

    [Fact]
    public void Review_to_fixing_is_capped_at_three_rounds()
    {
        Assert.True(Legal(Review, Fixing, new TransitionContext(FixRounds: 2)));
        Assert.False(Legal(Review, Fixing, new TransitionContext(FixRounds: 3)));
        Assert.True(Legal(Review, Escalated, new TransitionContext(FixRounds: 3)));
    }

    [Fact]
    public void Context_from_history_finds_pause_origin_and_counts_fix_rounds_since_implement()
    {
        Assert.Equal(new TransitionContext(Review, 1),
            TransitionContext.From([Intake, Implement, Review, Fixing, Paused, Fixing, Review, Paused]));
        Assert.Equal(new TransitionContext(null, 0),
            TransitionContext.From([Intake, Implement, Review, Fixing, Review, CI, MergeGate, Merge, Watch, Implement]));
    }
}
