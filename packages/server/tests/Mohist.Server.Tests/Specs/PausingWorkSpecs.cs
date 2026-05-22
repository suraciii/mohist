using Xunit;

namespace Mohist.Server.Tests.Specs;

public class PausingWorkSpecs : WorkflowGrainSpecs
{
    public PausingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Given_Running_Workflow_When_Pause_Requested_Then_Workflow_Pauses_Before_Next_Task()
    {
        var runnerId = await RegisterRunnerAsync();
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

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task1 = await PollWorkAsync(runnerId);
        Assert.Equal("task-1", task1.Id);
        await ReportAsync(runnerId, task1.WorkId, "completed");

        await workflow.PauseAsync("user requested");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task Given_Paused_Workflow_When_Resumed_Then_Workflow_Continues_From_Pending_Work()
    {
        var runnerId = await RegisterRunnerAsync();
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

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var task1 = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, task1.WorkId, "completed");

        await workflow.PauseAsync("pause");

        var runnerId2 = await RegisterRunnerAsync();
        await workflow.ResumeAsync();

        var task2 = await PollWorkAsync(runnerId2);
        Assert.Equal("task-2", task2.Id);
        await ReportAsync(runnerId2, task2.WorkId, "completed");

        var check = await PollWorkAsync(runnerId2);
        await ReportAsync(runnerId2, check.WorkId, "pass");

        var runner2 = Grains.GetGrain<IRunnerGrain>(runnerId2);
        Assert.True(await runner2.IsAvailableAsync());
    }
}
