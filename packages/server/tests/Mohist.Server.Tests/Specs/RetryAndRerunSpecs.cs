using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RetryAndRerunSpecs : WorkflowGrainSpecs
{
    public RetryAndRerunSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FailedTask_Retry_TaskResetAndReExecuted()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var retriedTask = await PollWorkAsync(runnerId);
        Assert.Equal("task-1", retriedTask.WorkId);
        Assert.Equal("task", retriedTask.WorkType);

        await ReportAsync(runnerId, retriedTask.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task FailedCheck_Retry_CheckResetAndReExecuted()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "fail", "broken");

        await workflow.RetryAsync();

        var retriedCheck = await PollWorkAsync(runnerId);
        Assert.Equal("check-1", retriedCheck.WorkType);
        Assert.Equal("check", retriedCheck.WorkType);

        await ReportAsync(runnerId, retriedCheck.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task FailedStage_Rerun_StageReInitializedFromScratch()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "failed", "boom");

        await workflow.RerunAsync();

        var init2 = await PollWorkAsync(runnerId);
        Assert.Equal("load", init2.WorkType);
        await ReportAsync(runnerId, init2.WorkId, "completed");

        var task2 = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task2.WorkId, "completed");

        var check2 = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check2.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task PassedStage_Rerun_StageReInitialized()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "pass");

        await workflow.RerunAsync();

        var init2 = await PollWorkAsync(runnerId);
        Assert.Equal("load", init2.WorkType);
    }

    [Fact]
    public async Task NonFailedWorkflow_Retry_Throws()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        await Assert.ThrowsAsync<Exception>(async () => await workflow.RetryAsync());
    }
}
