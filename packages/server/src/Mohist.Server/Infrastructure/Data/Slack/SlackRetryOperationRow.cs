using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Infrastructure.Data.Slack;

public sealed class SlackRetryOperationRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ActionKey { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string FailedInputId { get; set; } = string.Empty;
    public string FailedTurnId { get; set; } = string.Empty;
    public string DispatchRef { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string MessageTs { get; set; } = string.Empty;
    public string? ThreadTs { get; set; }
    public bool OriginalDirectMessage { get; set; }
    public string ActorSlackUserId { get; set; } = string.Empty;
    public string RetryDispatchKey { get; set; } = string.Empty;
    public string AttemptKind { get; set; } = string.Empty;
    public string? PreMintedSessionId { get; set; }
    public string? PreMintedInputId { get; set; }
    public string? PreMintedTurnId { get; set; }
    public string? FollowupOperationId { get; set; }
    public string State { get; set; } = SlackRetryOperationStates.DispatchPending;
    public string? Outcome { get; set; }
    public string? ResultSessionId { get; set; }
    public string? ResultInputId { get; set; }
    public string? ResultTurnId { get; set; }
    public string? ResultReason { get; set; }
    public string? RecoveryLeaseId { get; set; }
    public DateTimeOffset? RecoveryLeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SlackRetryOperationStates
{
    public const string DispatchPending = "dispatch-pending";
    public const string Completed = "completed";
}

public static class SlackRetryOperationOutcomes
{
    public const string Accepted = "accepted";
    public const string AlreadyApplied = "already_applied";
    public const string Stale = "stale";
    public const string Unavailable = "unavailable";
}

public sealed record SlackRetryOperationDraft(
    string ProjectId,
    string ActionKey,
    string ConnectionId,
    string SessionId,
    string FailedInputId,
    string FailedTurnId,
    string DispatchRef,
    SlackMessageIdentity Source,
    string? ThreadTs,
    bool OriginalDirectMessage,
    string ActorSlackUserId,
    string RetryDispatchKey,
    string AttemptKind,
    string? PreMintedSessionId,
    string? PreMintedInputId,
    string? PreMintedTurnId,
    string? FollowupOperationId);

public sealed record SlackRetryOperationClaimResult(
    SlackRetryOperationRow Operation,
    bool Created);
