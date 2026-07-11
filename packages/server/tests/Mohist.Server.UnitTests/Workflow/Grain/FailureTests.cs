using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.UnitTests.Workflow;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class FailureTests : WorkflowGrainTests
{
    public FailureTests(WorkflowGrainFixture fixture) : base(fixture) { }

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
