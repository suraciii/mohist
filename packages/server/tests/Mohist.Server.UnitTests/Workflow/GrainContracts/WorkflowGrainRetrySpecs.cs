using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Retry, rerun, recovery-round, and legacy-reject arbitration of failed
/// workflow runs plus their status projections, driven through the real
/// grain without a cluster. Migrates the SpecTests WorkflowRetrySpecs (#681);
/// the runner-id equality facts of the original polls are dispatch-door
/// details owned by the retained representative proofs.
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainRetrySpecs
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainRetrySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_RunnerGetsNextTaskAttempt()
    {
        var arrangement = await ArrangeAsync("wr-retry-next-attempt");

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task, "flaky");

        await arrangement.Grain.RetryAsync();

        var retried = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-1.2", retried!.Id);
        // A fresh attempt carries no session binding.
        Assert.Null(SessionName(retried));
    }

    [Fact]
    public async Task TaskFailsBeforeLaterTasks_UserRetriesWorkflow_NewAttemptRunsBeforeLaterTasks()
    {
        var arrangement = await ArrangeAsync(
            "wr-retry-order",
            SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
                checks: []));

        var task1 = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task1, "flaky");

        await arrangement.Grain.RetryAsync();

        var retried = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-1.2", retried!.Id);
        await arrangement.ReportCompletedAsync(retried);

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-2.1", task2!.Id);
    }

    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_PreviousAttemptStaysFailed()
    {
        var arrangement = await ArrangeAsync("wr-retry-previous-stays");

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task, "flaky");

        await arrangement.Grain.RetryAsync();

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        var buildStage = status!.Stages.Find(stage => stage.Stage == "build");
        Assert.NotNull(buildStage);
        var task1 = buildStage!.Tasks.Find(t => t.Id == "task-1.1");
        Assert.NotNull(task1);
        Assert.Equal("failed", task1!.Status);
    }

    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_StatusNoLongerShowsActiveFailure()
    {
        var arrangement = await ArrangeAsync("wr-retry-failure-cleared");

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task, "flaky");

        await arrangement.Grain.RetryAsync();

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        // After retry the failure is cleared and the new task is
        // dispatchable; the runner is still assigned so the run lands
        // on Ready (no in-flight work yet).
        Assert.Equal("ready", status!.Status);
        Assert.Null(status.Failure);
        var buildStage = Assert.Single(status.Stages, stage => stage.Stage == "build");
        Assert.Null(buildStage.Failure);
    }

    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_WorkflowContinuesAfterTaskSucceeds()
    {
        var arrangement = await ArrangeAsync("wr-retry-continues");

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task, "flaky");

        await arrangement.Grain.RetryAsync();

        var retried = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-1.2", retried!.Id);
        await arrangement.ReportCompletedAsync(retried);

        var checks = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(checks);
        Assert.StartsWith("checks-", checks!.Id);
        await arrangement.ReportChecksPassAsync(checks, "check-1");
    }

    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_RetriedTaskKeepsRecovery()
    {
        var recovery = new RecoveryDefinition(
            2,
            [
                new RecoveryHandlerDefinition(
                    "error.code=base-moved",
                    [
                        new TaskDefinition("recover:rebase", "Rebase after base moved", "spec/task"),
                    ],
                    RetrySelf: true),
            ]);
        var arrangement = await ArrangeAsync(
            "wr-retry-keeps-recovery",
            SingleStage(
                tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
                checks: []));

        var firstAttempt = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("merge-pr.1", firstAttempt.Id);
        Assert.NotNull(firstAttempt.Recovery);
        Assert.Null(firstAttempt.RecoveryRemaining);
        await arrangement.ReportFailedAsync(firstAttempt, "transient gh EOF");

        await arrangement.Grain.RetryAsync();

        var retriedAttempt = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("merge-pr.2", retriedAttempt!.Id);
        Assert.NotNull(retriedAttempt.Recovery);
        Assert.Null(retriedAttempt.RecoveryRemaining);
        Assert.Equal(
            "error.code=base-moved",
            retriedAttempt.Recovery!.Handlers[0].When);

        await arrangement.ReportTaskResultAsync(
            retriedAttempt,
            JsonSerializer.SerializeToElement(new { errorCode = "base-moved" }),
            [
                new RuntimeTaskInput("recover:rebase", "Rebase after base moved", "spec/task"),
                new RuntimeTaskInput("merge-pr", "Merge PR", "spec/task", Recovery: recovery, RecoveryRemaining: 1),
            ]);

        var recoveryTask = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("recover:rebase.1", recoveryTask!.Id);

        await arrangement.ReportCompletedAsync(recoveryTask);
        var selfRetry = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("merge-pr.3", selfRetry!.Id);
        Assert.Equal(1, selfRetry.RecoveryRemaining);
        Assert.Equal(2, selfRetry.Recovery!.Budget);
    }

    [Fact]
    public async Task RecoveryFollowUpWithoutRemainingState_FailsTheRunTerminallyInsteadOfThrowing()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=base-moved", [], RetrySelf: true)]);
        var arrangement = await ArrangeAsync(
            "wr-retry-followup-no-state",
            SingleStage(
                tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
                checks: []));

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.Grain.ReceiveTaskReportAsync(
            arrangement.WorkerId,
            task.Id!,
            new TaskReport(
                task.Id!,
                TaskReportStatus.Succeeded,
                Output: JsonSerializer.SerializeToElement(new { }),
                Artifacts: null,
                AddTasks: [new RuntimeTaskInput("recover", "Recover", "spec/task", Recovery: recovery)],
                ActionAttemptId: await arrangement.RunningActionAttemptIdAsync()));

        var failed = await RequireRunAsync(arrangement);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, failed.CurrentStage().Tasks.Single().Status);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task RecoveryFollowUpBatch_AcceptsOutOfRangeContinuationAlongsideValidFollowUp()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=base-moved", [], RetrySelf: true)]);
        var arrangement = await ArrangeAsync(
            "wr-retry-out-of-range",
            SingleStage(
                tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
                checks: []));

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            task,
            JsonSerializer.SerializeToElement(new { }),
            [
                new RuntimeTaskInput("fix", "Fix", "spec/fix"),
                new RuntimeTaskInput("merge-pr", "Merge PR", "spec/task", Recovery: recovery, RecoveryRemaining: 3),
            ]);

        var run = await RequireRunAsync(arrangement);
        var attempts = run.CurrentStage().Tasks.Where(t => t.DefinitionId == "merge-pr").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, attempts[0].Status);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, attempts[1].Status);
        Assert.Equal(3, attempts[1].RecoveryRemaining);
        Assert.Equal(2, attempts[1].Recovery!.Budget);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public async Task NonRecoveryFollowUpWithRecoveryRemaining_StillFailsTheRunTerminally()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=base-moved", [], RetrySelf: true)]);
        var arrangement = await ArrangeAsync(
            "wr-retry-non-recovery-followup",
            SingleStage(
                tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
                checks: []));

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            task,
            JsonSerializer.SerializeToElement(new { }),
            [new RuntimeTaskInput("merge-pr", "Merge PR", "spec/task", RecoveryRemaining: 1)]);

        var failed = await RequireRunAsync(arrangement);
        var active = Assert.Single(failed.CurrentStage().Tasks);
        Assert.Equal("merge-pr.1", active.Id);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, active.Status);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task StaleFailedTaskReport_DoesNotFailNewerActiveTask()
    {
        var arrangement = await ArrangeAsync(
            "wr-retry-stale-report",
            SingleStage(
                tasks: [new("first", "First", "spec/task"), new("second", "Second", "spec/task")],
                checks: []));

        var first = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(first);
        var second = (await arrangement.AssignAndClaimAsync())!;

        var ack = await arrangement.Grain.ReceiveTaskReportAsync(
            arrangement.WorkerId,
            first.Id!,
            new TaskReport(
                first.Id!,
                TaskReportStatus.Failed,
                Output: null,
                Artifacts: null,
                Detail: "stale report",
                ActionAttemptId: await PersistedActionAttemptIdAsync(arrangement, "first.1")));

        Assert.Equal(WorkReportVerdict.Refused, ack);
        var run = await RequireRunAsync(arrangement);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, run.CurrentStage().Tasks.Single(task => task.Id == "first.1").Status);
        Assert.Equal(WorkflowActionAttemptStatus.Running, run.CurrentStage().Tasks.Single(task => task.Id == "second.1").Status);
    }

    [Fact]
    public async Task ExhaustedRecoveryRound_UserRetryStartsNewFullRound()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition(
                "error.code=fail",
                [new TaskDefinition("fix", "Fix", "spec/fix")],
                RetrySelf: true)]);
        var arrangement = await ArrangeAsync(
            "wr-retry-exhausted-round",
            SingleStage(
                tasks: [new TaskDefinition("review", "Review", "spec/review", Recovery: recovery)],
                checks: []));

        var first = (await arrangement.AssignAndClaimAsync())!;
        Assert.Null(first.RecoveryRemaining);
        await arrangement.ReportTaskResultAsync(
            first,
            JsonSerializer.SerializeToElement(new { errorCode = "fail" }),
            RecoveryFollowUps(recovery, 1));

        var fix1 = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("fix.1", fix1.Id);
        await arrangement.ReportCompletedAsync(fix1);
        var second = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal(1, second.RecoveryRemaining);
        await arrangement.ReportTaskResultAsync(
            second,
            JsonSerializer.SerializeToElement(new { errorCode = "fail" }),
            RecoveryFollowUps(recovery, 0));

        var fix2 = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("fix.2", fix2.Id);
        await arrangement.ReportCompletedAsync(fix2);
        var exhausted = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal(0, exhausted.RecoveryRemaining);
        await arrangement.ReportTaskResultAsync(
            exhausted,
            JsonSerializer.SerializeToElement(new { errorCode = "fail" }),
            addTasks: null,
            status: TaskReportStatus.Failed);

        await arrangement.Grain.RetryAsync();

        var fresh = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("review.4", fresh!.Id);
        Assert.Null(fresh.RecoveryRemaining);
        await arrangement.ReportTaskResultAsync(
            fresh,
            JsonSerializer.SerializeToElement(new { errorCode = "fail" }),
            RecoveryFollowUps(recovery, 1));

        var persisted = await RequireRunAsync(arrangement);
        var attempts = persisted.CurrentStage().Tasks.Where(t => t.DefinitionId == "review").ToList();
        Assert.Equal(new int?[] { null, 1, 0, null, 1 }, attempts.Select(t => t.RecoveryRemaining).ToArray());
        Assert.All(attempts, task => Assert.Equal(2, task.Recovery!.Budget));
    }

    private static List<RuntimeTaskInput> RecoveryFollowUps(RecoveryDefinition recovery, int remaining) =>
    [
        new RuntimeTaskInput("fix", "Fix", "spec/fix"),
        new RuntimeTaskInput("review", "Review", "spec/review", Recovery: recovery, RecoveryRemaining: remaining),
    ];

    [Fact]
    public async Task CheckFails_UserRetriesWorkflow_CheckRunsAgain()
    {
        var arrangement = await ArrangeAsync("wr-retry-check-again");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(checks, ("check-1", CheckResultStatus.Failed, "broken"));

        await arrangement.Grain.RetryAsync();

        var retried = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(retried);
        Assert.StartsWith("checks-", retried!.Id);
    }

    [Fact]
    public async Task CheckFails_UserRetriesWorkflow_WorkflowContinuesAfterCheckPasses()
    {
        var arrangement = await ArrangeAsync("wr-retry-check-pass");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(checks, ("check-1", CheckResultStatus.Failed, "broken"));

        await arrangement.Grain.RetryAsync();

        var retried = await arrangement.AssignAndClaimAsync();
        await arrangement.ReportChecksPassAsync(retried!, "check-1");
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task WorkflowIsRunning_UserRetriesWorkflow_RetryIsRejected()
    {
        var arrangement = await ArrangeAsync("wr-retry-running-rejected");
        var _ = await arrangement.AssignAndClaimAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await arrangement.Grain.RetryAsync());
    }

    [Fact]
    public async Task PlanIsRejected_LegacyRejectRoutesToFeedbackLoop_RetryIsNotRejected()
    {
        var arrangement = await ArrangeAsync("wr-retry-legacy-feedback", ApprovalStage());
        await DrivePlanToGateAsync(arrangement);

        // Legacy reject no longer fails the workflow; it routes through
        // the feedback loop. The workflow is Running, not Failed, so
        // RetryAsync throws because Retry is reserved for failed runs.
#pragma warning disable CS0618
        await arrangement.Grain.RequestChangesAsync("needs rework", "operator-1");
#pragma warning restore CS0618

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await arrangement.Grain.RetryAsync());
    }

    [Fact]
    public async Task LegacyReject_UserViewsWorkflowStatus_FeedbackLoopIsObservable()
    {
        var arrangement = await ArrangeAsync("wr-retry-legacy-status", ApprovalStage());
        await DrivePlanToGateAsync(arrangement);

#pragma warning disable CS0618
        await arrangement.Grain.RequestChangesAsync("needs rework", "operator-1");
#pragma warning restore CS0618

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        // After RequestChanges, the legacy approval is replaced with a
        // feedback task the runner can pick up. With the runner still
        // assigned and dispatchable work queued, the run is Ready.
        Assert.Equal("ready", status!.Status);

        // An apply-feedback task is scheduled, so no recovery actions
        // should be present.
        Assert.Empty(status.AvailableActions);
    }

    [Fact]
    public void LegacyApprovalRejectedWithoutWorkflowFailure_UserViewsWorkflowStatus_RerunActionIsAvailable()
    {
        var run = WorkflowRun.Create("legacy-approval-rejected", ApprovalStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var stage = run.CurrentStage();
        stage.Initialized = true;
        stage.Status = StageRunStatus.Failed;
        stage.ApprovalStatus = new ApprovalStatus(
            "rejected",
            TestTime.UtcNow.AddMinutes(-1).ToString("O"),
            TestTime.UtcNow.ToString("O"));
        stage.Failure = new FailureDetails(FailureReason.ApprovalRejected, stage.Id, Message: "needs rework");
        run.Status = WorkflowRunStatus.Failed;

        var status = WorkflowStatusMapper.BuildStatusView(run, null);

        Assert.NotNull(status);
        Assert.NotNull(status!.Failure);
        Assert.Equal("ApprovalRejected", status.Failure!.Reason);
        var rerunAction = status.AvailableActions.Find(action => action.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("plan", rerunAction!.Target);
    }

    [Fact]
    public async Task TaskFails_UserRerunsStage_StageStartsFromFirstTask()
    {
        var arrangement = await ArrangeAsync(
            "wr-rerun-from-first-task",
            SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
                checks: []));

        var task1 = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task1, "boom");

        await arrangement.Grain.RerunAsync();

        var retriedTask1 = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-1.", retriedTask1!.Id);
        await arrangement.ReportCompletedAsync(retriedTask1);

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-2.", task2!.Id);
    }

    [Fact]
    public async Task CheckFails_UserRerunsStage_StageStartsFromFirstTask()
    {
        var arrangement = await ArrangeAsync("wr-rerun-check-first-task");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(checks, ("check-1", CheckResultStatus.Failed, "broken"));

        await arrangement.Grain.RerunAsync();

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(task2);
        Assert.StartsWith("task-1.", task2!.Id);
    }

    [Fact]
    public async Task StagePasses_UserRerunsStage_StageStartsFromFirstTask()
    {
        var arrangement = await ArrangeAsync("wr-rerun-completed-stage");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(checks, ("check-1", CheckResultStatus.Passed, null));

        await arrangement.Grain.RerunAsync();

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(task2);
        Assert.StartsWith("task-1.", task2!.Id);
    }

    [Fact]
    public async Task TaskFails_UserViewsWorkflowStatus_RetryActionIsAvailable()
    {
        var arrangement = await ArrangeAsync("wr-retry-action-task");

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task, "compile error");

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("TaskFailed", status.Failure!.Reason);
        Assert.Equal("task-1.1", status.Failure.TaskId);

        var retryAction = status.AvailableActions.Find(action => action.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("task-1.1", retryAction!.Target);

        var rerunAction = status.AvailableActions.Find(action => action.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("build", rerunAction!.Target);
    }

    [Fact]
    public async Task WorkflowFailed_UserViewsWorkflowStatus_StartNewWorkflowActionIsAvailable()
    {
        var arrangement = await ArrangeAsync("wr-retry-action-start-new");

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task, "compile error");

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);

        var startAction = status.AvailableActions.Find(action => action.Name == "start");
        Assert.NotNull(startAction);
        Assert.Equal("Start new workflow", startAction!.Label);
    }

    [Fact]
    public async Task CheckFails_UserViewsWorkflowStatus_RetryActionIsAvailable()
    {
        var arrangement = await ArrangeAsync("wr-retry-action-check");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(checks, ("check-1", CheckResultStatus.Failed, "typecheck errors"));

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckFailed", status.Failure!.Reason);
        Assert.Equal("check-1", status.Failure.CheckName);

        var retryAction = status.AvailableActions.Find(action => action.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("check-1", retryAction!.Target);

        var rerunAction = status.AvailableActions.Find(action => action.Name == "rerun");
        Assert.NotNull(rerunAction);
    }

    [Fact]
    public async Task ApprovalRequested_UserViewsWorkflowStatus_ApprovalActionsAreAvailable()
    {
        var arrangement = await ArrangeAsync("wr-retry-action-approval", ApprovalStage());
        await DrivePlanToGateAsync(arrangement);

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("awaiting-approval", status!.Status);

        var approveAction = status.AvailableActions.Find(action => action.Name == "approve");
        Assert.NotNull(approveAction);

        var requestChangesAction = status.AvailableActions.Find(action => action.Name == "request-changes");
        Assert.NotNull(requestChangesAction);
        Assert.Equal("Request changes", requestChangesAction!.Label);
        Assert.NotNull(status.AvailableActions.Find(action => action.Name == "stop"));

        // The legacy "reject" action must NOT be present.
        Assert.Null(status.AvailableActions.Find(action => action.Name == "reject"));
    }

    [Fact]
    public async Task ApprovalRequested_WithoutFeedbackTasks_HidesRequestChangesButKeepsApprovalAndStop()
    {
        var arrangement = await ArrangeAsync(
            "wr-retry-action-no-feedback",
            ApprovalStage() with { Approval = null });
        await DrivePlanToGateAsync(arrangement);

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("awaiting-approval", status!.Status);
        Assert.NotNull(status.AvailableActions.Find(action => action.Name == "approve"));
        Assert.NotNull(status.AvailableActions.Find(action => action.Name == "stop"));
        Assert.Null(status.AvailableActions.Find(action => action.Name == "request-changes"));
    }

    [Fact]
    public async Task WorkflowIsRunning_UserViewsWorkflowStatus_NoRetryActionAvailable()
    {
        var arrangement = await ArrangeAsync("wr-retry-action-none");
        var _ = await arrangement.AssignAndClaimAsync();

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("running", status!.Status);

        var retryAction = status.AvailableActions.Find(action => action.Name == "retry");
        Assert.Null(retryAction);
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(string runId, WorkflowDefinition? definition = null) =>
        await WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition ?? SingleStage(), TimeProvider);

    /// <summary>Claims and completes the stage's single leading task.</summary>
    private static async Task CompleteFirstTaskAsync(WorkflowGrainArrangement arrangement)
    {
        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);
    }

    /// <summary>Drives the approval stage's task and check to the gate.</summary>
    private static async Task DrivePlanToGateAsync(WorkflowGrainArrangement arrangement)
    {
        var draft = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(draft);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        await arrangement.ReportChecksPassAsync(check!, "plan-ok");
    }

    private static async Task<string> PersistedActionAttemptIdAsync(
        WorkflowGrainArrangement arrangement,
        string workId)
    {
        var run = await arrangement.Store.LoadAsync(arrangement.RunId)
            ?? throw new InvalidOperationException("run missing");
        return run.CurrentStage().Tasks.Single(task => task.WorkId == workId).Id;
    }

    private static string? SessionName(WorkItem item)
    {
        if (item.With is null || !item.With.TryGetValue("session", out var session))
            return null;
        return session.HasValue ? session.Value.GetString() : null;
    }

    private static async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");

    private static WorkflowDefinition SingleStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null) => new(
    [
        new StageDefinition(
            "build",
            tasks ?? [new("task-1", "Task 1", "spec/task")],
            checks ?? [new("check-1", "Check 1", "spec/check")]),
    ]);

    private static WorkflowDefinition ApprovalStage() => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            RequiresApproval: true),
        new StageDefinition(
            "build",
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]),
    ],
    Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
        new TaskDefinition("apply-feedback", "Apply approval feedback", "spec/task")
    ])));
}
