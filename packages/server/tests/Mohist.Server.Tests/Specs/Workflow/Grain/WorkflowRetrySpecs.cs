using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using System.Text.Json;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class WorkflowRetrySpecs : WorkflowGrainSpecs
{
    public WorkflowRetrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_RunnerGetsNextTaskAttempt()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.1", task.WorkId);
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var (retried, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.2", retried.WorkId);
        Assert.Null(SessionName(retried));
        Assert.Equal(r1, r2);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFailsBeforeLaterTasks_UserRetriesWorkflow_NewAttemptRunsBeforeLaterTasks()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks: []));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.1", task1.WorkId);
        await ReportAsync(r1, task1.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var (retried, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.2", retried.WorkId);
        Assert.Null(SessionName(retried));
        await ReportAsync(r2, retried.WorkId, "completed");

        var (task2, _) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.1", task2.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_PreviousAttemptStaysFailed()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        var buildStage = status.Stages.Find(s => s.Stage == "build");
        Assert.NotNull(buildStage);
        var task1 = buildStage.Tasks.Find(t => t.Id == "task-1.1");
        Assert.NotNull(task1);
        Assert.Equal("failed", task1.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_StatusNoLongerShowsActiveFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("running", status.Status);
        Assert.Null(status.Failure);
        var buildStage = Assert.Single(status.Stages, s => s.Stage == "build");
        Assert.Null(buildStage.Failure);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_WorkflowContinuesAfterTaskSucceeds()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var (retried, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.2", retried.WorkId);
        Assert.Null(SessionName(retried));
        await ReportAsync(r2, retried.WorkId, "completed");

        var (checks, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", checks.WorkId);
        await ReportChecksPassAsync(r3, checks, "check-1");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_UserRetriesWorkflow_CheckRunsAgain()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", checks.WorkId);
        await ReportChecksFailAsync(r2, checks, "check-1", "broken");

        await workflow.RetryAsync();

        var (retried, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", retried.WorkId);
        Assert.NotEqual(checks.WorkId, retried.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_UserRetriesWorkflow_WorkflowContinuesAfterCheckPasses()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "check-1", "broken");

        await workflow.RetryAsync();

        var (retried, r3) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r3, retried, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowIsRunning_UserRetriesWorkflow_RetryIsRejected()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (_, r1) = await PollWorkAnyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workflow.RetryAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task PlanIsRejected_LegacyRejectRoutesToFeedbackLoop_RetryIsNotRejected()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, checks, "plan-ok");

        // Legacy reject no longer fails the workflow; it routes through
        // the feedback loop. The workflow is now Running, not Failed,
        // so RetryAsync throws because Retry is reserved for failed runs.
#pragma warning disable CS0618
        await workflow.RequestChangesAsync("needs rework");
#pragma warning restore CS0618

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workflow.RetryAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LegacyReject_UserViewsWorkflowStatus_FeedbackLoopIsObservable()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, checks, "plan-ok");

        // The legacy reject path now routes through the feedback loop.
        // The workflow is Running, not Failed, and the available actions
        // list shows a request-changes action (instead of the prior
        // retry/rerun failure-recovery actions).
#pragma warning disable CS0618
        await workflow.RequestChangesAsync("needs rework");
#pragma warning restore CS0618

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("running", status.Status);

        // The workflow is running with an apply-feedback task scheduled,
        // so no recovery actions should be present.
        Assert.Empty(status.AvailableActions);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LegacyApprovalRejectedWithoutWorkflowFailure_UserViewsWorkflowStatus_RerunActionIsAvailable()
    {
        var run = WorkflowRun.Create("legacy-approval-rejected", ApprovalStage());
        run.Start();
        var stage = run.CurrentStage();
        stage.Initialized = true;
        stage.Status = StageRunStatus.Failed;
        stage.ApprovalStatus = new ApprovalStatus(
            "rejected",
            DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"));
        stage.Failure = new FailureDetails(FailureReason.ApprovalRejected, stage.Id, Message: "needs rework");
        run.Status = WorkflowRunStatus.Failed;

        var status = WorkflowStatusMapper.BuildStatusView(run, null);

        Assert.NotNull(status);
        Assert.NotNull(status.Failure);
        Assert.Equal("ApprovalRejected", status.Failure.Reason);
        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("plan", rerunAction.Target);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_UserRerunsStage_StageStartsFromFirstTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
            checks: []));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);
        await ReportAsync(r1, task1.WorkId, "failed", "boom");

        await workflow.RerunAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task2.WorkId);
        await ReportAsync(r2, task2.WorkId, "completed");

        var (task3, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task3.WorkId);
        await ReportAsync(r3, task3.WorkId, "completed");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_UserRerunsStage_StageStartsFromFirstTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "check-1", "broken");

        await workflow.RerunAsync();

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task2.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskFails_UserViewsWorkflowStatus_RetryActionIsAvailable()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "compile error");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("TaskFailed", status.Failure.Reason);
        Assert.Equal("task-1.1", status.Failure.TaskId);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("task-1.1", retryAction.Target);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("build", rerunAction.Target);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_UserViewsWorkflowStatus_RetryActionIsAvailable()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "check-1", "typecheck errors");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckUnrepaired", status.Failure.Reason);
        Assert.Equal("check-1", status.Failure.CheckName);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("check-1", retryAction.Target);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ApprovalRequested_UserViewsWorkflowStatus_ApprovalActionsAreAvailable()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, checks, "plan-ok");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("awaiting-approval", status.Status);

        var approveAction = status.AvailableActions.Find(a => a.Name == "approve");
        Assert.NotNull(approveAction);

        var requestChangesAction = status.AvailableActions.Find(a => a.Name == "request-changes");
        Assert.NotNull(requestChangesAction);
        Assert.Equal("Request changes", requestChangesAction!.Label);

        // The legacy "reject" action must NOT be present.
        Assert.Null(status.AvailableActions.Find(a => a.Name == "reject"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowIsRunning_UserViewsWorkflowStatus_NoRetryActionAvailable()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (_, r1) = await PollWorkAnyAsync();

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("running", status.Status);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.Null(retryAction);
    }

    private static string? SessionName(WorkDispatch dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.With))
            return null;

        using var document = JsonDocument.Parse(dispatch.With!);
        return document.RootElement.TryGetProperty("session", out var session)
            ? session.GetString()
            : null;
    }
}
