using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class CheckRetrySpecs : WorkflowGrainSpecs
{
    public CheckRetrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static WorkflowDefinitionInput StageWithRetryCheck(int retryLimit = 2) =>
        new([
            new StageDefinitionInput("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check", RetryLimit: retryLimit,
                    RetryTask: new("fix-check", "Fix check", "spec/fix"))])
        ]);

    [Fact]
    public async Task CheckFails_RetryTaskRunsBeforeRecheck()
    {
        await StartWorkflowAsync(StageWithRetryCheck(retryLimit: 2));

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", check.WorkId);
        await ReportAsync(r2, check.WorkId, "fail", "needs fix");

        var (fixTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("fix-check:", fixTask.WorkId);
        Assert.Equal("spec/fix", fixTask.Uses);
        await ReportAsync(r3, fixTask.WorkId, "completed");

        var (recheck, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", recheck.WorkId);
        Assert.NotEqual(check.WorkId, recheck.WorkId);
        await ReportAsync(r4, recheck.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task CheckFailsRepeatedly_RetryTaskRunsEachTime()
    {
        await StartWorkflowAsync(StageWithRetryCheck(retryLimit: 3));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "fail", "first fail");

        var (fix1, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("fix-check:", fix1.WorkId);
        await ReportAsync(r3, fix1.WorkId, "completed");

        var (recheck1, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", recheck1.WorkId);
        await ReportAsync(r4, recheck1.WorkId, "fail", "second fail");

        var (fix2, r5) = await PollWorkAnyAsync();
        Assert.StartsWith("fix-check:", fix2.WorkId);
        Assert.NotEqual(fix1.WorkId, fix2.WorkId);
        await ReportAsync(r5, fix2.WorkId, "completed");

        var (recheck2, r6) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", recheck2.WorkId);
        await ReportAsync(r6, recheck2.WorkId, "pass");
    }

    [Fact]
    public async Task CheckFails_NoRetryConfigured_WorkflowFails()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "fail", "no retry");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }
}
