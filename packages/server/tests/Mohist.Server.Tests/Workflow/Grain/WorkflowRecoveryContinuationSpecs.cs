using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Tests.Workflow.Grain;

[Collection("ComponentGrain")]
[Trait("level", "L0")]
public sealed class WorkflowRecoveryContinuationSpecs
{
    private readonly ComponentWorkflowGrainFixture _orleansFixture;

    public WorkflowRecoveryContinuationSpecs(ComponentWorkflowGrainFixture fixture)
    {
        _orleansFixture = fixture;
    }

    [Fact]
    public async Task AcceptedReport_CommitsClaimableContinuationContract()
    {
        var workflow = _orleansFixture.Grains.GetGrain<IWorkflowGrain>(ComponentWorkflowGrainFixture.RecoveryWorkflowId);
        var freshWorkId = _orleansFixture.RecoveryFreshWorkId;

        var acknowledgement = await workflow.ReceiveTaskReportAsync(
            ComponentWorkflowGrainFixture.RecoveryRunnerId,
            freshWorkId,
            new TaskReport(
                freshWorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                Detail: null,
                ActionAttemptId: freshWorkId,
                AddTasks: new List<RuntimeTaskInput>
                {
                    ComponentWorkflowGrainFixture.RecoveryFollowUp()
                }));

        Assert.Equal(WorkReportVerdict.Accepted, acknowledgement);

        var continuation = Assert.IsType<WorkItem>(await workflow.ClaimNextAsync(
            ComponentWorkflowGrainFixture.RecoveryRunnerId,
            "test-generation"));
        Assert.NotEqual(freshWorkId, continuation.Id);
        Assert.Equal(1, continuation.RecoveryRemaining);
        Assert.Equal("${{ vars.agent }}", continuation.With!["options"]!.Value.GetString());
        Assert.Equal(
            "${{ vars.marker }}",
            continuation.Expect!["markers"]!.Value[0].GetProperty("failIf").GetString());
    }
}
