using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Errors;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RetryRerunSpecs : WorkflowGrainSpecs
{
    public RetryRerunSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task TaskFails_Retry_RunnerGetsNewAttempt()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.1", task.WorkId);
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var (retriedTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.2", retriedTask.WorkId);
        Assert.Equal(r1, r2);

        await ReportAsync(r2, retriedTask.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check.WorkId, "pass");
    }

    [Fact]
    public async Task CheckFails_Retry_RunnerGetsNewCheckRun()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", check.WorkId);
        await ReportAsync(r2, check.WorkId, "fail", "broken");

        await workflow.RetryAsync();

        var (retriedCheck, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", retriedCheck.WorkId);
        Assert.Equal(r2, r3);

        await ReportAsync(r3, retriedCheck.WorkId, "pass");
    }

    [Fact]
    public async Task StageFails_Rerun_RunnerGetsNewStageRun()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RerunAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task2.WorkId);
        Assert.Equal(r1, r2);

        await ReportAsync(r2, task2.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check.WorkId, "pass");
    }

    [Fact]
    public async Task StagePasses_Rerun_RunnerGetsNewStageRun()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pass");

        await workflow.RerunAsync();

        var (task2, r2b) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task2.WorkId);
        Assert.Equal(r2, r2b);
    }

    [Fact]
    public async Task RunningWorkflow_Retry_Error()
    {
        await StartWorkflowAsync(SingleStage());

        await Assert.ThrowsAsync<WorkflowDomainException>(async () =>
        {
            var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
            await workflow.RetryAsync();
        });
    }

    [Fact]
    public async Task FailedWorkflow_Retry_RunnerGetsNewWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());

        await workflow.RetryAsync();

        var (retried, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", retried.WorkId);
        Assert.Equal(r1, r2);

        await ReportAsync(r2, retried.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check.WorkId, "pass");
    }

    [Fact]
    public async Task MultipleRetries_TaskAttemptNumberIncreases()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.1", task1.WorkId);
        await ReportAsync(r1, task1.WorkId, "failed", "first fail");

        await workflow.RetryAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.2", task2.WorkId);
        await ReportAsync(r2, task2.WorkId, "failed", "second fail");

        await workflow.RetryAsync();

        var (task3, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.3", task3.WorkId);
        await ReportAsync(r3, task3.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        await ReportAsync(r4, check.WorkId, "pass");
    }
}
