using Xunit;

namespace Mohist.Server.Tests.Specs;

public class FailureSpecs : WorkflowGrainSpecs
{
    public FailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunningTask_ReportsFailure_WorkflowFails()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "failed", "compile error");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task RunningCheck_ReportsFail_WorkflowFails()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "fail", "typecheck errors");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }
}
