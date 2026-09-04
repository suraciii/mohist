using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

public partial class DispatchServiceReconciliationSpecs
{
    [Fact]
    public async Task PollAsync_LegacyRunningFeedbackGap_ClaimsPendingFeedbackTaskExactlyOnce()
    {
        var workflow = await StartWorkflowAsync(FeedbackWorkflow());
        var runnerId = _runnerId!;

        var (draft, _) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, draft, "completed");
        var (checks, _) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(runnerId, checks, "plan-ok");
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, (await LoadRunAsync(_workflowId!)).Status);

        var feedbackId = await workflow.RequestChangesAsync("apply and publish", "operator-1");
        var approvalRequestCount = (await EventStore.ListAsync(_workflowId!))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested);
        var (apply, _) = await PollWorkAnyAsync();
        Assert.Equal("apply-feedback.1", apply.ActionAttemptId);
        await ReportAsync(runnerId, apply, "completed");

        var current = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Ready, current.Status);
        Assert.Equal(ApprovalFeedbackStatus.Open, current.Feedback.Single(item => item.Id == feedbackId).Status);

        await RewriteAsLegacyRunningGapAsync(current);

        var recovered = Assert.Single((await Dispatch.PollAsync(
            runnerId,
            DispatchTestExtensions.ReadyPollRequest())).Dispatches);
        Assert.Equal(_workflowId, recovered.WorkflowRunId);
        Assert.Equal("publish-feedback.1", recovered.ActionAttemptId);

        var eventsAfterClaim = await EventStore.ListAsync(_workflowId!);
        Assert.Single(eventsAfterClaim, evt =>
            evt.Envelope.Type == EventCatalog.ReverseDns.TaskStarted
            && evt.Envelope.Data?.ToString().Contains("publish-feedback.1", StringComparison.Ordinal) == true);

        var reported = DispatchTestExtensions.ReadyPollRequest() with
        {
            InFlight = [WorkKey(_workflowId!, recovered.WorkId)],
        };
        Assert.Empty((await Dispatch.PollAsync(runnerId, reported)).Dispatches);
        Assert.Equal(eventsAfterClaim.Count, (await EventStore.ListAsync(_workflowId!)).Count);

        await ReportAsync(runnerId, recovered, "completed");

        var resolved = await LoadRunAsync(_workflowId!);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved.Feedback.Single(item => item.Id == feedbackId).Status);
        Assert.Equal(2, resolved.CurrentStage().Attempt);
        Assert.Equal(approvalRequestCount, (await EventStore.ListAsync(_workflowId!))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested));
    }

    [Fact]
    public async Task PollAsync_RunningIdleNonFeedbackOrInFlightRows_AreNotReconciled()
    {
        await ClearBacklogAsync();
        var prefix = $"feedback-reconcile-exclusions-{Guid.NewGuid():N}";
        var projectId = $"{prefix}-project";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"{prefix}-runner", maxWorkflowSlots: 2);

        await InsertReconciliationEnvelopeAsync($"{prefix}-ordinary", projectId, runnerId, taskRunning: false);
        await InsertReconciliationEnvelopeAsync($"{prefix}-in-flight", projectId, runnerId, taskRunning: true);

        Assert.Empty((await Dispatch.PollAsync(
            runnerId,
            DispatchTestExtensions.ReadyPollRequest())).Dispatches);

        var ordinary = await LoadRunAsync($"{prefix}-ordinary");
        Assert.Equal(WorkflowRunStatus.Running, ordinary.Status);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, ordinary.CurrentStage().Tasks.Single().Status);
        var inFlight = await LoadRunAsync($"{prefix}-in-flight");
        Assert.Equal(WorkflowActionAttemptStatus.Running, inFlight.CurrentStage().Tasks.Single().Status);
    }

    private async Task RewriteAsLegacyRunningGapAsync(WorkflowRun run)
    {
        await DeactivateWorkflowAsync(run.Id);
        run.Status = WorkflowRunStatus.Running;

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowRuns.SingleAsync(candidate => candidate.WorkflowRunId == run.Id);
        row.State = JSON.Serialize(run);
        row.ActiveWorkId = null;
        row.ActiveWorkerId = null;
        await db.SaveChangesAsync();
    }

    private async Task InsertReconciliationEnvelopeAsync(
        string workflowRunId,
        string projectId,
        string runnerId,
        bool taskRunning)
    {
        var definition = SingleStage(checks: []);
        var run = WorkflowRun.Create(
            workflowRunId,
            definition,
            TestTime.UtcNow,
            new WorkflowRunMetadata(null, TestTime.UtcNow, ProjectId: projectId));
        run.BoundWorkflowDefinitionJson = WorkflowYamlSerializer.ToJson(definition);
        run.Start(TestTime.UtcNow);
        run.InitializeStage([new("task-1", "Task 1", "spec/task")], [], TestTime.UtcNow);
        run.AssignTo(runnerId, TestTime.UtcNow);
        if (taskRunning)
            run.StartTask("task-1.1", runnerId, TestRunnerGenerationExtensions.ProcessGeneration, TestTime.UtcNow);
        run.Status = WorkflowRunStatus.Running;

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
            ActiveWorkId = null,
            ActiveWorkerId = null,
        });
        await db.SaveChangesAsync();
    }

    private static WorkflowDefinition FeedbackWorkflow() => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            RequiresApproval: true),
    ],
    Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
        new TaskDefinition("apply-feedback", "Apply approval feedback", "spec/task"),
        new TaskDefinition("publish-feedback", "Publish approval feedback", "spec/task"),
    ])));
}
