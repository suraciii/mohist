using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowRecovery")]
public class WorkflowLeaseActivationSpecs : WorkflowGrainSpecs
{
    public WorkflowLeaseActivationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunningTask_SurvivesActivation_AndRestoresOwnerFields()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (task, runnerId) = await PollWorkAnyAsync();
        var workflowId = _workflowId!;

        await DeactivateWorkflowAsync(workflowId);
        await RegisterRunnerAsync(runnerId);

        workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        Assert.Equal(runnerId, await workflow.GetAssignedWorkerIdAsync());
        var snapshot = await GetQuerier().GetStatusAsync(workflowId);
        var runningTask = snapshot!.Stages.Single().Tasks.Single();
        Assert.Equal("running", runningTask.Status);
        var differentRunner = await workflow.AssignWorkerAsync("different-runner");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, differentRunner.Status);
        Assert.Equal("already-assigned", differentRunner.Reason);
    }
}
