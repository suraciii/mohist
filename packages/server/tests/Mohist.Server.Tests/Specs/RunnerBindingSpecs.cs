using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RunnerBindingSpecs : WorkflowGrainSpecs
{
    public RunnerBindingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task OneRunner_TwoWorkflows_BothGetWork()
    {
        var runnerId = await RegisterRunnerAsync("shared-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-1";
        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-1");
        await runner.AssignWorkflowAsync("wf-1");
        await wf1.StartAsync(SingleStage(checks: []));

        _workflowId = "wf-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-2");
        await runner.AssignWorkflowAsync("wf-2");
        await wf2.StartAsync(SingleStage(checks: []));

        var work1 = await runner.PollAsync();
        Assert.NotNull(work1);
        Assert.Equal("wf-1", work1.WorkflowRunId);

        var work2 = await runner.PollAsync();
        Assert.NotNull(work2);
        Assert.Equal("wf-2", work2.WorkflowRunId);
    }

    [Fact]
    public async Task TaskCompletes_NextTaskOnSameRunner()
    {
        var runnerId = await RegisterRunnerAsync("sticky-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-sticky";
        _runnerId = runnerId;

        var workflow = Grains.GetGrain<IWorkflowGrain>("wf-sticky");
        await runner.AssignWorkflowAsync("wf-sticky");
        await workflow.StartAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks: []));

        var first = await runner.PollAsync();
        Assert.NotNull(first);
        Assert.StartsWith("task-1.", first.WorkId);
        await runner.ReportAsync(first.WorkId, new WorkDispatchResult("completed"));

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
        await runner.AssignWorkflowAsync("wf-report-1");
        await wf1.StartAsync(SingleStage(checks: []));

        _workflowId = "wf-report-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-report-2");
        await runner.AssignWorkflowAsync("wf-report-2");
        await wf2.StartAsync(SingleStage(checks: []));

        var work1 = await runner.PollAsync();
        Assert.NotNull(work1);
        Assert.Equal("wf-report-1", work1.WorkflowRunId);
        await runner.ReportAsync(work1.WorkId, new WorkDispatchResult("completed"));

        var nextPoll = await runner.PollAsync();
        Assert.NotNull(nextPoll);
        Assert.Equal("wf-report-2", nextPoll.WorkflowRunId);
        Assert.StartsWith("task-1.", nextPoll.WorkId);
    }
}
