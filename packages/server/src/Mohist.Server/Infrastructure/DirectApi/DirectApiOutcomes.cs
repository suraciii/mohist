namespace Mohist.Server.Infrastructure.DirectApi;

public sealed record DirectApiLaunchOutcome(
    string CoordinatorKey,
    string? JobId = null,
    string? SessionId = null,
    string? InputId = null,
    string? TurnId = null,
    string? RejectionCode = null,
    string? RejectionReason = null);

public sealed record DirectApiFollowupOutcome(
    string ProjectId,
    string SessionId,
    string? AgentId,
    string? InputId = null,
    string? TurnId = null,
    string? RejectionCode = null,
    string? RejectionReason = null,
    string? SnapshotJson = null);

public sealed record DirectApiStopOutcome(
    string ProjectId,
    string SessionId,
    string TurnId,
    string OperationId);

public sealed record DirectApiFrozenStopTarget(
    string ProjectId,
    string SessionId,
    string TurnId,
    long TurnRevision,
    long ContextGeneration,
    DirectApiFrozenStopBinding Binding,
    DateTimeOffset? DeadlineAt,
    string OperationId);

public sealed record DirectApiFrozenStopBinding(
    string? RunnerId,
    string? SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir);
