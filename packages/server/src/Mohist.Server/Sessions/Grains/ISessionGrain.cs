namespace Mohist.Server.Sessions.Grains;

public interface ISessionGrain : IGrainWithStringKey
{
    Task<SessionSnapshot> EnsureAsync(EnsureSessionCommand command);
    Task<SessionSnapshot> AttachAgentAsync(AttachAgentCommand command);
    Task<IReadOnlyList<SessionEventSnapshot>> AppendEventsAsync(AppendSessionEventsCommand command);
    Task<SessionSnapshot?> FailIfRunningAsync(string reason);
    Task<SessionSnapshot?> GetAsync();
}

[GenerateSerializer]
public sealed record EnsureSessionCommand(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] int? IssueNumber,
    [property: Id(2)] string WorkflowRunId,
    [property: Id(3)] string SessionName,
    [property: Id(4)] string RunnerId,
    [property: Id(5)] string? WorkId = null,
    [property: Id(6)] string? WorkType = null,
    [property: Id(7)] string? Stage = null,
    [property: Id(8)] string? Title = null);

[GenerateSerializer]
public sealed record AttachAgentCommand(
    [property: Id(0)] string AgentSessionId,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? ChangeDir = null,
    [property: Id(4)] int? ProcessPid = null);

[GenerateSerializer]
public sealed record AppendSessionEventsCommand(
    [property: Id(0)] string? WorkId = null,
    [property: Id(1)] string? WorkType = null,
    [property: Id(2)] string? Stage = null,
    [property: Id(3)] IReadOnlyList<SessionEventInput> Events = null!);

[GenerateSerializer]
public sealed record SessionEventInput(
    [property: Id(0)] string Type,
    [property: Id(1)] string PayloadJson);

[GenerateSerializer]
public sealed record SessionSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string ProjectId,
    [property: Id(2)] int? IssueNumber,
    [property: Id(3)] string WorkflowRunId,
    [property: Id(4)] string SessionName,
    [property: Id(5)] string? WorkId,
    [property: Id(6)] string? WorkType,
    [property: Id(7)] string? Stage,
    [property: Id(8)] string? Title,
    [property: Id(9)] string? RunnerId,
    [property: Id(10)] string? AgentSessionId,
    [property: Id(11)] string Status,
    [property: Id(12)] string? Model,
    [property: Id(13)] string? WorkDir,
    [property: Id(14)] string? ChangeDir,
    [property: Id(15)] int? ProcessPid,
    [property: Id(16)] string CreatedAt,
    [property: Id(17)] string? StartedAt,
    [property: Id(18)] string? LastDataAt,
    [property: Id(19)] string? CompletedAt,
    [property: Id(20)] string? FailureReason,
    [property: Id(21)] int? ExitCode);

[GenerateSerializer]
public sealed record SessionEventSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string ProjectId,
    [property: Id(3)] int IssueNumber,
    [property: Id(4)] string WorkflowRunId,
    [property: Id(5)] string SessionName,
    [property: Id(6)] string? AgentSessionId,
    [property: Id(7)] string? WorkId,
    [property: Id(8)] string? WorkType,
    [property: Id(9)] string? Stage,
    [property: Id(10)] long Sequence,
    [property: Id(11)] string Type,
    [property: Id(12)] string PayloadJson,
    [property: Id(13)] string CreatedAt);