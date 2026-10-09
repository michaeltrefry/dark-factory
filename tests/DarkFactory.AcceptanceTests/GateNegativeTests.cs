using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Epic AT4 (sc-25391), the gate's negatives on seeded PRs against the sandbox (docs/acceptance.md, P2 epic AT4). Live: skipped unless
/// FACTORY_E2E=1 and each test's own story variable. Each runs <c>factory run</c> (production wiring, a throwaway ledger) on a To Do
/// bug story whose fix lands in normal code of the sandbox (like P2-AT1's), with the review panel replaced by
/// <see cref="SeededReviewer"/> so the gate's own rule decides deterministically, then seeds what the case needs on the PR branch
/// (as the workers' App) or on a throwaway branch (as the owner): never the sandbox's main. None of them merges: each leaves the item
/// Escalated, and closes its PR and deletes its branch at the end. Push-after-verdict is <see cref="ReviewGateTests"/>' P2-AT2.
/// </summary>
public class GateNegativeTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(90);

    private sealed record Run(E2e E2e, FactoryOptions Options, int StoryId, SandboxRepo Sandbox, CancellationTokenSource Timeout) : IAsyncDisposable
    {
        public CancellationToken Ct => Timeout.Token;

        public async ValueTask DisposeAsync()
        {
            await Sandbox.CloseItemAsync(StoryId);
            Sandbox.Dispose();
            Timeout.Dispose();
            await E2e.DisposeAsync();
        }
    }

    private static async Task<Run> StartAsync(string variable, string prefix)
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story(variable, "a To Do bug story whose fix lands in normal (not free, sealed or protected) code of the sandbox repo");
        var e2e = await E2e.StartAsync(prefix);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var options = e2e.Options();
            timeout.CancelAfter(RunTimeout);
            await ReviewGateTests.RequireGateReadyAsync(options, timeout.Token);
            return new Run(e2e, options, storyId, new SandboxRepo(options, options.DefaultRepo), timeout);
        }
        catch
        {
            // A skip (or a failure) before the run: drop the throwaway ledger and release the work root's lock.
            timeout.Dispose();
            await e2e.DisposeAsync();
            throw;
        }
    }

    private static async Task<RunOutcome> FactoryRunAsync(Run run, Func<GateStage, GateStage> adjust)
    {
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        return await FactoryRunner.RunAsync(run.Options, FactoryRunner.CreateWorkSource(run.Options, shortcutHttp), run.StoryId, ignoreScope: true,
            Console.Out, run.Ct, adjust);
    }

    /// <summary>The item escalated at the gate and nothing merged: the last <c>gate</c> row is a Blocked decision, gate_rejected.</summary>
    private static async Task<(List<LedgerEntry> History, LedgerEntry Gate)> AssertBlockedAtTheGateAsync(Run run, RunOutcome outcome, string reason)
    {
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains(reason, outcome.Error);
        var history = await run.E2e.HistoryAsync(run.StoryId, run.Ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.Equal([WorkState.MergeGate, WorkState.Escalated], history.Where(e => e.Step is null).Select(e => e.State).TakeLast(2));
        Assert.DoesNotContain(history, e => e.Step is null && e.State == WorkState.Merge);
        Assert.DoesNotContain(history, e => e.Step == RunPipeline.Steps.GatePassed);
        var gate = history.Last(e => e.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith($"{GateOutcome.Blocked} ", gate.Detail);
        Assert.Contains(reason, gate.Detail);
        Assert.Equal(StepOutcome.GateRejected, gate.Outcome);
        Assert.Equal(StepOutcome.Escalated, history.Last(e => e.Step is null).Outcome);
        var pr = await LinkedPullAsync(run, history);
        Assert.Equal(JsonValueKind.Null, pr.GetProperty("merged_at").ValueKind);
        return (history, gate);
    }

    /// <summary>
    /// The item's PR on GitHub, found by the PR its ledger's <c>linked</c> checkpoint names: the ledger, not the run's
    /// outcome, is the record of which PR the item opened.
    /// </summary>
    private static async Task<JsonElement> LinkedPullAsync(Run run, List<LedgerEntry> history)
    {
        var url = RunPipeline.LinkedPullRequestUrl(history);
        Assert.True(url is not null, $"sc-{run.StoryId}'s ledger links no pull request.");
        return (await E2e.PullRequestsAsync(run.Sandbox.Repo, run.StoryId, run.Ct)).Single(p => p.GetProperty("html_url").GetString() == url);
    }

    /// <summary>
    /// AT4: a sealed path on the PR's head escalates instead of merging. Before the first review the test pushes
    /// <c>factory/prompts/e2e-sealed-probe.md</c> (a sealed directory on every valid policy: the floor seals <c>factory/prompts/</c>) to
    /// the PR branch as the workers' App; the head moves, the new head is reviewed (seeded pass) and goes through CI, and the gate,
    /// classifying the head's own diff, escalates it whatever the review said.
    /// </summary>
    [Fact]
    public async Task A_sealed_path_on_the_pr_head_escalates_instead_of_merging()
    {
        await using var run = await StartAsync("FACTORY_E2E_GATE_SEALED_STORY", "df_e2e_p2at4_sealed");
        const string probe = "factory/prompts/e2e-sealed-probe.md";
        var reviewer = new SeededReviewer(beforeFirst: async (request, ct) =>
        {
            var token = await run.Sandbox.AppTokenAsync(ct);
            await run.Sandbox.PutFileAsync(token, StoryId.BranchName(run.StoryId), probe,
                $"[dark-factory e2e] a sealed-path probe ({DateTimeOffset.UtcNow:O})\n", "acceptance: touch a sealed path", ct);
        });

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = reviewer });

        var (_, gate) = await AssertBlockedAtTheGateAsync(run, outcome, "touches sealed path(s), which always escalate");
        Assert.Contains($"{probe} (sealed)", gate.Detail);
        Assert.True(reviewer.Reviews > 0);
    }

    /// <summary>
    /// AT4: a corrupt <c>factory/gate.yaml</c> blocks every merge. The test (as the owner) branches <c>e2e/corrupt-policy-&lt;guid&gt;</c>
    /// off the sandbox's main and commits an invalid policy there (<c>version: [</c>); once the item has been reviewed (seeded pass, on
    /// the real policy), every policy read of the gate is redirected to that branch (<see cref="PolicyFromRef"/>), so the merge gate
    /// reads a really corrupted file through GitHub and blocks. The throwaway branch is deleted at the end; main is never touched.
    /// </summary>
    [Fact]
    public async Task A_corrupt_gate_policy_blocks_the_merge_and_escalates()
    {
        Harness.RequireOptIn();
        var owner = SandboxRepo.OwnerToken();
        await using var run = await StartAsync("FACTORY_E2E_GATE_POLICY_STORY", "df_e2e_p2at4_policy");
        var branch = $"e2e/corrupt-policy-{Guid.NewGuid():N}";
        await run.Sandbox.CreateBranchAsync(owner, branch, await run.Sandbox.BranchShaAsync(owner, "main", run.Ct), run.Ct);
        try
        {
            await run.Sandbox.PutFileAsync(owner, branch, GatePolicy.Path, "version: [\n", "acceptance: a corrupt gate policy (throwaway branch)", run.Ct);
            var reviewer = new SeededReviewer();
            PolicyFromRef? policies = null;

            var outcome = await FactoryRunAsync(run, gate =>
            {
                policies = new PolicyFromRef(gate.GitHub, branch, () => reviewer.Reviews > 0);
                return gate with { Reviewer = reviewer, GitHub = policies };
            });

            await AssertBlockedAtTheGateAsync(run, outcome, $"{GatePolicy.Path} is not valid YAML");
            Assert.True(policies!.Redirected > 0);
        }
        finally
        {
            await run.Sandbox.DeleteBranchAsync(owner, branch);
        }
    }

    /// <summary>
    /// AT4: a new test that already passes on the base is gate_rejected. Before the first review the test pushes
    /// <c>&lt;the sandbox's first test project&gt;/E2eAlreadyPassingTests.cs</c>, an xUnit test asserting nothing the change fixes, to
    /// the PR branch; the gate's <c>new-tests-fail-on-base</c> check runs it (sandboxed) on the base, where it passes, and rejects it
    /// by name.
    /// </summary>
    [Fact]
    public async Task A_new_test_that_already_passes_on_the_base_is_gate_rejected()
    {
        await using var run = await StartAsync("FACTORY_E2E_GATE_PASSING_TEST_STORY", "df_e2e_p2at4_tests");
        const string test = "DarkFactoryE2e.E2eAlreadyPassingTests.Already_passes_on_the_base";
        var reviewer = new SeededReviewer(beforeFirst: async (request, ct) =>
        {
            var token = await run.Sandbox.AppTokenAsync(ct);
            var branch = StoryId.BranchName(run.StoryId);
            var dir = await run.Sandbox.TestProjectDirAsync(token, branch, ct);
            await run.Sandbox.PutFileAsync(token, branch, $"{dir}/E2eAlreadyPassingTests.cs", """
                namespace DarkFactoryE2e;

                // [dark-factory e2e] seeded by the acceptance test: it passes on the base, so the gate must reject it.
                public class E2eAlreadyPassingTests
                {
                    [Xunit.Fact]
                    public void Already_passes_on_the_base() => Xunit.Assert.True(true);
                }

                """, "acceptance: add a test that already passes on the base", ct);
        });

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = reviewer });

        var (history, _) = await AssertBlockedAtTheGateAsync(run, outcome, $"{GateChecks.NewTestsFailOnBase} ({NewTestsOutcome.Rejected})");
        var newTests = history.Last(e => e.Step == RunPipeline.Steps.NewTests);
        Assert.Equal(StepOutcome.GateRejected, newTests.Outcome);
        var result = Assert.IsType<NewTestsResult>(NewTestsResult.FromDetail(newTests.Detail));
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        var seeded = Assert.Single(result.Tests, t => t.Test == test);
        Assert.Equal((TestCaseResult.Passed, TestCaseResult.Passed), (seeded.Base, seeded.Head));
        Assert.Contains(test, result.Reason);
    }

    /// <summary>
    /// The fix-round cap a PR whose base has <paramref name="policy"/> (<see cref="GatePolicy.Path"/>; null: none) runs under: the
    /// policy's <c>risk.max_fix_rounds</c>, never above the hard cap <see cref="Lifecycle.MaxFixRounds"/> (what the pipeline's
    /// <c>fix-cap</c> row records). The sandbox's policy sets 2.
    /// </summary>
    internal static int ExpectedFixCap(string? policy) =>
        policy is null ? Lifecycle.MaxFixRounds : Math.Min(GatePolicy.Parse(policy).Risk.MaxFixRounds, Lifecycle.MaxFixRounds);

    /// <summary>
    /// AT4: a fix round past the cap escalates. The seeded panel reports one confirmed blocking correctness finding on every head,
    /// so the real fixer runs as many rounds as the cap allows (each a real worker session) — the cap the base's policy sets
    /// (<see cref="ExpectedFixCap"/>, read from the sandbox here, and the cap the ledger's <c>fix-cap</c> row records) — and the review
    /// after the last one escalates instead of starting another, with the open finding attached; nothing reaches the merge gate.
    /// </summary>
    [Fact]
    public async Task A_fix_round_past_the_cap_escalates_with_the_open_finding()
    {
        await using var run = await StartAsync("FACTORY_E2E_GATE_ROUNDS_STORY", "df_e2e_p2at4_rounds");
        var reviewer = new SeededReviewer(blocking: true);
        IGateGitHub? github = null;

        var outcome = await FactoryRunAsync(run, gate =>
        {
            github = gate.GitHub;
            return gate with { Reviewer = reviewer };
        });

        Assert.Equal(WorkState.Escalated, outcome.State);
        var history = await run.E2e.HistoryAsync(run.StoryId, run.Ct);
        var pr = await LinkedPullAsync(run, history);
        var cap = ExpectedFixCap(await github!.GetPolicyAsync(run.Sandbox.Repo, pr.GetProperty("base").GetProperty("ref").GetString()!, run.Ct));
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.Equal(cap, RunPipeline.FixCapOf(history));
        var transitions = history.Where(e => e.Step is null).Select(e => e.State).ToList();
        Assert.Equal(cap, transitions.Count(s => s == WorkState.Fixing));
        Assert.Equal([WorkState.Review, WorkState.Escalated], transitions.TakeLast(2));
        Assert.DoesNotContain(WorkState.MergeGate, transitions);
        Assert.All(history.Where(e => e.Step is null && e.State == WorkState.Fixing), e => Assert.Equal(StepOutcome.Failed, e.Outcome));
        Assert.NotEmpty(RunPipeline.Verdicts(history));
        Assert.All(RunPipeline.Verdicts(history), v => Assert.False(v.Passed));
        Assert.Contains(await E2e.StoryCommentsAsync(run.StoryId, run.Ct),
            c => c.Contains($"after {cap} fix rounds", StringComparison.Ordinal) && c.Contains(SeededReviewer.FindingTitle, StringComparison.Ordinal));
    }
}
