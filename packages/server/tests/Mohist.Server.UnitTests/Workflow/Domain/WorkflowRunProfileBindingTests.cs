using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public sealed class WorkflowRunProfileBindingTests
{
    [Fact]
    public void CreateFromStructure_PersistsProfileIdAndStartupTopology()
    {
        var run = WorkflowRun.Create(
            "run-1",
            new WorkflowStructure(
                "delivery/review",
                [new StageStructure("plan", false), new StageStructure("implement", true)]),
            DateTimeOffset.UnixEpoch);

        Assert.Equal("delivery/review", run.WorkflowProfileId);
        Assert.Equal(["plan", "implement"], run.Stages.Select(stage => stage.Id));
    }

    [Fact]
    public void DefinitionResolutionFailure_IsVisibleAndRetainsStartupFacts()
    {
        var run = WorkflowRun.Create(
            "run-1",
            new WorkflowStructure("delivery/review", [new StageStructure("implement", false)]),
            DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);

        var events = run.FailDefinitionResolution("Workflow 'run-1' has no definition for stage 'implement'");

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("delivery/review", run.WorkflowProfileId);
        Assert.Equal("implement", run.Stages.Single().Id);
        Assert.Equal(FailureReason.DefinitionResolutionFailed, run.Failure?.Reason);
        Assert.Single(events);
    }
}
