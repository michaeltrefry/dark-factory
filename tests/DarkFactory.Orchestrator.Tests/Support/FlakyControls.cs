using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// Controls whose per-item read (<see cref="IControls.EffectiveAsync"/>) fails while <see cref="Broken"/> or for the next
/// <see cref="FailNext"/> reads, as an unreachable ledger would; everything else reads and writes through <paramref name="inner"/>.
/// </summary>
internal sealed class FlakyControls(IControls inner) : IControls
{
    private readonly Lock _gate = new();
    private int _failNext;
    private int _failures;

    public volatile bool Broken;

    /// <summary>The next this many reads fail (then reads work again).</summary>
    public int FailNext
    {
        set
        {
            lock (_gate)
            {
                _failNext = value;
            }
        }
    }

    /// <summary>Reads that failed so far.</summary>
    public int Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures;
            }
        }
    }

    public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Broken || _failNext > 0)
            {
                _failNext = Math.Max(0, _failNext - 1);
                _failures++;
                return Task.FromException<ControlState>(new InvalidOperationException("the controls table cannot be read"));
            }
        }
        return inner.EffectiveAsync(externalId, epicId, ct);
    }

    public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    public Task<Control?> UsagePauseAsync(CancellationToken ct) => inner.UsagePauseAsync(ct);
    public Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct) => inner.PauseForUsageAsync(resumeAt, reason, ct);
    public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) =>
        inner.FreezeAsync(trigger, detail, readChangedAt, ct);
    public Task<Control?> GetAsync(string scope, CancellationToken ct) => inner.GetAsync(scope, ct);
    public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => inner.SetAsync(scope, state, by, ct);
    public Task ClearAsync(string scope, CancellationToken ct) => inner.ClearAsync(scope, ct);
}
