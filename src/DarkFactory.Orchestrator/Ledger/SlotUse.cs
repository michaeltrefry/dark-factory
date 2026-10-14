namespace DarkFactory.Orchestrator.Ledger;

/// <summary>What a run is doing with a worker slot, as its last slot-use row says (sc-25991).</summary>
public enum SlotUseKind
{
    /// <summary>Neither holding nor waiting: no worker phase now.</summary>
    None,
    /// <summary>Waiting for a slot another run's worker phase holds.</summary>
    Waiting,
    /// <summary>In a worker phase: it holds the slot's lock.</summary>
    Holding,
}

/// <summary>
/// A run's use of a worker slot, read from the ledger (sc-25991) so that the dashboard is right for the runs of any process —
/// a <c>factory run</c> beside <c>factory work</c> — and after a restart: the last of the item's
/// <see cref="RunPipeline.Steps.SlotWait"/>, <see cref="RunPipeline.Steps.SlotHeld"/> and
/// <see cref="RunPipeline.Steps.SlotReleased"/> rows. One row per event, never per poll of a wait: a wait writes one when it
/// starts; a worker phase one when it has the slot and one when it gives it up (a phase inside a phase writes none).
/// A row says what the run last recorded, not that the run is alive: a run that crashed or was killed inside a phase leaves
/// <see cref="SlotUseKind.Holding"/> as its last row — whoever shows it asks the item's run lock too
/// (<see cref="PostgresRunLocks.HeldAsync"/>: a dead run's lock is gone with its connection) and calls it stale without one.
/// The Detail of each row is <c>&lt;slot&gt; &lt;user&gt; &lt;what the phase does&gt;</c>, e.g. <c>2 _factory2 implement</c>; a
/// wait for any free slot (an item that has none yet) is <c>any - implement</c>.
/// </summary>
/// <param name="Slot">The slot held, waited for or given up; null for a wait for any free one.</param>
/// <param name="User">That slot's sandbox user; null with no slot.</param>
/// <param name="What">What the worker phase does (<c>implement</c>, <c>run the new tests</c>, <c>plan</c>, …), in the pipeline's own words.</param>
/// <param name="Row">The ledger id of the row it was read from (0 when not read from one).</param>
public sealed record SlotUse(SlotUseKind Kind, int? Slot, string? User, string What, long Row = 0)
{
    public static readonly SlotUse None = new(SlotUseKind.None, null, null, "");

    private const string Any = "any";
    private const string NoUser = "-";

    public static bool IsStep(string? step) =>
        step is RunPipeline.Steps.SlotWait or RunPipeline.Steps.SlotHeld or RunPipeline.Steps.SlotReleased;

    /// <summary>
    /// Whether <paramref name="step"/> is a worker slot's bookkeeping — the item's slot (<see cref="RunPipeline.Steps.Slot"/>,
    /// sc-25989), a stop's record of a slot no longer configured (<see cref="RunPipeline.Steps.SlotLeft"/>) or the run's use of
    /// it (<see cref="IsStep"/>, sc-25991). Such a row is written around a phase (a slot's release in the phase's <c>finally</c>,
    /// before an escalation's row), so it is never "the step the item reached": every reader of an item's last row by position
    /// passes over these (the escalation comment's last ledger state, a plan's "not evaluated" row still being its last, the
    /// worker attempt's rows).
    /// </summary>
    public static bool IsBookkeeping(string? step) =>
        step is RunPipeline.Steps.Slot or RunPipeline.Steps.SlotLeft || IsStep(step);

    /// <summary>A slot-use row's Detail.</summary>
    public static string Detail(int? slot, string? user, string what) => $"{slot?.ToString() ?? Any} {user ?? NoUser} {what}";

    /// <summary>The use the newest slot-use row of <paramref name="history"/> (one item's rows, oldest first) records; none without one.</summary>
    public static SlotUse Of(IEnumerable<LedgerEntry> history) =>
        history.LastOrDefault(e => IsStep(e.Step)) is { } row ? Parse(row.Step!, row.Detail) with { Row = row.Id } : None;

    private static SlotUse Parse(string step, string? detail)
    {
        var parts = (detail ?? "").Split(' ', 3);
        var slot = int.TryParse(parts[0], out var number) && number >= 1 ? number : (int?)null;
        var user = parts.Length > 1 && parts[1] != NoUser ? parts[1] : null;
        var what = parts.Length > 2 ? parts[2] : "";
        return step switch
        {
            RunPipeline.Steps.SlotWait => new(SlotUseKind.Waiting, slot, user, what),
            // A held row always names its slot: one that does not read back is not shown as a hold.
            RunPipeline.Steps.SlotHeld when slot is not null => new(SlotUseKind.Holding, slot, user, what),
            _ => new(SlotUseKind.None, slot, user, what),
        };
    }

    /// <summary><c>slot 2 (_factory2)</c>.</summary>
    public string Named => Slot is { } slot ? $"slot {slot}{(User is null ? "" : $" ({User})")}" : "a slot";
}
