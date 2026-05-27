using System.Text.Json;

namespace Mohist.Server.Workflow.Sessions.Queries;

public sealed record WorkflowSessionDto(
    string Id,
    string WorkflowRunId,
    string SessionName,
    string? AcpSessionId,
    string? ProjectId,
    int? IssueNumber,
    string? RunnerId,
    string Status,
    string? Model,
    string? WorkDir,
    int? ProcessPid,
    string CreatedAt,
    string? StartedAt,
    string? LastDataAt,
    string? CompletedAt,
    string? FailureReason,
    int? ExitCode);

public sealed record WorkflowSessionEventDto(
    string Id,
    string WorkflowSessionId,
    string WorkflowRunId,
    string SessionName,
    string? AcpSessionId,
    string? ProjectId,
    int? IssueNumber,
    string? WorkId,
    string? WorkType,
    string? Stage,
    long Sequence,
    string Type,
    object? Payload,
    string CreatedAt);

public sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, IReadOnlyList<WorkflowSessionEventDto> Events);
