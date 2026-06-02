using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RunnerBindingSpecs : WorkflowGrainSpecs
{
    public RunnerBindingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CapacityOneRunner_WithInFlightWork_DoesNotGetSecondWorkflow()
    {
        var runnerId = await RegisterRunnerAsync("shared-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-1";
        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-1");
        await wf1.StartAsync(SingleStage(checks: []), TestInput());
        await AssignWorkflowToRunnerAsync("wf-1", runnerId);

        _workflowId = "wf-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-2");
        await wf2.StartAsync(SingleStage(checks: []), TestInput());

        var work1 = await runner.PollAsync();
        Assert.NotNull(work1);
        Assert.Equal("wf-1", work1.WorkflowRunId);

        var work2 = await runner.PollAsync();
        Assert.Null(work2);
    }

    [Fact]
    public async Task CapacityTwoRunner_TwoWorkflows_BothGetInFlightWork()
    {
        var runnerId = await RegisterRunnerAsync("shared-runner-capacity-2", maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-capacity-1";
        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-capacity-1");
        await wf1.StartAsync(SingleStage(checks: []), TestInput());
        await AssignWorkflowToRunnerAsync("wf-capacity-1", runnerId);

        _workflowId = "wf-capacity-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-capacity-2");
        await wf2.StartAsync(SingleStage(checks: []), TestInput());

        var work1 = await runner.PollAsync();
        Assert.NotNull(work1);
        Assert.Equal("wf-capacity-1", work1.WorkflowRunId);

        var work2 = await runner.PollAsync();
        Assert.NotNull(work2);
        Assert.Equal("wf-capacity-2", work2.WorkflowRunId);

        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task TaskCompletes_NextTaskOnSameRunner()
    {
        await ClearBacklogAsync();
        var runnerId = await RegisterRunnerAsync("sticky-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-sticky";
        _runnerId = runnerId;

        var workflow = Grains.GetGrain<IWorkflowGrain>("wf-sticky");
        await workflow.StartAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks: []), TestInput());
        await AssignWorkflowToRunnerAsync("wf-sticky", runnerId);

        var first = await runner.PollAsync();
        Assert.NotNull(first);
        Assert.StartsWith("task-1.", first.WorkId);
        await workflow.ReportResultAsync(runnerId, first.WorkId, new WorkResult("completed"));

        var second = await runner.PollAsync();
        Assert.NotNull(second);
        Assert.StartsWith("task-2.", second.WorkId);
    }

    [Fact]
    public async Task TwoWorkflows_CompletingOneDoesNotAffectOther()
    {
        var runnerId = await RegisterRunnerAsync("report-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-report-1";
        _runnerId = runnerId;

        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-report-1");
        await wf1.StartAsync(SingleStage(checks: []), TestInput());
        await AssignWorkflowToRunnerAsync("wf-report-1", runnerId);

        _workflowId = "wf-report-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-report-2");
        await wf2.StartAsync(SingleStage(checks: []), TestInput());
        await AssignWorkflowToRunnerAsync("wf-report-2", runnerId);

        var work1 = await runner.PollAsync();
        Assert.NotNull(work1);
        Assert.Equal("wf-report-1", work1.WorkflowRunId);
        await wf1.ReportResultAsync(runnerId, work1.WorkId, new WorkResult("completed"));

        var nextPoll = await runner.PollAsync();
        Assert.NotNull(nextPoll);
        Assert.Equal("wf-report-2", nextPoll.WorkflowRunId);
        Assert.StartsWith("task-1.", nextPoll.WorkId);
    }

    [Fact]
    public async Task Heartbeat_WhenRegistryEntryMissing_ReRegistersRunnerPresence()
    {
        var projectId = $"heartbeat-repair-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"heartbeat-repair-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(projectId));

        await registry.UnregisterAsync(runnerId);
        Assert.DoesNotContain(runnerId, await registry.ListRunnerIdsAsync());

        await runner.HeartbeatAsync();

        Assert.Contains(runnerId, await registry.ListRunnerIdsAsync());
    }

    [Fact]
    public async Task Poll_WhenRegistryEntryMissing_ReRegistersRunnerPresence()
    {
        var projectId = $"poll-repair-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"poll-repair-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(projectId));

        await registry.UnregisterAsync(runnerId);
        Assert.DoesNotContain(runnerId, await registry.ListRunnerIdsAsync());

        var work = await runner.PollAsync();

        Assert.Null(work);
        Assert.Contains(runnerId, await registry.ListRunnerIdsAsync());
    }

    [Fact]
    public async Task Register_WhenRunnerScopeChanges_RemovesStaleRegistryPresence()
    {
        var projectId = $"scope-change-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"scope-change-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var projectRegistry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject(projectId));
        var globalRegistry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

        Assert.Contains(runnerId, await projectRegistry.ListRunnerIdsAsync());

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        Assert.DoesNotContain(runnerId, await projectRegistry.ListRunnerIdsAsync());
        Assert.Contains(runnerId, await globalRegistry.ListRunnerIdsAsync());
    }
}
