using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Issue.WorkflowProfiles;

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
        var approval = workflow?.Stages
            .Select(s => s.ApprovalStatus is null ? null : new StageApproval
            {
                Stage = s.Stage,
                Status = s.ApprovalStatus.Result ?? "awaiting",
                RequestedAt = ParseDateTime(s.ApprovalStatus.RequestedAt),
                RespondedAt = ParseNullableDateTime(s.ApprovalStatus.RespondedAt),
            })
            .Where(a => a is not null)
            .LastOrDefault();
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
            RuntimeStatus(issueStage, attention, workflow.Status, workflow.ClaimedBy),
            ComputeBlockedReason(attention, workflow),
            approval,
            attention,
            ChangeDir(issueNumber),
            workflow.Status == "Completed");
    }

    private static WorkflowAttention? ProjectAttention(WorkflowStatusView? workflow)
    {
        if (workflow?.Status == "AwaitingApproval")
            return WorkflowAttention.ReviewRequired(workflow.WorkflowRunId, $"Awaiting approval for {workflow.CurrentStage ?? "workflow"}");
        if (workflow?.Status == "Failed")
            return WorkflowAttention.Blocked(workflow.WorkflowRunId, workflow.Failure?.Message ?? "Workflow failed");
        return null;
    }

    private static string? ComputeBlockedReason(WorkflowAttention? attention, WorkflowStatusView? workflow) =>
        attention?.Message
        ?? (workflow?.Status == "Failed" ? workflow.Failure?.Message : null);

    private static string RuntimeStatus(string issueStatus, WorkflowAttention? attention, string? workflowStatus = null, string? claimedBy = null)
    {
        if (issueStatus == "done") return "done";
        if (issueStatus == "cancelled") return "cancelled";
        if (attention?.Reason is WorkflowAttentionReason.Blocked or WorkflowAttentionReason.WorkflowFailed) return "blocked";
        if (attention is not null) return "attention";
        return workflowStatus switch
        {
            "Running" when claimedBy is null => "queued",
            "Paused" => "paused",
            "Failed" => "blocked",
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
