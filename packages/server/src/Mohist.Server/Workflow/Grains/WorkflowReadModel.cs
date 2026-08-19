using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

internal sealed class WorkflowReadModel
{
    private readonly IWorkflowGrainContext _owner;

    public WorkflowReadModel(IWorkflowGrainContext owner)
    {
        _owner = owner;
    }

    public WorkflowActiveWorkView? GetActiveWork(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId)) return null;
        var run = _owner.RunOrNull;
        if (run is null) return null;
        // A blocked Agent settlement has released its active-work lease; the
        // attempt must not be presented as active work after the deadline.
        if (run.HasBlockedAgentResult()) return null;

        var currentStage = run.CurrentStage();
        if (currentStage is null) return null;

        var activeTask = currentStage.RunningTask;
        var checksWorkId = currentStage.ChecksWorkId;
        if (!string.Equals(activeTask?.WorkId ?? checksWorkId, workId, StringComparison.Ordinal))
            return null;

        var projectId = _owner.GetProjectId();
        var stage = run.CurrentStageId ?? string.Empty;
        return new WorkflowActiveWorkView(
            WorkId: workId,
            WorkType: activeTask is not null ? "task" : "checks",
            Stage: stage,
            TaskRunId: activeTask?.Id ?? WorkflowRunExtensions.ChecksWorkIdFor(stage),
            Title: activeTask?.Title ?? "Stage checks",
            ProjectId: string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            IssueNumber: _owner.GetIssueNumber());
    }

    public WorkflowFeedbackRecord? GetFeedback(string feedbackId)
    {
        var run = _owner.RunOrNull;
        if (run is null) return null;
        if (string.IsNullOrWhiteSpace(feedbackId)) return null;

        var feedback = run.Feedback.FirstOrDefault(f => string.Equals(f.Id, feedbackId, StringComparison.Ordinal));
        if (feedback is null) return null;

        return ToSnapshot(feedback);
    }

    public IReadOnlyList<WorkflowFeedbackRecord> ListFeedback()
    {
        var run = _owner.RunOrNull;
        if (run is null) return Array.Empty<WorkflowFeedbackRecord>();
        var issueNumber = ResolveIssueNumber();
        return run.Feedback
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => ToSnapshot(f, issueNumber))
            .ToList();
    }

    private WorkflowFeedbackRecord ToSnapshot(ApprovalFeedback feedback) =>
        ToSnapshot(feedback, ResolveIssueNumber());

    private static WorkflowFeedbackRecord ToSnapshot(ApprovalFeedback feedback, int? issueNumber) =>
        new(
            Id: feedback.Id,
            WorkflowRunId: feedback.WorkflowRunId,
            Stage: feedback.Stage,
            Body: feedback.Body,
            Status: feedback.Status,
            CreatedAt: feedback.CreatedAt,
            Resolution: ToResolution(feedback),
            IssueNumber: issueNumber);

    private static WorkflowFeedbackResolution? ToResolution(ApprovalFeedback feedback) =>
        feedback.Status == ApprovalFeedbackStatus.Resolved
            ? new WorkflowFeedbackResolution(
                ResolutionTaskId: feedback.ResolutionTaskId,
                ResolvedAt: feedback.ResolvedAt,
                ResolutionSummary: feedback.ResolutionSummary)
            : null;

    private int? ResolveIssueNumber() => _owner.GetIssueNumber();
}
