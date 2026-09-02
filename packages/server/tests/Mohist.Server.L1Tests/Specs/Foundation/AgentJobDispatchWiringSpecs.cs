using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Workflow;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Foundation;

[Collection("WorkflowExecution")]
public sealed class AgentJobDispatchWiringSpecs : WorkflowGrainSpecs
{
    public AgentJobDispatchWiringSpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task MohistWorkflowDispatchesAgentWorkAsAgentJob()
    {
        await StartWorkflowAsync(Mohist.Server.Workflow.Services.WorkflowProfileCatalog.Definition);

        var (prepare, prepareRunner) = await PollWorkAnyAsync();
        Assert.Equal("task", prepare.WorkType);
        Assert.Equal("plan", prepare.Stage);
        Assert.Equal("mohist/workspace-prepare", prepare.Uses);
        await ReportAsync(prepareRunner, prepare.WorkId, "completed");

        var (plan, _) = await PollWorkAnyAsync();

        Assert.Equal("agent-job", plan.WorkType);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, plan.OwnerKind);
        Assert.Equal("plan", plan.Stage);
        Assert.Null(plan.Uses);
        Assert.NotNull(plan.AgentJobId);
        Assert.NotNull(plan.AgentSessionId);
        Assert.StartsWith("plan.", plan.ActionAttemptId);
        Assert.Contains("\"prompt\"", plan.With);
        Assert.Contains("PLANS/PLAN.md", plan.Expect!);
        Assert.Contains("PLANS/DESIGN.md", plan.Expect!);
        Assert.Contains("PLANS/tasks.json", plan.Expect!);
    }
}
