using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain2")]
public class HappyPathSpecs : WorkflowGrainSpecs
{
    public HappyPathSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task SingleStageTaskAndCheck_BothPass_WorkflowCompletes()
    {
        await StartWorkflowAsync(SingleStage());

        var (taskWork, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", taskWork.WorkId);
        Assert.Equal("task", taskWork.WorkType);
        Assert.Equal("build", taskWork.Stage);
        Assert.Equal("Task 1", taskWork.Title);
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var (checkWork, rid2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", checkWork.WorkId);
        Assert.Equal("checks", checkWork.WorkType);
        Assert.Equal("build", checkWork.Stage);
        await ReportChecksPassAsync(rid2, checkWork, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(rid2);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task TwoStages_AllTasksAndChecksPass_WorkflowCompletes()
    {
        await StartWorkflowAsync(TwoStages());

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task1.WorkId);
        await ReportAsync(r1, task1.WorkId, "completed");

        var (check1, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check1.WorkId);
        Assert.Equal("checks", check1.WorkType);
        await ReportChecksPassAsync(r2, check1, "plan-ok");

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", task2.WorkId);
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check2.WorkId);
        Assert.Equal("checks", check2.WorkType);
        await ReportChecksPassAsync(r4, check2, "build-ok");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
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
        Assert.StartsWith("checks-", c1.WorkId);
        Assert.Equal("checks", c1.WorkType);
        await ReportChecksPassAsync(r4, c1, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }
}
