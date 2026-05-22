using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class PausingWorkSpecs : WorkflowGrainSpecs
{
    public PausingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunningWorkflow_PauseRequested_WorkflowPausesBeforeNextTask()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks:
            [
                new("check-1", "Check 1", "spec/check")
            ]));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.Equal("task-1", task1.WorkId);
        await workflow.PauseAsync("user requested");
        await ReportAsync(r1, task1.WorkId, "completed");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task PausedWorkflow_Resumed_WorkflowContinuesFromPendingWork()
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks:
            [
                new("check-1", "Check 1", "spec/check")
            ]));

        var (task1, r1) = await PollWorkAnyAsync();
        await workflow.PauseAsync("pause");
        await ReportAsync(r1, task1.WorkId, "completed");

        var runnerId2 = await RegisterRunnerAsync();
        await workflow.ResumeAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.Equal("task-2", task2.WorkId);
        await ReportAsync(r2, task2.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, check.WorkId, "pass");

        var runner2 = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.True(await runner2.IsAvailableAsync());
    }
}
