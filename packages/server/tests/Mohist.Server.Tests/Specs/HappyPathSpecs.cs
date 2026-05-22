using Xunit;

namespace Mohist.Server.Tests.Specs;

public class HappyPathSpecs : WorkflowGrainSpecs
{
    public HappyPathSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Given_Single_Stage_With_Task_And_Check_When_Both_Pass_Then_Workflow_Completes()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var initWork = await PollWorkAsync(runnerId);
        Assert.Equal("load", initWork!.WorkType);
        await ReportAsync(runnerId, initWork.WorkId, "completed");

        var taskWork = await PollWorkAsync(runnerId);
        Assert.Equal("task", taskWork!.WorkType);
        Assert.Equal("task-1", taskWork.Id);
        await ReportAsync(runnerId, taskWork.WorkId, "completed");

        var checkWork = await PollWorkAsync(runnerId);
        Assert.Equal("check", checkWork!.WorkType);
        Assert.Equal("check-1", checkWork.Name);
        await ReportAsync(runnerId, checkWork.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var available = await runner.IsAvailableAsync();
        Assert.True(available);
    }

    [Fact]
    public async Task Given_Two_Stages_When_All_Tasks_And_Checks_Pass_Then_Workflow_Completes()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(TwoStages());

        // Stage 1 (plan)
        var init1 = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init1.WorkId, "completed");

        var task1 = await PollWorkAsync(runnerId);
        Assert.Equal("draft", task1.Id);
        await ReportAsync(runnerId, task1.WorkId, "completed");

        var check1 = await PollWorkAsync(runnerId);
        Assert.Equal("plan-ok", check1.Name);
        await ReportAsync(runnerId, check1.WorkId, "pass");

        // Stage 2 (build)
        var init2 = await PollWorkAsync(runnerId);
        Assert.Equal("build", init2.Stage);
        await ReportAsync(runnerId, init2.WorkId, "completed");

        var task2 = await PollWorkAsync(runnerId);
        Assert.Equal("compile", task2.Id);
        await ReportAsync(runnerId, task2.WorkId, "completed");

        var check2 = await PollWorkAsync(runnerId);
        Assert.Equal("build-ok", check2.Name);
        await ReportAsync(runnerId, check2.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task Given_Multi_Task_Stage_When_All_Pass_Then_Checks_Run()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage(
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

        var init = await PollWorkAsync(runnerId);
        await ReportAsync(runnerId, init.WorkId, "completed");

        var t1 = await PollWorkAsync(runnerId);
        Assert.Equal("task-1", t1.Id);
        await ReportAsync(runnerId, t1.WorkId, "completed");

        var t2 = await PollWorkAsync(runnerId);
        Assert.Equal("task-2", t2.Id);
        await ReportAsync(runnerId, t2.WorkId, "completed");

        var t3 = await PollWorkAsync(runnerId);
        Assert.Equal("task-3", t3.Id);
        await ReportAsync(runnerId, t3.WorkId, "completed");

        var c1 = await PollWorkAsync(runnerId);
        Assert.Equal("check-1", c1.Name);
        await ReportAsync(runnerId, c1.WorkId, "pass");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }
}
