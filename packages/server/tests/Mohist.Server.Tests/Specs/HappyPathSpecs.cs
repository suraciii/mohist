using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class HappyPathSpecs : WorkflowGrainSpecs
{
    public HappyPathSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task SingleStageTaskAndCheck_BothPass_WorkflowCompletes()
    {
        await StartWorkflowAsync(SingleStage());

        var (taskWork, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", taskWork.WorkId);
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, rid2) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", checkWork.WorkId);
        await ReportAsync(rid2, checkWork.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(rid2);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task TwoStages_AllTasksAndChecksPass_WorkflowCompletes()
    {
        await StartWorkflowAsync(TwoStages());

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task1.WorkId);
        await ReportAsync(r1, task1.WorkId, "completed");

        var (check1, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("plan-ok:", check1.WorkId);
        await ReportAsync(r2, check1.WorkId, "pass");

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", task2.WorkId);
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("build-ok:", check2.WorkId);
        await ReportAsync(r4, check2.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task MultiTaskStage_AllTasksPass_CheckRunsAndCompletes()
    {
        await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task"),
                new("task-3", "Task 3", "spec/task")
            ],
            checks:
            [
                new("check-1", "Check 1", "spec/check")
            ]));

        var (t1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", t1.WorkId);
        await ReportAsync(r1, t1.WorkId, "completed");

        var (t2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", t2.WorkId);
        await ReportAsync(r2, t2.WorkId, "completed");

        var (t3, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("task-3.", t3.WorkId);
        await ReportAsync(r3, t3.WorkId, "completed");

        var (c1, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("check-1:", c1.WorkId);
        await ReportAsync(r4, c1.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }
}
