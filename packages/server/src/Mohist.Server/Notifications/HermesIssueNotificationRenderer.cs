using Mohist.Server.Inbox;

namespace Mohist.Server.Notifications;

public sealed class HermesIssueNotificationRenderer
{
    public HermesIssueNotificationPayload Render(HermesIssueNotificationDraft draft)
    {
        var action = SuggestedAction(draft.NotificationType, draft.IssueNumber);
        var body = draft.NotificationType switch
        {
            NotificationKinds.ApprovalRequested =>
                $"Issue #{draft.IssueNumber} needs approval at stage {draft.Stage ?? "unknown"}: {draft.IssueTitle}\nNext: {action}",
            NotificationKinds.WorkflowFailed =>
                $"Issue #{draft.IssueNumber} failed: {draft.IssueTitle}\nReason: {NormalizeReason(draft.FailureReason)}\nNext: {action}",
            NotificationKinds.IssueCompleted =>
                $"Issue #{draft.IssueNumber} completed: {draft.IssueTitle}\nNext: {action}",
            NotificationKinds.IssueStarted =>
                $"Issue #{draft.IssueNumber} started: {draft.IssueTitle}\nNext: {action}",
            _ => throw new InvalidOperationException($"Unsupported Hermes notification type '{draft.NotificationType}'"),
        };

        return new HermesIssueNotificationPayload(
            draft.NotificationType,
            draft.EventType,
            draft.SourceEventId,
            draft.OccurredAt,
            draft.ProjectId,
            draft.IssueId,
            draft.IssueNumber,
            draft.IssueTitle,
            draft.WorkflowRunId,
            draft.Stage,
            draft.FailureReason,
            action,
            body);
    }

    private static string SuggestedAction(string notificationType, int issueNumber) => notificationType switch
    {
        NotificationKinds.ApprovalRequested => $"approve {issueNumber}",
        NotificationKinds.WorkflowFailed => $"retry {issueNumber} or abandon {issueNumber}",
        NotificationKinds.IssueCompleted => $"review issue {issueNumber}",
        NotificationKinds.IssueStarted => $"open issue {issueNumber}",
        _ => $"open issue {issueNumber}",
    };

    private static string NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "No failure reason was reported." : reason.Trim();
}
