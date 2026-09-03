using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Workflow;

namespace Mohist.Server.L1Tests.Specs.Workflow.Grain;

[Trait("level", "L1")]
public class DispatchAndLoadingSpecs : WorkflowGrainSpecs
{
    public DispatchAndLoadingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NoRunnerAtStart_RegisterLater_AssignAndRun()
    {
        var workflow = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        _runnerId = await RegisterRunnerAsync();

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
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        await workflow.PauseAsync("paused before capacity");
        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);

        Assert.Null(await runner.PollAsync(Services));
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task DynamicTaskRegistration_DoesNotAbandonInFlightLoadTaskOnConcurrentPoll()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/load")],
                [new("check-1", "Check 1", "spec/check")])
        ]), maxWorkflowSlots: 2);

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        var load = await runner.PollAsync(Services);
        Assert.NotNull(load);
        Assert.StartsWith("load-tasks.", load.WorkId);

        var addResult = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("dynamic-1", "Dynamic 1", "spec/task")
            ]));
        Assert.Equal(1, addResult.AddedCount);

        var concurrentPoll = await runner.PollAsync(Services);
        if (concurrentPoll is not null)
        {
            Assert.Equal(load.WorkflowRunId, concurrentPoll.WorkflowRunId);
            Assert.Equal(load.WorkId, concurrentPoll.WorkId);
        }

        await ReportAsync(_runnerId!, _workflowId!, load.WorkId, new WorkResult("completed"));

        var dynamicTask = await runner.PollAsync(Services);
        Assert.NotNull(dynamicTask);
        Assert.Equal(_workflowId, dynamicTask.WorkflowRunId);
        Assert.StartsWith("dynamic-1.", dynamicTask.WorkId);
    }

    [Fact]
    public async Task StageWithStaticAndDynamicTasks_LoadTaskThenDynamicThenStaticBeforeChecks()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new("load-tasks", "Load tasks", "spec/load"), new("static-1", "Static 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (load, r1) = await PollWorkAnyAsync();

        await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("dynamic-1", "Dynamic 1", "spec/task")
            ]));

        await ReportAsync(r1, load.WorkId, "completed");

        var (dynamicTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-1.", dynamicTask.WorkId);
        await ReportAsync(r2, dynamicTask.WorkId, "completed");

        var (staticTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("static-1.", staticTask.WorkId);
        await ReportAsync(r3, staticTask.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check, "check-1");
    }
}
