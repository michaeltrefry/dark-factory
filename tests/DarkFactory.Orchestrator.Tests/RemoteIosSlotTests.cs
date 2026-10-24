using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RemoteIosSlotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hosted_groups_leave_the_slot_free_while_local_groups_hold_it_for_their_runs(bool ios)
    {
        var root = Directory.CreateTempSubdirectory("df-ios-slot-").FullName;
        try
        {
            var workerLock = WorkerLock.For(root);
            var dispatch = new SlotDispatch(workerLock, WorkerLock.For(root));
            var h = new GatePipelineTests.Harness();
            var source = RemoteIosGateTests.Sources(ios);
            h.TestRunner.BaseFiles.Clear();
            foreach (var pair in source.BaseFiles) h.TestRunner.BaseFiles.Add(pair.Key, pair.Value);
            h.TestRunner.BaseCommit = RemoteIosGateTests.Base;
            h.TestRunner.HeadFiles = source.HeadFiles;
            var localHeld = new List<bool>();
            h.TestRunner.Answer = spec =>
            {
                localHeld.Add(workerLock.IsHeld);
                return FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? "passed" : "failed");
            };
            h.GitHub.Head = RemoteIosGateTests.Head;
            h.GitHub.CurrentBaseSha = RemoteIosGateTests.Base;
            h.GitHub.ExpectedPolicySha = RemoteIosGateTests.Base;
            var pipeline = new RunPipeline(h.Stories, h.Ledger, h.Locks, h.Workspaces,
                new RunPipelineTests.FakeWorker(GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)),
                h.Prs, RunPipelineTests.Sandbox, TextWriter.Null,
                controls: h.Controls, slots: TestSlots.Of(workerLock), swift: true,
                gate: new GateStage(h.GitHub, h.Reviewer, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), null,
                    h.TestRunner, null, FakeCodeGraphRisk.Reader(h.CodeGraph), null,
                    null, new RemoteIosNewTests(dispatch, gui: GuiRemoteGateTests.Settings) { PollInterval = TimeSpan.Zero }));
            var outcome = await pipeline.RunAsync(77, CancellationToken.None);
            var rows = await h.Rows();
            // Mutations: slot-hosted-enclosing-phase (ios), slot-local-phase-bypass (macOS).
            // The separate lock descriptor proves an unrelated worker can acquire the slot during hosted dispatch.
            Assert.Equal((true, ios ? 2 : 0, true, ios ? 0 : 2, true, ios ? 0 : 1),
                (outcome.Succeeded, dispatch.Probes.Count, dispatch.Probes.All(p => p), localHeld.Count,
                    localHeld.All(p => p), rows.Count(r => r.Step == RunPipeline.Steps.SlotHeld && r.Detail!.EndsWith("run the new tests", StringComparison.Ordinal))));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SlotDispatch(WorkerLock own, WorkerLock competitor) : IIosWorkflowGitHub
    {
        private readonly RemoteIosGateTests.Dispatch _inner = new() { Gui = true };
        public List<bool> Probes { get; } = [];
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct) => _inner.ReadDefaultWorkflowAsync(repo, ct);
        public async Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            var held = own.IsHeld;
            var available = await competitor.TryHoldAsync(() => Task.CompletedTask);
            Probes.Add(!held && available);
            await _inner.DispatchAsync(repo, workflow, request, correlation, ct);
        }
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct) => _inner.CancelAsync(repo, run, ct);
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct) => _inner.RunsAsync(repo, ct);
        public Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct) => _inner.ResultAsync(repo, run, correlation, ct);
    }
}
