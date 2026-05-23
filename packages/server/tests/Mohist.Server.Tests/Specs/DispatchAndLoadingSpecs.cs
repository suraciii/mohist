using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class DispatchAndLoadingSpecs : WorkflowGrainSpecs
{
    public DispatchAndLoadingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NoRunnerAtStart_RegisterLater_AssignAndRun()
    {
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.AssignWorkflowAsync(_workflowId!);

        var (task, rId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);

        await ReportAsync(rId, task.WorkId, "completed");
        var (check, checkRunnerId) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(checkRunnerId, check, "check-1");
    }

    [Fact]
    public async Task PausedBeforeRunner_StillPaused()
    {
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        await workflow.PauseAsync("paused before capacity");
        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.AssignWorkflowAsync(_workflowId!);

        Assert.Null(await runner.PollAsync());
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task StageWithDynamicTasks_LoadCompletes_DynamicTasksMaterializedBeforeChecks()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build", [], [new("check-1", "Check 1", "spec/check")], TasksFromUses: "spec/load")
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("load-build:", load.WorkId);
        Assert.Equal("load", load.WorkType);
        Assert.Equal("build", load.Stage);
        Assert.Equal("spec/load", load.Uses);

        await ReportAsync(r1, load.WorkId, new WorkDispatchResult("loaded", Output: """
        {
          "tasks": [
            { "id": "dynamic-1", "title": "Dynamic 1", "uses": "spec/task", "with": { "value": "one" } },
            { "taskId": "dynamic-2", "title": "Dynamic 2", "uses": "spec/task" }
          ]
        }
        """));

        var (dynamic1, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-1.", dynamic1.WorkId);
        Assert.Equal("spec/task", dynamic1.Uses);
        Assert.Contains("one", dynamic1.With);
        await ReportAsync(r2, dynamic1.WorkId, "completed");

        var (dynamic2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-2.", dynamic2.WorkId);
        await ReportAsync(r3, dynamic2.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r4, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task StageWithStaticAndDynamicTasks_LoadCompletes_StaticTasksRunBeforeDynamicTasks()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput(
                "build",
                [new("static-1", "Static 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")],
                TasksFromUses: "spec/load")
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, load.WorkId, new WorkDispatchResult("loaded", Output: """
        [{ "id": "dynamic-1", "title": "Dynamic 1", "uses": "spec/task" }]
        """));

        var (staticTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("static-1.", staticTask.WorkId);
        await ReportAsync(r2, staticTask.WorkId, "completed");

        var (dynamicTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-1.", dynamicTask.WorkId);
        await ReportAsync(r3, dynamicTask.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check, "check-1");
    }

    [Fact]
    public async Task StageWithDynamicTasks_LoadFails_WorkflowFails()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build", [], [new("check-1", "Check 1", "spec/check")], TasksFromUses: "spec/load")
        ]));

        var (load, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("load-build:", load.WorkId);

        await ReportAsync(runnerId, load.WorkId, "failed", "loader failed");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }
}
