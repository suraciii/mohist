using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Errors;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RetryAndRerunSpecs : WorkflowGrainSpecs
{
    public RetryAndRerunSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FailedTask_Retry_TaskResetAndReExecuted()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var (retriedTask, r2) = await PollWorkAnyAsync();
        Assert.Equal("task-1", retriedTask.WorkId);
        Assert.Equal("task", retriedTask.WorkType);

        await ReportAsync(r2, retriedTask.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task FailedCheck_Retry_CheckResetAndReExecuted()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "fail", "broken");

        await workflow.RetryAsync();

        var (retriedCheck, r3) = await PollWorkAnyAsync();
        Assert.Equal("check", retriedCheck.WorkType);

        await ReportAsync(r3, retriedCheck.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task FailedStage_Rerun_StageReInitializedFromScratch()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RerunAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.Equal("task", task2.WorkType);
        Assert.Equal("task-1", task2.WorkId);
        await ReportAsync(r2, task2.WorkId, "completed");

        var (check2, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check2.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task PassedStage_Rerun_StageReInitialized()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pass");

        await workflow.RerunAsync();

        var (task2, _) = await PollWorkAnyAsync();
        Assert.Equal("task", task2.WorkType);
    }

    [Fact]
    public async Task NonFailedWorkflow_Retry_Throws()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        await Assert.ThrowsAsync<WorkflowDomainException>(async () => await workflow.RetryAsync());
    }
}
