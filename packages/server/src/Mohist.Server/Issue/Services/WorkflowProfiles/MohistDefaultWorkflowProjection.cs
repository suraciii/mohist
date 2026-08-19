using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public static class MohistDefaultWorkflowProjection
{
    public static string IssueStatusName(IssueStatus status) => status switch
    {
        IssueStatus.InProgress => "in_progress",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string Health(IssueStatus issueStatus) =>
        RuntimeStatus(IssueStatusName(issueStatus), null, null);

    public static MohistDefaultWorkflowState ProjectWorkflowState(
        int issueNumber,
        string issueTitle,
        IssueStatus issueStatus,
        WorkflowStatusView? workflow)
    {
        var issueStage = IssueStatusName(issueStatus);
        return ProjectWorkflowState(issueNumber, issueTitle, issueStage, workflow);
    }

    public static MohistDefaultWorkflowState ProjectWorkflowState(
        int issueNumber,
        string issueTitle,
        string issueStage,
        WorkflowStatusView? workflow)
    {
        var approval = StageApprovals(workflow).LastOrDefault();
        var attention = ProjectAttention(workflow);
        if (workflow is null)
        {
            return new MohistDefaultWorkflowState(
                issueStage,
                RuntimeStatus(issueStage, attention),
                ComputeBlockedReason(attention, null),
                null,
                attention,
                ChangeDir(issueNumber),
                issueStage == "done");
        }

        return new MohistDefaultWorkflowState(
            issueStage,
            RuntimeStatus(issueStage, attention, workflow.Status, workflow.AssignedTo),
            ComputeBlockedReason(attention, workflow),
            approval,
            attention,
            ChangeDir(issueNumber),
            workflow.Status == "completed");
    }

    public static IEnumerable<StageApproval> StageApprovals(WorkflowStatusView? workflow)
    {
        if (workflow is null) yield break;

        foreach (var stage in workflow.Stages)
        {
            var approval = stage.ApprovalStatus;
            if (approval is null) continue;

            yield return new StageApproval
            {
                Stage = stage.Stage,
                Status = approval.Result ?? "awaiting",
                RequestedAt = ParseDateTime(approval.RequestedAt),
                RespondedAt = ParseNullableDateTime(approval.RespondedAt),
            };
        }
    }

    private static WorkflowAttention? ProjectAttention(WorkflowStatusView? workflow)
    {
        var interruption = FindInterruption(workflow);
        if (interruption is not null)
            return WorkflowAttention.RecoverableInterrupted(workflow!.WorkflowRunId, interruption);

        if (workflow?.Status == "awaiting-approval")
            return WorkflowAttention.ReviewRequired(workflow.WorkflowRunId, $"Awaiting approval for {workflow.CurrentStage ?? "workflow"}");
        if (workflow?.Status == "failed")
            return WorkflowAttention.Blocked(workflow.WorkflowRunId, workflow.Failure?.Message ?? "Workflow failed");
        if (workflow?.Status == "blocked")
            return WorkflowAttention.AgentResultUnconfirmed(
                workflow.WorkflowRunId,
                workflow.AgentResultAttention?.Message ?? "Agent result unconfirmed",
                workflow.AgentResultAttention?.ReasonCode,
                workflow.AgentResultAttention?.DeadlineAt);
        return null;
    }

    private static WorkInterruptionView? FindInterruption(WorkflowStatusView? workflow)
    {
        if (workflow is null) return null;
        if (workflow.Interruption is not null) return workflow.Interruption;

        foreach (var stage in workflow.Stages)
        {
            if (stage.Interruption is not null) return stage.Interruption;
            var taskInterruption = stage.Tasks
                .Select(task => task.Interruption)
                .FirstOrDefault(interruption => interruption is not null);
            if (taskInterruption is not null) return taskInterruption;
            var checkInterruption = stage.Checks
                .Select(check => check.Interruption)
                .FirstOrDefault(interruption => interruption is not null);
            if (checkInterruption is not null) return checkInterruption;
        }

        return null;
    }

    private static string? ComputeBlockedReason(WorkflowAttention? attention, WorkflowStatusView? workflow) =>
        attention?.Message
        ?? (workflow?.Status == "failed" ? workflow.Failure?.Message : null);

    private static string RuntimeStatus(string issueStatus, WorkflowAttention? attention, string? workflowStatus = null, string? assignedBy = null)
    {
        if (issueStatus == "done") return "done";
        if (issueStatus == "cancelled") return "cancelled";
        if (attention?.Reason is WorkflowAttentionReason.Blocked
            or WorkflowAttentionReason.AgentResultUnconfirmed
            or WorkflowAttentionReason.WorkflowFailed) return "blocked";
        if (attention is not null) return "attention";
        return workflowStatus switch
        {
            "running" when assignedBy is null => "queued",
            "paused" => "paused",
            "failed" => "blocked",
            _ => "active",
        };
    }

    public static string ChangeName(int issueNumber) => $"issue-{issueNumber}";

    public static string ChangeDir(int issueNumber) => $"openspec/changes/{ChangeName(issueNumber)}";

    private static DateTime ParseDateTime(string value) =>
        DateTime.TryParse(value, out var result) ? result : DateTime.UtcNow;

    private static DateTime? ParseNullableDateTime(string? value) =>
        value is not null && DateTime.TryParse(value, out var result) ? result : null;
}

public sealed record MohistDefaultWorkflowState(
    string IssueStatus,
    string Health,
    string? BlockedReason,
    StageApproval? StageApproval,
    WorkflowAttention? Attention,
    string ChangeDir,
    bool Completed);
