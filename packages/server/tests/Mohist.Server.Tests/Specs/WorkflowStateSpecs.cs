using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowStateSpecs : WorkflowGrainSpecs
{
    public WorkflowStateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FailedWorkflow_NoMoreWork()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task CompletedWorkflow_NoMoreWork()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task RejectedWorkflow_NoMoreWork()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RejectAsync("bad");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task TaskRunning_SecondPollWaitsForCompletion()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());

        await ReportAsync(r1, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r2, check, "check-1");
    }

    [Fact]
    public async Task StaleReport_IgnoredWorkflowContinues()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        await workflow.ReportResultAsync(task.WorkId, new WorkDispatchResult("failed", "stale"));

        await ReportChecksPassAsync(r2, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task SameRunnerAssignedTwice_WorkflowStillRuns()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        await runner.AssignWorkflowAsync(workflowId);
        await workflow.AssignRunnerAsync(runnerId);
        await workflow.StartAsync(SingleStage(checks: []));

        var work = await runner.PollAsync();
        Assert.NotNull(work);
    }

    [Fact]
    public async Task StartWithoutRunner_ManualAssignLater()
    {
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

        await workflow.AssignRunnerAsync(runnerId);
        await runner.AssignWorkflowAsync(_workflowId!);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.StartsWith("task-1.", work.WorkId);
    }
}
