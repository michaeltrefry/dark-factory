namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>
/// A failure the whole factory shares, not one item's: a worker sandbox that is not ready, the ledger migration or the
/// worktree sweep. (Another run's worker phase holding the sandbox user, <see cref="WorkerLock"/>, is none: a run waits for it.)
/// Every item would fail the same way, so the intake loop shows it once on the dashboard and escalates no item for it.
/// </summary>
public sealed class FactoryUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>An error the intake loop hit; <see cref="Count"/> is how many polls in a row hit it.</summary>
public sealed record IntakeError(string Message, DateTimeOffset At, int Count, string? GaveUp = null);

/// <summary>
/// New work the intake loop is not starting (sc-25991): a run the freeze evaluator deferred (<see cref="Id"/> its item, or
/// <c>intake</c> for the poll's own evaluation), or — <see cref="Hold"/> — a factory-wide control the loop found in effect
/// (<see cref="Id"/> its scope: <c>usage</c>, <c>factory</c>, <c>freeze</c>). <see cref="Lifts"/> says what ends it, when
/// <see cref="Why"/> does not already; <see cref="Count"/> is how often in a row it was recorded.
/// </summary>
public sealed record IntakeDeferral(string Id, string Why, string? Lifts, DateTimeOffset At, int Count, bool Hold = false)
{
    public string Message => (Hold ? $"new work held: {Why}" : $"{Id} deferred: {Why}") + (Lifts is null ? "" : $"; {Lifts}");
}

/// <summary>
/// What the <c>factory work</c> intake loop last hit, for the dashboard (E10): the factory-wide error of the last poll,
/// if any — one, however many of the poll's runs hit it —, each item whose runs keep failing before its pipeline starts, and
/// everything that holds new work (<see cref="Deferrals"/>, sc-25991: several runs of one poll may each be deferred). In
/// memory: the dashboard shares the intake loop's process.
/// </summary>
public sealed class IntakeStatus(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IntakeError> _items = [];
    /// <summary>Each deferred run, with the order it was recorded in (a clock may not tell two of one poll apart).</summary>
    private readonly Dictionary<string, (IntakeDeferral Deferral, long Order)> _deferred = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IntakeDeferral> _holds = new(StringComparer.Ordinal);
    private IntakeError? _factory;
    private long _deferrals;

    /// <summary>Raised after every change, on the intake loop's thread: handlers must only schedule their work.</summary>
    public event Action? Changed;

    public IntakeError? FactoryError
    {
        get { lock (_gate) { return _factory; } }
    }

    /// <summary>
    /// The last run the freeze evaluator deferred (outcome <c>deferred</c>, sc-25387), until a run is dispatched again: the message
    /// names the item and why. With several in effect (<see cref="Deferrals"/>), the one recorded last.
    /// </summary>
    public IntakeError? DeferredRun
    {
        get
        {
            lock (_gate)
            {
                return _deferred.Count > 0 && _deferred.Values.MaxBy(d => d.Order).Deferral is var last ? new IntakeError(last.Message, last.At, last.Count) : null;
            }
        }
    }

    /// <summary>
    /// Everything that holds new work, each with what lifts it (sc-25991): the factory-wide controls the loop found in effect
    /// (by scope), then every run deferred since a run was last dispatched, in the order recorded.
    /// </summary>
    public IReadOnlyList<IntakeDeferral> Deferrals
    {
        get
        {
            lock (_gate)
            {
                return [.. _holds.Values.OrderBy(h => h.Id, StringComparer.Ordinal), .. _deferred.Values.OrderBy(d => d.Order).Select(d => d.Deferral)];
            }
        }
    }

    /// <summary>
    /// A run of <paramref name="id"/> (or the poll's own evaluation, <c>intake</c>) was deferred. Other items' deferrals stay:
    /// with several runs in flight each deferred one is shown.
    /// </summary>
    public void Deferred(string id, string why, string? lifts = null)
    {
        lock (_gate)
        {
            _deferred[id] = (new IntakeDeferral(id, why, lifts, time.GetUtcNow(), (_deferred.TryGetValue(id, out var known) ? known.Deferral.Count : 0) + 1), ++_deferrals);
        }
        Raise();
    }

    /// <summary>Runs are dispatched again: no run's deferral stands. (A control's hold is lifted by the loop's next read of it, <see cref="Holding"/>.)</summary>
    public void NotDeferred()
    {
        lock (_gate)
        {
            if (_deferred.Count == 0)
            {
                return;
            }
            _deferred.Clear();
        }
        Raise();
    }

    /// <summary>
    /// The factory-wide controls holding new work as the loop last read them (at a poll's start, or before it dispatched a ready
    /// item): exactly <paramref name="holds"/> — scope, why, what lifts it. One no longer among them is lifted.
    /// </summary>
    public void Holding(IReadOnlyList<(string Scope, string Why, string Lifts)> holds)
    {
        lock (_gate)
        {
            bool Known((string Scope, string Why, string Lifts) h) => _holds.GetValueOrDefault(h.Scope) is { } known && known.Why == h.Why && known.Lifts == h.Lifts;
            if (_holds.Count == holds.Count && holds.All(Known))
            {
                return;
            }
            var now = time.GetUtcNow();
            var next = holds.Select(h => Known(h) ? _holds[h.Scope] : new IntakeDeferral(h.Scope, h.Why, h.Lifts, now, 1, Hold: true)).ToList();
            _holds.Clear();
            foreach (var hold in next)
            {
                _holds[hold.Id] = hold;
            }
        }
        Raise();
    }

    /// <summary>Items whose last runs failed, by external id (e.g. <c>sc-12</c>, <c>gh-3</c>).</summary>
    public IReadOnlyDictionary<string, IntakeError> ItemErrors
    {
        get { lock (_gate) { return new Dictionary<string, IntakeError>(_items); } }
    }

    public void FactoryFailed(string message)
    {
        lock (_gate)
        {
            _factory = new IntakeError(message, time.GetUtcNow(), (_factory?.Count ?? 0) + 1);
        }
        Raise();
    }

    /// <summary>
    /// One more factory-wide failure of a poll that already showed one (sc-25991: another of its runs, or another lane's
    /// listing): the same error — one poll, so <see cref="IntakeError.Count"/> stays — with <paramref name="message"/> added
    /// unless it is already shown.
    /// </summary>
    public void FactoryFailedAlso(string message)
    {
        lock (_gate)
        {
            if (_factory is null)
            {
                _factory = new IntakeError(message, time.GetUtcNow(), 1);
            }
            else if (_factory.Message.Contains(message, StringComparison.Ordinal))
            {
                return;
            }
            else
            {
                _factory = _factory with { Message = $"{_factory.Message}; also: {message}" };
            }
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
    public int ItemFailed(string id, string message)
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
    public void ItemGaveUp(string id, string what)
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

    public void ItemOk(string id)
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
