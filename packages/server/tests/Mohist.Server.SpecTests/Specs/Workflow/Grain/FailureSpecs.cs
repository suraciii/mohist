using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
public class FailureSpecs : WorkflowGrainSpecs
{
    public FailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunningTask_ReportsFailure_WorkflowFails()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "compile error");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task RunningCheck_ReportsFail_WorkflowFails()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, check, "check-1", "typecheck errors");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }
}
