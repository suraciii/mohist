using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

public partial class DispatchServiceReconciliationSpecs
{
    [Fact]
    public async Task PollAsync_FeedbackTaskCompletion_ClaimsNextFeedbackTaskExactlyOnce()
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

        var publish = Assert.Single((await Dispatch.PollAsync(
            runnerId,
            DispatchTestExtensions.ReadyPollRequest())).Dispatches);
        Assert.Equal(_workflowId, publish.WorkflowRunId);
        Assert.Equal("publish-feedback.1", publish.ActionAttemptId);

        var eventsAfterClaim = await EventStore.ListAsync(_workflowId!);
        Assert.Single(eventsAfterClaim, evt =>
            evt.Envelope.Type == EventCatalog.ReverseDns.TaskStarted
            && evt.Envelope.Data?.ToString().Contains("publish-feedback.1", StringComparison.Ordinal) == true);

        var reported = DispatchTestExtensions.ReadyPollRequest() with
        {
            InFlight = [WorkKey(_workflowId!, publish.WorkId)],
        };
        Assert.Empty((await Dispatch.PollAsync(runnerId, reported)).Dispatches);
        Assert.Equal(eventsAfterClaim.Count, (await EventStore.ListAsync(_workflowId!)).Count);

        await ReportAsync(runnerId, publish, "completed");

        var resolved = await LoadRunAsync(_workflowId!);
        Assert.Equal(ApprovalFeedbackStatus.Resolved, resolved.Feedback.Single(item => item.Id == feedbackId).Status);
        Assert.Equal(2, resolved.CurrentStage().Attempt);
        Assert.Equal(approvalRequestCount, (await EventStore.ListAsync(_workflowId!))
            .Count(evt => evt.Envelope.Type == EventCatalog.ReverseDns.StageApprovalRequested));
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
