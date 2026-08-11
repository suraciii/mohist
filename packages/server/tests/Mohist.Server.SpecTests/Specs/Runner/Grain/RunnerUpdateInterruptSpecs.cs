using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public sealed class RunnerUpdateInterruptSpecs : Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainSpecs
{
    public RunnerUpdateInterruptSpecs(Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainFixture fixture)
        : base(fixture) { }

    [Fact]
    public async Task BeginUpdateInterrupt_PreservesActiveWorkAndClosesAdmissionIdempotently()
    {
        await ClearBacklogAsync();
        var projectId = "runner-update-interrupt-project-567";
        var runnerId = await RegisterRunnerForProjectAsync(
            projectId,
            "runner-update-interrupt-567",
            maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var activeJobId = "runner-update-interrupt-active-567";
        var pendingJobId = "runner-update-interrupt-pending-567";
        var activeJob = Grains.GetGrain<IAgentJobGrain>(activeJobId);
        var pendingJob = Grains.GetGrain<IAgentJobGrain>(pendingJobId);

        try
        {
            await activeJob.SubmitAsync(new AgentJobInput(
                "run until interrupted",
                WorkspacePath: "spec-workspace",
                ProjectId: projectId,
                AgentId: "agent-test",
                PinnedRunnerId: runnerId));
            var claim = await runner.TryClaimAgentJobAsync(activeJobId, projectId);
            Assert.NotNull(claim);
            Assert.Equal(AgentJobStatus.Running, await activeJob.GetStatusAsync());

            await pendingJob.SubmitAsync(new AgentJobInput(
                "wait for admission",
                WorkspacePath: "spec-workspace",
                ProjectId: projectId,
                AgentId: "agent-test",
                PinnedRunnerId: runnerId));
            Assert.Equal(AgentJobStatus.Pending, await pendingJob.GetStatusAsync());

            var first = await runner.BeginUpdateInterruptAsync();

            Assert.NotNull(first);
            Assert.Equal(RunnerStatus.Online, first!.Status);
            Assert.True(first.Draining);
            var active = Assert.Single(first.ActiveWorks);
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, active.OwnerKind);
            Assert.Equal(activeJobId, active.OwnerId);
            Assert.Equal(claim!.WorkId, active.WorkId);
            Assert.Equal(AgentJobStatus.Running, await activeJob.GetStatusAsync());

            var repeated = await runner.BeginUpdateInterruptAsync();

            Assert.NotNull(repeated);
            Assert.True(repeated!.Draining);
            Assert.Equal(first.Status, repeated.Status);
            Assert.Equal(first.LastHeartbeatAt, repeated.LastHeartbeatAt);
            Assert.Equal(
                first.ActiveWorks.Select(work => (work.OwnerKind, work.OwnerId, work.WorkId)),
                repeated.ActiveWorks.Select(work => (work.OwnerKind, work.OwnerId, work.WorkId)));

            Assert.False((await runner.TryBeginPollAsync()).Admitted);
            Assert.Null(await runner.TryClaimAgentJobAsync(pendingJobId, projectId));
            Assert.Equal(AgentJobStatus.Pending, await pendingJob.GetStatusAsync());
            Assert.Equal(AgentJobStatus.Running, await activeJob.GetStatusAsync());
        }
        finally
        {
            await pendingJob.CancelAsync();
            await activeJob.CancelAsync();
            await runner.UnregisterAsync();
        }
    }
}
