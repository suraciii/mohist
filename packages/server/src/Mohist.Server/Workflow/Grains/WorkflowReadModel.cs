using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Read-side projections of <see cref="WorkflowRun"/> state, composed inside
/// the grain process to preserve the strong-consistency requirement (grain is
/// the consistency boundary for <see cref="WorkflowRun"/>). Extracted from
/// <see cref="WorkflowGrain"/> so the grain body stays focused on the state
/// machine — query snapshots no longer interleave with the write path.
/// </summary>
public sealed class WorkflowReadModel
{
    private readonly WorkflowGrain _owner;

    public WorkflowReadModel(WorkflowGrain owner)
    {
        _owner = owner;
    }

    public WorkflowActiveWorkView? GetActiveWork(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId)) return null;
        var run = _owner.RunOrNull;
        if (run is null) return null;

        var currentStage = run.CurrentStage();
        if (currentStage is null) return null;

        var activeTask = currentStage.RunningTask;
        var checksWorkId = currentStage.ChecksWorkId;
        if (!string.Equals(activeTask?.WorkId ?? checksWorkId, workId, StringComparison.Ordinal))
            return null;

        var projectId = _owner.GetProjectId();
        var issueId = _owner.GetIssueId();
        var stage = run.CurrentStageId ?? string.Empty;
        return new WorkflowActiveWorkView(
            WorkId: workId,
            WorkType: activeTask is not null ? "task" : "checks",
            Stage: stage,
            TaskRunId: activeTask?.Id ?? $"checks-{stage}",
            Title: activeTask?.Title ?? "Stage checks",
            ProjectId: string.IsNullOrWhiteSpace(projectId) ? null : projectId,
            IssueId: issueId,
            IssueNumber: ResolveIssueNumber());
    }

    /// <summary>
    /// Re-entry projection: returns the work item currently in flight on the
    /// current stage for the given runner, so a runner that lost its in-memory
    /// dispatch (e.g. after a process restart) can recover it by re-polling.
    /// Read-only — the task/check is already Running (claimed). Returns
    /// <c>null</c> when there is no in-flight work or it belongs to a
    /// different runner.
    /// </summary>
    public WorkItem? GetActiveWorkForRunner(WorkflowRun run, string runnerId)
    {
        var currentStage = run.CurrentStage();
        var runningTask = currentStage.RunningTask;
        if (runningTask is not null)
        {
            if (!string.Equals(runningTask.RunnerId, runnerId, StringComparison.Ordinal))
                return null;

            return WorkItem.Task(
                stage: currentStage.Id,
                id: runningTask.WorkId ?? runningTask.Id,
                title: runningTask.Title,
                uses: runningTask.Uses,
                with: runningTask.WithInput,
                artifacts: runningTask.Artifacts,
                setVars: runningTask.SetVars);
        }

        var checksWorkId = currentStage.ChecksWorkId;
        if (checksWorkId is null)
            return null;

        var pendingChecks = currentStage.Checks
            .Where(c => c.Status == StageCheckStatus.Pending)
            .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
            .ToList();
        return WorkItem.Checks(currentStage.Id, checksWorkId, pendingChecks);
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

    private int? ResolveIssueNumber()
    {
        var raw = _owner.GetIssueNumber();
        return int.TryParse(raw, out var number) ? number : null;
    }
}
