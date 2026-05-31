using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowLeaseActivationSpecs : WorkflowGrainSpecs
{
    public WorkflowLeaseActivationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PersistedLease_SurvivesActivation_AndRestoresOwnerFields()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (task, runnerId) = await PollWorkAnyAsync();
        var workflowId = _workflowId!;

        await DeactivateWorkflowAsync(workflowId);

        workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        Assert.Equal(runnerId, await workflow.GetAssignedRunnerIdAsync());
        Assert.Equal(task.WorkId, await workflow.GetAssignedWorkIdAsync());
        Assert.Null(await workflow.GetWorkAsync("different-runner"));
    }

    [Fact]
    public async Task IncompletePersistedLease_AfterActivation_RemainsNonDispatchable()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));
        var workflowId = _workflowId!;

        await SeedLeaseAsync(workflowId, new WorkLease(
            WorkId: "",
            WorkType: "task",
            Stage: "build",
            LogicalId: "task-1",
            Title: "Task 1",
            RunnerId: "runner-restored"));

        await DeactivateWorkflowAsync(workflowId);

        workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        Assert.Equal("runner-restored", await workflow.GetAssignedRunnerIdAsync());
        Assert.Equal(string.Empty, await workflow.GetAssignedWorkIdAsync());
        Assert.Null(await workflow.GetWorkAsync("runner-other"));

        var runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());
    }
}
