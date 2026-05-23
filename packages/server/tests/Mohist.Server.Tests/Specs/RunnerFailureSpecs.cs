using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task InFlightTask_Fails_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("TaskFailed", status.Failure.Reason);
        Assert.Contains("timeout", status.Failure.Message);
    }

    [Fact]
    public async Task InFlightChecks_Fails_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, _) = await PollWorkAnyAsync();

        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckUnrepaired", status.Failure.Reason);
    }

    [Fact]
    public async Task InFlightTask_Fails_WorkflowReleasedFromBacklog()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key);
        var running = await backlog.ListRunningAsync();
        Assert.All(running, r => Assert.NotEqual(_workflowId, r.WorkflowId));
    }

    [Fact]
    public async Task InFlightTask_Fails_RetryRecoversWorkflow()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();
        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, rr2) = await PollWorkAnyAsync();
        Assert.Equal("task", retried.WorkType);
        await ReportAsync(rr2, retried.WorkId, "completed");

        var (checks, rr3) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(rr3, checks, "check-1");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Passed", status.Status);
    }

    [Fact]
    public async Task InFlightChecks_Fails_RetryRecoversWorkflow()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, _) = await PollWorkAnyAsync();
        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, rr2) = await PollWorkAnyAsync();
        Assert.Equal("checks", retried.WorkType);
        await ReportChecksPassAsync(rr2, retried, "check-1");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Passed", status.Status);
    }

    [Fact]
    public async Task InFlightWork_Fails_StatusExposesRetryAndRerunActions()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();
        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
    }

    [Fact]
    public async Task StaleReport_AfterFailInFlight_IsIgnored()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        var staleWorkId = task.WorkId;

        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        await ReportAsync(r1, staleWorkId, "completed");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
    }

    [Fact]
    public async Task FailInFlight_NoInFlightWork_DoesNothing()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        await workflow.FailInFlightWorkAsync("nothing in flight");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task RunnerUnregistered_WithInFlightTask_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        await runner.UnregisterAsync();

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.Contains("unregistered", status.Failure?.Message);
    }

    [Fact]
    public async Task RunnerUnregistered_NoInFlightWork_WorkflowUnaffected()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        await runner.UnregisterAsync();

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task InFlightLoad_Fails_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build",
                [],
                [new("check-1", "Check 1", "spec/check")],
                TasksFromUses: "spec/loader")
        ]));

        var (loadWork, _) = await PollWorkAnyAsync();
        Assert.Equal("load", loadWork.WorkType);

        await workflow.FailInFlightWorkAsync("Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
    }
}
