using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Domain;

public static class MohistDefaultWorkflowProjection
{
    public static MohistDefaultWorkflowState Project(
        int issueNumber,
        string issueTitle,
        string issueStatus,
        IssueAttention? issueAttention,
        string? fallbackBlockedReason,
        WorkflowStatusSnapshot? workflow)
    {
        var approval = workflow?.Stages
            .Select(s => s.Approval is null ? null : new ApprovalState
            {
                Stage = s.Stage,
                Status = s.Approval.Status,
                OutputJson = s.Approval.Output,
                RequestedAt = s.Approval.RequestedAt,
                RespondedAt = s.Approval.RespondedAt,
            })
            .Where(a => a is not null)
            .LastOrDefault();
        var attention = ProjectAttention(issueAttention, workflow);
        if (workflow is null)
        {
            return new MohistDefaultWorkflowState(
                issueStatus,
                RuntimeStatus(issueStatus, attention),
                fallbackBlockedReason,
                null,
                attention,
                ChangeDir(issueNumber),
                issueStatus == "done");
        }

        var projectedStatus = workflow.Status == "Completed" ? "done" : issueStatus;

        return new MohistDefaultWorkflowState(
            projectedStatus,
            RuntimeStatus(projectedStatus, attention, workflow.Status),
            attention?.Message ?? (workflow.Status == "Failed" ? workflow.Failure?.Message : fallbackBlockedReason),
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

    private static string RuntimeStatus(string issueStatus, IssueAttention? attention, string? workflowStatus = null)
    {
        if (issueStatus == "done") return "completed";
        if (issueStatus == "cancelled") return "cancelled";
        if (attention?.Reason is IssueAttentionReasons.Blocked or IssueAttentionReasons.WorkflowFailed) return "blocked";
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
}

public sealed record MohistDefaultWorkflowState(
    string IssueStatus,
    string RuntimeStatus,
    string? BlockedReason,
    ApprovalState? ApprovalState,
    IssueAttention? Attention,
    string ChangeDir,
    bool Completed);
