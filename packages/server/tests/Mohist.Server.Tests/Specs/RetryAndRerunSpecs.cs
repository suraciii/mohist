using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Errors;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RetryAndRerunSpecs : WorkflowGrainSpecs
{
    public RetryAndRerunSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task TaskFails_Retry_TaskRunsAgain()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var (retriedTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", retriedTask.WorkId);

        await ReportAsync(r2, retriedTask.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check.WorkId, "pass");
    }

    [Fact]
    public async Task CheckFails_Retry_CheckRunsAgain()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "fail", "broken");

        await workflow.RetryAsync();

        var (retriedCheck, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", retriedCheck.WorkId);

        await ReportAsync(r3, retriedCheck.WorkId, "pass");
    }

    [Fact]
    public async Task StageFails_Rerun_StageStartsFromScratch()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RerunAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task2.WorkId);
        await ReportAsync(r2, task2.WorkId, "completed");

        var (check2, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check2.WorkId, "pass");
    }

    [Fact]
    public async Task StagePasses_Rerun_StageStartsOver()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pass");

        await workflow.RerunAsync();

        var (task2, _) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task2.WorkId);
    }

    [Fact]
    public async Task RunningWorkflow_Retry_Error()
    {
        await StartWorkflowAsync(SingleStage());

        await Assert.ThrowsAsync<WorkflowDomainException>(async () =>
            await Grains.GetGrain<IWorkflowGrain>(_workflowId!).RetryAsync());
    }
}
