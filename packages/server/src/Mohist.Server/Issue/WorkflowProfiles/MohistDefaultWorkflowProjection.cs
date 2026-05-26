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
        string? fallbackBlockedReason,
        WorkflowStatusSnapshot? workflow)
    {
        var approval = workflow?.Stages
            .Select(s => s.Approval is null ? null : new StageApproval
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
                issueStage,
                RuntimeStatus(issueStage, attention),
                fallbackBlockedReason,
                null,
                attention,
                ChangeDir(issueNumber),
                issueStage == "done");
        }

        var projectedStage = workflow.Status == "Completed" ? "done" : issueStage;

        return new MohistDefaultWorkflowState(
            projectedStage,
            RuntimeStatus(projectedStage, attention, workflow.Status),
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
    string IssueStage,
    string RuntimeStatus,
    string? BlockedReason,
    StageApproval? StageApproval,
    IssueAttention? Attention,
    string ChangeDir,
    bool Completed);
