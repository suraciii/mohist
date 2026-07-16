using Mohist.Server.Inbox;

namespace Mohist.Server.Notifications;

public sealed class HermesIssueNotificationRenderer
{
    public HermesIssueNotificationPayload Render(HermesIssueNotificationDraft draft)
    {
        var action = SuggestedAction(draft.NotificationType, draft.IssueNumber);
        var failureReason = NormalizeReason(draft.FailureReason);
        var body = draft.NotificationType switch
        {
            NotificationKinds.ApprovalRequested =>
                $"Issue #{draft.IssueNumber} needs approval at stage {draft.Stage ?? "unknown"}: {draft.IssueTitle}\nNext: {action}",
            NotificationKinds.WorkflowFailed =>
                $"Issue #{draft.IssueNumber} failed: {draft.IssueTitle}\nReason: {failureReason}\nNext: {action}",
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
            draft.IssueNumber,
            draft.IssueTitle,
            draft.WorkflowRunId,
            draft.Stage,
            draft.NotificationType == NotificationKinds.WorkflowFailed ? failureReason : draft.FailureReason,
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

    private static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "No failure reason was reported.";

        var safeLines = new List<string>();
        foreach (var line in reason.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (IsStackTraceLine(trimmed))
                break;

            safeLines.Add(trimmed);
        }

        return safeLines.Count == 0
            ? "Failure details were omitted because they only contained stack trace output."
            : string.Join(" ", safeLines);
    }

    private static bool IsStackTraceLine(string line) =>
        line.StartsWith("at ", StringComparison.Ordinal)
        || line.StartsWith("Stack trace:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Traceback ", StringComparison.Ordinal)
        || line.StartsWith("--- End of stack trace", StringComparison.Ordinal);
}
