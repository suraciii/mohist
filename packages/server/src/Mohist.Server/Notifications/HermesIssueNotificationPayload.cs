namespace Mohist.Server.Notifications;

public sealed record HermesIssueNotificationPayload(
    string NotificationType,
    string EventType,
    string SourceEventId,
    DateTimeOffset OccurredAt,
    string ProjectId,
    int IssueNumber,
    int? EpicNumber,
    string IssueTitle,
    string? WorkflowRunId,
    string? Stage,
    string? FailureReason,
    string SuggestedAction,
    string Body);

public sealed record HermesIssueNotificationDraft(
    string NotificationType,
    string EventType,
    string SourceEventId,
    DateTimeOffset OccurredAt,
    string ProjectId,
    int IssueNumber,
    int? EpicNumber,
    string IssueTitle,
    string? WorkflowRunId,
    string? Stage,
    string? FailureReason);
