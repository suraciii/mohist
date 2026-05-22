using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RetryAndRerunSpecs : WorkflowGrainSpecs
{
    public RetryAndRerunSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Given_Failed_Task_When_Retry_Then_Failed_Task_Is_Reset_And_Re_Executed()
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
        Assert.Equal("task-1", retriedTask.Id);
        Assert.Equal("task", retriedTask.WorkType);

        await ReportAsync(runnerId, retriedTask.WorkId, "completed");

        var check = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, check.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task Given_Failed_Check_When_Retry_Then_Failed_Checks_Are_Reset_And_Re_Executed()
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
        Assert.Equal("check-1", retriedCheck.Name);
        Assert.Equal("check", retriedCheck.WorkType);

        await ReportAsync(runnerId, retriedCheck.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task Given_Failed_Stage_When_Rerun_Then_Stage_Is_Re_Initialized_From_Scratch()
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
    public async Task Given_Passed_Stage_When_Rerun_Then_Stage_Is_Re_Initialized()
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
    public async Task Given_Non_Failed_Workflow_When_Retry_Then_Throws()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        await Assert.ThrowsAsync<Exception>(async () => await workflow.RetryAsync());
    }
}
