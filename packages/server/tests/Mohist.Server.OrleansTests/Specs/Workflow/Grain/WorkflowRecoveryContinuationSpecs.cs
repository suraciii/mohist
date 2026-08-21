using Mohist.Server.OrleansTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.OrleansTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public sealed class WorkflowRecoveryContinuationSpecs : WorkflowGrainSpecs
{
    private readonly OrleansL0WorkflowGrainFixture _orleansFixture;

    public WorkflowRecoveryContinuationSpecs(OrleansL0WorkflowGrainFixture fixture) : base(fixture)
    {
        _orleansFixture = fixture;
    }

    [Fact]
    public async Task AcceptedReport_CommitsClaimableContinuationContract()
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(OrleansL0WorkflowGrainFixture.RecoveryWorkflowId);
        var freshWorkId = _orleansFixture.RecoveryFreshWorkId;

        var acknowledgement = await workflow.ReceiveTaskReportAsync(
            OrleansL0WorkflowGrainFixture.RecoveryRunnerId,
            freshWorkId,
            new TaskReport(
                freshWorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                Detail: null,
                TaskRunId: freshWorkId,
                AddTasks: new List<RuntimeTaskInput>
                {
                    OrleansL0WorkflowGrainFixture.RecoveryFollowUp()
                }));

        Assert.Equal(ReportAck.Accepted, acknowledgement);

        var continuation = Assert.IsType<WorkItem>(await workflow.ClaimNextAsync(
            OrleansL0WorkflowGrainFixture.RecoveryRunnerId));
        Assert.NotEqual(freshWorkId, continuation.Id);
        Assert.Equal(1, continuation.RecoveryRemaining);
        Assert.Equal("${{ vars.agent }}", continuation.With!["options"]!.Value.GetString());
        Assert.Equal(
            "${{ vars.marker }}",
            continuation.Expect!["markers"]!.Value[0].GetProperty("failIf").GetString());
    }
}
