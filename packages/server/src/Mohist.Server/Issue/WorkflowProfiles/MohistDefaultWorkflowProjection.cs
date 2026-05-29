using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.WorkflowProfiles;

public static class MohistDefaultWorkflowProjection
{
    public static MohistDefaultWorkflowState ProjectWorkflowState(
        int issueNumber,
        string issueTitle,
        string issueStage,
        IssueAttention? issueAttention,
        WorkflowStatusSnapshot? workflow)
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
        var attention = ProjectAttention(issueAttention, workflow);
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
            RuntimeStatus(issueStage, attention, workflow.Status),
            ComputeBlockedReason(attention, workflow),
            approval,
            attention,
            ChangeDir(issueNumber),
            workflow.Status == "Completed");
    }

    private static IssueAttention? ProjectAttention(IssueAttention? issueAttention, WorkflowStatusSnapshot? workflow)
    {
        if (workflow?.Status == "AwaitingApproval")
            return IssueAttention.ReviewRequired(workflow.WorkflowRunId, $"Awaiting approval for {workflow.CurrentStage ?? "workflow"}");
        if (workflow?.Status == "Failed")
            return IssueAttention.Blocked(workflow.WorkflowRunId, workflow.Failure?.Message ?? "Workflow failed");
        return issueAttention;
    }

    private static string? ComputeBlockedReason(IssueAttention? attention, WorkflowStatusSnapshot? workflow) =>
        attention?.Message
        ?? (workflow?.Status == "Failed" ? workflow.Failure?.Message : null);

    private static string RuntimeStatus(string issueStatus, IssueAttention? attention, string? workflowStatus = null)
    {
        if (issueStatus == "done") return "done";
        if (issueStatus == "cancelled") return "cancelled";
        if (attention?.Reason is IssueAttentionReason.Blocked or IssueAttentionReason.WorkflowFailed) return "blocked";
        if (attention is not null) return "attention";
        return workflowStatus switch
        {
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
    string IssueStage,
    string RuntimeStatus,
    string? BlockedReason,
    StageApproval? StageApproval,
    IssueAttention? Attention,
    string ChangeDir,
    bool Completed);