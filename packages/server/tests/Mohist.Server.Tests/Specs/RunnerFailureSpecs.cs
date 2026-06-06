using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs;

public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerUnregistersWithInFlightWork_AssignmentIsPreserved()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetClaimedRunnerIdAsync());
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());

        var otherRunnerId = await RegisterRunnerAsync();
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        var assignment = await workflow.AssignRunnerAsync(otherRunnerId);

        Assert.Equal(WorkflowAssignmentStatus.Rejected, assignment.Status);
        Assert.Equal("already-assigned", assignment.Reason);
        Assert.Null(await otherRunner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RunnerUnregistersWithAssignment_AssignmentIsPreserved()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        await AssignWorkflowToRunnerAsync(_workflowId!, runnerId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetClaimedRunnerIdAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StoppedWorkflow_KeepsAssignment_AndRunnerDropsPendingWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        Assert.NotNull(await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync());

        await workflow.StopAsync("test-stop");

        Assert.Equal("Stopped", await workflow.GetRunStatusAsync());
        Assert.Equal(runnerId, await workflow.GetClaimedRunnerIdAsync());

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RegisteredButUnavailableLeaseOwner_RemainsRecoveryBlocked()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));
        var workflowId = _workflowId!;
        await SeedLeaseAsync(workflowId, new WorkLease(
            WorkId: "task-1.1",
            WorkType: "task",
            Stage: "build",
            LogicalId: "task-1",
            Title: "Task 1",
            RunnerId: "runner-blocked"));
        await workflow.AssignRunnerAsync("runner-blocked");

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject("test-project"));
        await registry.RegisterAsync(new RunnerInfo("runner-blocked", ["spec/*"], "test-host", "test-project"));

        await DeactivateWorkflowAsync(workflowId);

        var reactivated = Grains.GetGrain<IWorkflowGrain>(workflowId);
        var otherRunner = await reactivated.AssignRunnerAsync("runner-other");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, otherRunner.Status);
        Assert.Equal("already-assigned", otherRunner.Reason);
        Assert.Equal("runner-blocked", await reactivated.GetClaimedRunnerIdAsync());
        Assert.Equal("task-1.1", await reactivated.GetCurrentWorkIdAsync());
    }
}
