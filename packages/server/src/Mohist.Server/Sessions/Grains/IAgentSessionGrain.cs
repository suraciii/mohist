using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public interface IAgentSessionGrain : IGrainWithStringKey
{
    Task<AgentSessionInfo> OpenAsync(OpenAgentSessionCommand command);
    Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command);
    Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command);
    Task<AgentSessionInfo?> GetAsync();
}

[GenerateSerializer]
public sealed record OpenAgentSessionCommand(
    [property: Id(0)] string RunnerId,
    [property: Id(1)] string AgentRuntime,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? Model = null,
    [property: Id(4)] AgentSessionMetadata? Metadata = null);

[GenerateSerializer]
public sealed record AttachPhysicalSessionCommand(
    [property: Id(0)] string AgentSessionId,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? ChangeDir = null,
    [property: Id(4)] int? ProcessPid = null);

[GenerateSerializer]
public sealed record AppendAgentSessionRuntimeEventsCommand(
    [property: Id(0)] IReadOnlyList<AgentSessionRuntimeEventInput> RuntimeEvents = null!);

[GenerateSerializer]
public sealed record AgentSessionRuntimeEventInput(
    [property: Id(0)] string Type,
    [property: Id(1)] string PayloadJson);

[GenerateSerializer]
public sealed record AgentSessionInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string? RunnerId,
    [property: Id(2)] string? AgentSessionId,
    [property: Id(3)] string Status,
    [property: Id(4)] string? Model,
    [property: Id(5)] string? WorkDir,
    [property: Id(6)] string? ChangeDir,
    [property: Id(7)] int? ProcessPid,
    [property: Id(8)] string CreatedAt,
    [property: Id(9)] string? StartedAt,
    [property: Id(10)] string? LastDataAt,
    [property: Id(11)] string? CompletedAt,
    [property: Id(12)] string? FailureReason,
    [property: Id(13)] int? ExitCode,
    [property: Id(14)] string? ResolvedModel,
    [property: Id(15)] long? InputTokens,
    [property: Id(16)] long? OutputTokens,
    [property: Id(17)] long? TotalTokens,
    [property: Id(18)] long? CachedReadTokens,
    [property: Id(19)] long? ThoughtTokens,
    [property: Id(20)] double? CostAmount,
    [property: Id(21)] string? CostCurrency,
    [property: Id(22)] long? ContextWindowUsed,
    [property: Id(23)] long? ContextWindowSize,
    [property: Id(24)] string? FailureCategory,
    [property: Id(25)] int? ToolCallCount,
    [property: Id(26)] int? ToolErrorCount);

[GenerateSerializer]
public sealed record AgentSessionRuntimeEventInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string? AgentSessionId,
    [property: Id(3)] long Sequence,
    [property: Id(4)] string Type,
    [property: Id(5)] string PayloadJson,
    [property: Id(6)] string CreatedAt);
