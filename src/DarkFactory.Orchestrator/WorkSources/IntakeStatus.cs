namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>
/// A failure the whole factory shares, not one item's: the run lock another sandboxed run holds
/// (<see cref="WorkerLock"/>), a worker sandbox that is not ready, the ledger migration or the worktree sweep.
/// Every item would fail the same way, so the intake loop shows it once on the dashboard and escalates no item for it.
/// </summary>
public sealed class FactoryUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>An error the intake loop hit; <see cref="Count"/> is how many polls in a row hit it.</summary>
public sealed record IntakeError(string Message, DateTimeOffset At, int Count, string? GaveUp = null);

/// <summary>
/// What the <c>factory work</c> intake loop last hit, for the dashboard (E10): the factory-wide error of the last poll,
/// if any, and each item whose runs keep failing before its pipeline starts. In memory: the dashboard shares the
/// intake loop's process.
/// </summary>
public sealed class IntakeStatus(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, IntakeError> _items = [];
    private IntakeError? _factory;

    /// <summary>Raised after every change, on the intake loop's thread: handlers must only schedule their work.</summary>
    public event Action? Changed;

    public IntakeError? FactoryError
    {
        get { lock (_gate) { return _factory; } }
    }

    /// <summary>Items whose last runs failed, by story id.</summary>
    public IReadOnlyDictionary<int, IntakeError> ItemErrors
    {
        get { lock (_gate) { return new Dictionary<int, IntakeError>(_items); } }
    }

    public void FactoryFailed(string message)
    {
        lock (_gate)
        {
            _factory = new IntakeError(message, time.GetUtcNow(), (_factory?.Count ?? 0) + 1);
        }
        Raise();
    }

    public void FactoryOk()
    {
        lock (_gate)
        {
            if (_factory is null)
            {
                return;
            }
            _factory = null;
        }
        Raise();
    }

    /// <summary>Records one more failed run of the item; returns how many in a row have failed.</summary>
    public int ItemFailed(int id, string message)
    {
        int count;
        lock (_gate)
        {
            var previous = _items.GetValueOrDefault(id);
            count = (previous?.Count ?? 0) + 1;
            _items[id] = new IntakeError(message, time.GetUtcNow(), count, previous?.GaveUp);
        }
        Raise();
        return count;
    }

    /// <summary>The intake loop gave up on the item (escalated, parked or commented); <paramref name="what"/> says how.</summary>
    public void ItemGaveUp(int id, string what)
    {
        lock (_gate)
        {
            if (_items.GetValueOrDefault(id) is not { } error)
            {
                return;
            }
            _items[id] = error with { GaveUp = what };
        }
        Raise();
    }

    public void ItemOk(int id)
    {
        lock (_gate)
        {
            if (!_items.Remove(id))
            {
                return;
            }
        }
        Raise();
    }

    private void Raise()
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // one broken viewer must not stop the intake loop
            }
        }
    }
}
