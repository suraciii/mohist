using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Domain;

public static class MohistDefaultWorkflowProjection
{
    public static MohistDefaultWorkflowState Project(
        int issueNumber,
        string issueTitle,
        string fallbackStage,
        string fallbackStatus,
        string? fallbackBlockedReason,
        WorkflowStatusSnapshot? workflow)
    {
        if (workflow is null)
        {
            return new MohistDefaultWorkflowState(
                fallbackStage,
                fallbackStatus,
                fallbackBlockedReason,
                null,
                ChangeDir(issueNumber, issueTitle),
                fallbackStatus == "completed" || fallbackStage == "done");
        }

        var stage = workflow.Status == "Passed" ? "done" : workflow.CurrentStage ?? fallbackStage;
        var runtimeStatus = workflow.Status switch
        {
            "Passed" => "completed",
            "Failed" => "blocked",
            "Paused" => "paused",
            _ => "active",
        };

        var approval = workflow.Stages
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

        return new MohistDefaultWorkflowState(
            stage,
            runtimeStatus,
            workflow.Status == "Failed" ? workflow.Failure?.Message : fallbackBlockedReason,
            approval,
            ChangeDir(issueNumber, issueTitle),
            workflow.Status == "Passed");
    }

    public static string ChangeDir(int issueNumber, string issueTitle) =>
        $"openspec/changes/{issueNumber}-{Slug(issueTitle)}";

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "issue" : slug;
    }
}

public sealed record MohistDefaultWorkflowState(
    string Stage,
    string RuntimeStatus,
    string? BlockedReason,
    ApprovalState? ApprovalState,
    string ChangeDir,
    bool Completed);
