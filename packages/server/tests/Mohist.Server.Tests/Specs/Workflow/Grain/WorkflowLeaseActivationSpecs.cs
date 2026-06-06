using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class WorkflowLeaseActivationSpecs : WorkflowGrainSpecs
{
    public WorkflowLeaseActivationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task PersistedLease_SurvivesActivation_AndRestoresOwnerFields()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (task, runnerId) = await PollWorkAnyAsync();
        var workflowId = _workflowId!;

        await DeactivateWorkflowAsync(workflowId);

        workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        Assert.Equal(runnerId, await workflow.GetClaimedRunnerIdAsync());
        Assert.Equal(task.WorkId, await workflow.GetCurrentWorkIdAsync());
        var differentRunner = await workflow.AssignRunnerAsync("different-runner");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, differentRunner.Status);
        Assert.Equal("already-assigned", differentRunner.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task IncompletePersistedLease_AfterActivation_RemainsNonDispatchable()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));
        var workflowId = _workflowId!;
        await workflow.AssignRunnerAsync("runner-restored");

        await SeedLeaseAsync(workflowId, new WorkLease(
            WorkId: "",
            WorkType: "task",
            Stage: "build",
            LogicalId: "task-1",
            Title: "Task 1",
            RunnerId: "runner-restored"));

        await DeactivateWorkflowAsync(workflowId);

        workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        Assert.Equal("runner-restored", await workflow.GetClaimedRunnerIdAsync());
        Assert.Equal(string.Empty, await workflow.GetCurrentWorkIdAsync());
        var otherRunner = await workflow.AssignRunnerAsync("runner-other");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, otherRunner.Status);
        Assert.Equal("already-assigned", otherRunner.Reason);
        var sameRunner = await workflow.AssignRunnerAsync("runner-restored");
        Assert.Equal(WorkflowAssignmentStatus.Assigned, sameRunner.Status);

        var runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());
    }
}
