using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Sessions.Grains;

public interface IAgentSessionGrain : IGrainWithStringKey
{
    Task<AgentSessionSnapshot?> EnsureCreatedAsync(EnsureAgentSessionCommand command);
    Task<AgentSessionSnapshot?> MarkStartedAsync(AgentSessionStartedCommand command);
    Task<IReadOnlyList<AgentSessionEventSnapshot>> AppendEventsAsync(IReadOnlyList<AgentSessionEventInput> events);
    Task<AgentSessionSnapshot?> MarkStatusAsync(AgentSessionStatusCommand command);
    Task<AgentSessionSnapshot?> MarkCompletedAsync(AgentSessionCompletedCommand command);
    Task<AgentSessionSnapshot?> FailIfRunningAsync(string reason);
    Task<AgentSessionSnapshot?> GetAsync();
}

public static class AgentSessionGrainKeys
{
    public static string ForWork(string workflowRunId, string workId) => $"as:{workflowRunId}:{workId}";
}

[GenerateSerializer]
public sealed record EnsureAgentSessionCommand(
    [property: Id(0)] string RunnerId,
    [property: Id(1)] WorkDispatch Dispatch);

[GenerateSerializer]
public sealed record AgentSessionStartedCommand(
    [property: Id(0)] string? ExternalSessionId = null,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? ChangeDir = null,
    [property: Id(4)] int? ProcessPid = null);

[GenerateSerializer]
public sealed record AgentSessionEventInput(
    [property: Id(0)] string Type,
    [property: Id(1)] string PayloadJson);

[GenerateSerializer]
public sealed record AgentSessionStatusCommand(
    [property: Id(0)] string Status,
    [property: Id(1)] DateTime? LastDataAt = null,
    [property: Id(2)] string? FailureReason = null);

[GenerateSerializer]
public sealed record AgentSessionCompletedCommand(
    [property: Id(0)] string Status,
    [property: Id(1)] string? FailureReason = null,
    [property: Id(2)] int? ExitCode = null);

[GenerateSerializer]
public sealed record AgentSessionSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string ProjectId,
    [property: Id(2)] int IssueNumber,
    [property: Id(3)] string WorkflowRunId,
    [property: Id(4)] string WorkId,
    [property: Id(5)] string WorkType,
    [property: Id(6)] string? Stage,
    [property: Id(7)] string? Title,
    [property: Id(8)] string RunnerId,
    [property: Id(9)] string? ExternalSessionId,
    [property: Id(10)] string Status,
    [property: Id(11)] string? Model,
    [property: Id(12)] string? WorkDir,
    [property: Id(13)] string? ChangeDir,
    [property: Id(14)] int? ProcessPid,
    [property: Id(15)] string CreatedAt,
    [property: Id(16)] string? StartedAt,
    [property: Id(17)] string? CompletedAt,
    [property: Id(18)] string? LastDataAt,
    [property: Id(19)] string? FailureReason,
    [property: Id(20)] int? ExitCode);

[GenerateSerializer]
public sealed record AgentSessionEventSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string ProjectId,
    [property: Id(3)] int IssueNumber,
    [property: Id(4)] string WorkflowRunId,
    [property: Id(5)] string WorkId,
    [property: Id(6)] long Sequence,
    [property: Id(7)] string Type,
    [property: Id(8)] string PayloadJson,
    [property: Id(9)] string CreatedAt);
