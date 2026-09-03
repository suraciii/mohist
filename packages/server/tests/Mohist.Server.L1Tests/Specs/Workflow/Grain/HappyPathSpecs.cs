using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Workflow;

namespace Mohist.Server.L1Tests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
[Trait("level", "L1")]
public class HappyPathSpecs : WorkflowGrainSpecs
{
    public HappyPathSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task SingleStageTaskAndCheck_BothPass_WorkflowCompletes()
    {
        await StartWorkflowAsync(SingleStage());

        var (taskWork, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", taskWork.WorkId);
        Assert.Equal("task", taskWork.WorkType);
        Assert.Equal("build", taskWork.Stage);
        Assert.Equal("Task 1", taskWork.Title);
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, rid2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", checkWork.WorkId);
        Assert.Equal("checks", checkWork.WorkType);
        Assert.Equal("build", checkWork.Stage);
        await ReportChecksPassAsync(rid2, checkWork, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(rid2);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

}
