using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using System.Text.Json;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;
[Collection("WorkflowRecovery")]
public class WorkflowRetrySpecs : WorkflowGrainSpecs
{
    public WorkflowRetrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }
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
    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_StatusNoLongerShowsActiveFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "flaky");

        await workflow.RetryAsync();

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        // After retry the failure is cleared and the new task is
        // dispatchable; the runner is still assigned so the run lands
        // on Ready (no in-flight work yet).
        Assert.Equal("ready", status.Status);
        Assert.Null(status.Failure);
        var buildStage = Assert.Single(status.Stages, s => s.Stage == "build");
        Assert.Null(buildStage.Failure);
    }
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

    [Fact]
    public async Task TaskFails_UserRetriesWorkflow_RetriedTaskKeepsRecovery()
    {
        var recovery = new RecoveryDefinition(
            2,
            [
                new RecoveryHandlerDefinition(
                    "error.code=base-moved",
                    [
                        new TaskDefinition(
                            "recover:rebase",
                            "Rebase after base moved",
                            "spec/task")
                    ],
                    RetrySelf: true)
            ]);
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "merge-pr",
                    "Merge PR",
                    "spec/task",
                    Recovery: recovery)
            ],
            checks: []));

        var (firstAttempt, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("merge-pr.1", firstAttempt.WorkId);
        Assert.NotNull(firstAttempt.Recovery);
        Assert.Null(firstAttempt.RecoveryRemaining);
        await ReportAsync(r1, firstAttempt.WorkId, "failed", "transient gh EOF");

        await workflow.RetryAsync();

        var (retriedAttempt, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("merge-pr.2", retriedAttempt.WorkId);
        Assert.Equal(r1, r2);
        Assert.NotNull(retriedAttempt.Recovery);
        Assert.Null(retriedAttempt.RecoveryRemaining);
        using (var recoveryJson = JsonDocument.Parse(retriedAttempt.Recovery!))
        {
            Assert.Equal("error.code=base-moved",
                recoveryJson.RootElement
                    .GetProperty("handlers")[0]
                    .GetProperty("when")
                    .GetString());
        }

        await ReportAsync(r2, retriedAttempt.WorkId, new WorkResult(
            "completed",
            "Merge PR failed (error.code=base-moved); recovery scheduled",
            Output: JSON.DeserializeElement("""{"errorCode":"base-moved"}"""),
            AddTasks:
            [
                new RuntimeTaskInput(
                    "recover:rebase",
                    "Rebase after base moved",
                    "spec/task"),
                new RuntimeTaskInput(
                    "merge-pr",
                    "Merge PR",
                    "spec/task",
                    Recovery: recovery,
                    RecoveryRemaining: 1)
            ]));

        var (recoveryTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("recover:rebase.1", recoveryTask.WorkId);
        Assert.Equal(r2, r3);

        await ReportAsync(r3, recoveryTask.WorkId, "completed");
        var (selfRetry, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("merge-pr.3", selfRetry.WorkId);
        Assert.Equal(1, selfRetry.RecoveryRemaining);
        using var selfRetryRecovery = JsonDocument.Parse(selfRetry.Recovery!);
        Assert.Equal(2, selfRetryRecovery.RootElement.GetProperty("budget").GetInt32());
    }

    [Fact]
    public async Task RecoveryFollowUpWithoutRemainingState_FailsTheRunTerminallyInsteadOfThrowing()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=base-moved", [], RetrySelf: false)]);
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
            checks: []));
        var (task, runnerId) = await PollWorkAnyAsync();

        await ReportAsync(runnerId, task.WorkId, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{}"),
            AddTasks: [new RuntimeTaskInput("recover", "Recover", "spec/task", Recovery: recovery)]));

        var failed = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Failed, failed.CurrentStage().Tasks.Single().Status);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task RecoveryFollowUpOutsideDeclaredBudget_FailsTheRunTerminallyWithoutMutatingEarlierTasks(int recoveryRemaining)
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=base-moved", [], RetrySelf: false)]);
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
            checks: []));
        var (task, runnerId) = await PollWorkAnyAsync();

        await ReportAsync(runnerId, task.WorkId, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{}"),
            AddTasks: [new RuntimeTaskInput("merge-pr", "Merge PR", "spec/task", Recovery: recovery, RecoveryRemaining: recoveryRemaining)]));

        var failed = await LoadRunAsync(_workflowId!);
        var active = Assert.Single(failed.CurrentStage().Tasks);
        Assert.Equal("merge-pr.1", active.Id);
        Assert.Equal(TaskRunStatus.Failed, active.Status);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task RecoveryFollowUpBatchWithInvalidContinuation_FailsTheRunTerminallyWithoutInsertingEarlierTasks()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=base-moved", [], RetrySelf: false)]);
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("merge-pr", "Merge PR", "spec/task", Recovery: recovery)],
            checks: []));
        var (task, runnerId) = await PollWorkAnyAsync();

        await ReportAsync(runnerId, task.WorkId, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{}"),
            AddTasks:
            [
                new RuntimeTaskInput("fix", "Fix", "spec/fix"),
                new RuntimeTaskInput("merge-pr", "Merge PR", "spec/task", Recovery: recovery, RecoveryRemaining: 3),
            ]));

        var failed = await LoadRunAsync(_workflowId!);
        var active = Assert.Single(failed.CurrentStage().Tasks);
        Assert.Equal("merge-pr.1", active.Id);
        Assert.Equal(TaskRunStatus.Failed, active.Status);
        Assert.Equal(WorkflowRunStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task StaleFailedTaskReport_DoesNotFailNewerActiveTask()
    {
        await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition("first", "First", "spec/task"),
                new TaskDefinition("second", "Second", "spec/task"),
            ],
            checks: []));
        var (first, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, first.WorkId, "completed");
        var (second, sameRunner) = await PollWorkAnyAsync();

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        var ack = await workflow.ReceiveTaskReportAsync(sameRunner, first.WorkId, new TaskReport(
            first.WorkId,
            TaskReportStatus.Failed,
            Output: null,
            Artifacts: null,
            Detail: "stale report"));

        Assert.Equal(ReportAck.Stale, ack);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, run.CurrentStage().Tasks.Single(task => task.Id == "first.1").Status);
        Assert.Equal(TaskRunStatus.Running, run.CurrentStage().Tasks.Single(task => task.Id == "second.1").Status);
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
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("review", "Review", "spec/review", Recovery: recovery)],
            checks: []));

        var (first, runnerId) = await PollWorkAnyAsync();
        Assert.Null(first.RecoveryRemaining);
        await ReportAsync(runnerId, first, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{\"errorCode\":\"fail\"}"),
            AddTasks: RecoveryFollowUps(recovery, 1)));

        var (fix1, sameRunner) = await PollWorkAnyAsync();
        Assert.StartsWith("fix.1", fix1.WorkId);
        await ReportAsync(sameRunner, fix1, "completed");
        var (second, runnerAfterFix1) = await PollWorkAnyAsync();
        Assert.Equal(1, second.RecoveryRemaining);
        await ReportAsync(runnerAfterFix1, second, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{\"errorCode\":\"fail\"}"),
            AddTasks: RecoveryFollowUps(recovery, 0)));

        var (fix2, runnerAfterSecond) = await PollWorkAnyAsync();
        Assert.StartsWith("fix.2", fix2.WorkId);
        await ReportAsync(runnerAfterSecond, fix2, "completed");
        var (exhausted, runnerAfterFix2) = await PollWorkAnyAsync();
        Assert.Equal(0, exhausted.RecoveryRemaining);
        await ReportAsync(runnerAfterFix2, exhausted, new WorkResult(
            "failed",
            "review failed",
            Output: JSON.DeserializeElement("{\"errorCode\":\"fail\"}")));

        await workflow.RetryAsync();

        var (fresh, retryRunner) = await PollWorkAnyAsync();
        Assert.StartsWith("review.4", fresh.WorkId);
        Assert.Null(fresh.RecoveryRemaining);
        await ReportAsync(retryRunner, fresh, new WorkResult(
            "completed",
            Output: JSON.DeserializeElement("{\"errorCode\":\"fail\"}"),
            AddTasks: RecoveryFollowUps(recovery, 1)));

        var persisted = await LoadRunAsync(_workflowId!);
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
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", checks.WorkId);
        await ReportChecksFailAsync(r2, checks, "check-1", "broken");

        await workflow.RetryAsync();

        var (retried, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", retried.WorkId);
    }

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
        Assert.Null(await runner.PollAsync(Services));
    }

    [Fact]
    public async Task WorkflowIsRunning_UserRetriesWorkflow_RetryIsRejected()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (_, r1) = await PollWorkAnyAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workflow.RetryAsync());
    }

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

    [Fact]
    public async Task LegacyReject_UserViewsWorkflowStatus_FeedbackLoopIsObservable()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, checks, "plan-ok");

        // The legacy reject path now routes through the feedback loop.
        // The workflow is dispatched with an apply-feedback task, not
        // Failed, and the available actions list shows a request-changes
        // action (instead of the prior retry/rerun failure-recovery
#pragma warning disable CS0618
        await workflow.RequestChangesAsync("needs rework");
#pragma warning restore CS0618

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        // After RequestChanges, the legacy approval is replaced with a
        // feedback task the runner can pick up. With the runner still
        // assigned and dispatchable work queued, the run is Ready
        // (not in-flight yet — the runner hasn't picked the new task).
        Assert.Equal("ready", status.Status);

        // The workflow is dispatched with an apply-feedback task scheduled,
        // so no recovery actions should be present.
        Assert.Empty(status.AvailableActions);
    }

    [Fact]
    public async Task LegacyApprovalRejectedWithoutWorkflowFailure_UserViewsWorkflowStatus_RerunActionIsAvailable()
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
        Assert.NotNull(status.Failure);
        Assert.Equal("ApprovalRejected", status.Failure.Reason);
        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("plan", rerunAction.Target);
    }

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

    [Fact]
    public async Task WorkflowFailed_UserViewsWorkflowStatus_StartNewWorkflowActionIsAvailable()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "compile error");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);

        var startAction = status.AvailableActions.Find(a => a.Name == "start");
        Assert.NotNull(startAction);
        Assert.Equal("Start new workflow", startAction!.Label);
    }

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
        Assert.Equal("CheckFailed", status.Failure.Reason);
        Assert.Equal("check-1", status.Failure.CheckName);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("check-1", retryAction.Target);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
    }

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
