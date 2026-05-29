using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task InFlightTask_LosesRunner_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("TaskFailed", status.Failure.Reason);
        Assert.Contains("timeout", status.Failure.Message);
    }

    [Fact]
    public async Task InFlightChecks_LoseRunner_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, _) = await PollWorkAnyAsync();

        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckUnrepaired", status.Failure.Reason);
    }

    [Fact]
    public async Task InFlightTask_LosesRunner_WorkflowLeavesBacklog()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync());
    }

    [Fact]
    public async Task InFlightTask_LosesRunnerThenUserRetries_WorkflowPasses()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();
        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

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
        Assert.Equal("Completed", status.Status);
    }

    [Fact]
    public async Task InFlightChecks_LoseRunnerThenUserRetries_WorkflowPasses()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, _) = await PollWorkAnyAsync();
        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

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
        Assert.Equal("Completed", status.Status);
    }

    [Fact]
    public async Task InFlightWork_LosesRunner_StatusShowsRetryAndRerun()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();
        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
    }

    [Fact]
    public async Task StaleReport_ArrivesAfterInFlightFailure_WorkflowStaysFailed()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        var staleWorkId = task.WorkId;

        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        await ReportAsync(r1, staleWorkId, "completed");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
    }

    [Fact]
    public async Task WorkflowHasNoInFlightWork_RunnerFailureDoesNothing()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        await workflow.FailCurrentWorkAsync(_runnerId!, "nothing in flight");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task RunnerUnregistersWithInFlightTask_WorkflowFails()
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
    public async Task RunnerUnregistersWithoutInFlightWork_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        await runner.UnregisterAsync();

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task InFlightLoadTask_LosesRunner_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/loader")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (loadWork, _) = await PollWorkAnyAsync();
        Assert.Equal("task", loadWork.WorkType);
        Assert.StartsWith("load-tasks.", loadWork.WorkId);

        await workflow.FailCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
    }

    [Fact]
    public async Task NewRunnerHoldsInFlightTask_OldRunnerUnregisters_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, _) = await PollWorkAnyAsync();
        Assert.Equal("task", retried.WorkType);

        var oldRunner = Grains.GetGrain<IRunnerGrain>(r1);
        await oldRunner.UnregisterAsync();

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task NewRunnerHoldsInFlightChecks_OldRunnerUnregisters_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, _) = await PollWorkAnyAsync();
        await workflow.FailCurrentWorkAsync(r1, "timeout");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retriedChecks, _) = await PollWorkAnyAsync();
        Assert.Equal("checks", retriedChecks.WorkType);

        var oldRunner = Grains.GetGrain<IRunnerGrain>(r1);
        await oldRunner.UnregisterAsync();

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task NewRunnerHoldsInFlightTask_OldRunnerFailsInFlightWork_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, _) = await PollWorkAnyAsync();
        Assert.Equal("task", retried.WorkType);

        await workflow.FailCurrentWorkAsync(r1, "stale runner timeout");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
    }

    [Fact]
    public async Task LoadTaskFails_UserViewsStatus_RetryActionIsAvailable()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/loader")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (loadWork, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, loadWork.WorkId, "failed", "loader crashed");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);
    }

    [Fact]
    public async Task LoadTaskFails_UserViewsStatus_RerunActionIsAvailable()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/loader")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (loadWork, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, loadWork.WorkId, "failed", "loader crashed");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
    }

    [Fact]
    public async Task LoadTaskFails_UserRerunsStage_LoadTaskRunsAgain()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/loader")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (loadWork, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, loadWork.WorkId, "failed", "loader crashed");

        await workflow.RerunAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, _) = await PollWorkAnyAsync();
        Assert.Equal("task", retried.WorkType);
        Assert.StartsWith("load-tasks.", retried.WorkId);
    }

    [Fact]
    public async Task LoadTaskFails_UserRetriesWorkflow_RetryWorks()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/loader")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (loadWork, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, loadWork.WorkId, "failed", "loader crashed");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, _) = await PollWorkAnyAsync();
        Assert.StartsWith("load-tasks.", retried.WorkId);
        Assert.Equal("task", retried.WorkType);
    }
}
