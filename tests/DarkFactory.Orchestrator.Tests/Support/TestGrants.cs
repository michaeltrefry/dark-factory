using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Push grants for tests of the git layer itself (production grants come only from <see cref="Ledger.WorkLedger.GrantPushAsync"/>).</summary>
internal static class TestGrants
{
    public static readonly PushGrant Untainted = new(["test-session"]);
}
