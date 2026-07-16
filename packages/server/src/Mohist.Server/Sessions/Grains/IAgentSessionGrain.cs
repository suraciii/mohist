using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public interface IAgentSessionGrain : IGrainWithStringKey
{
    Task<AgentSessionInfo> OpenAsync(OpenAgentSessionCommand command);
    Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command);
    Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command);
    Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendSystemEventsAsync(AppendAgentSessionSystemEventsCommand command);
    Task<AgentSessionRecoveryResult> CompactAsync(CompactAgentSessionCommand command);
    Task<AgentSessionRecoveryResult> ResetAsync(ResetAgentSessionCommand command);
    Task<AgentSessionRecoveryResult?> GetCompletedRecoveryAsync(SessionCommandKind command);
    Task<SessionCommandRequest> PrepareSessionCommandAsync(SessionCommandKind command);
    Task<SessionCommandRequest> BeginResetAsync();
    Task<AgentSessionRecoveryResult> CompleteCompactAsync(CompleteCompactAgentSessionCommand command);
    Task<AgentSessionRecoveryResult> CompleteResetAsync(CompleteResetAgentSessionCommand command);
    Task AbandonResetAsync(string operationId);
    Task<AgentSessionFollowupReservation> BeginFollowupAsync();
    Task ConfirmFollowupAsync(string operationId);
    Task AbandonFollowupAsync(string operationId);
    Task<AgentSessionInfo?> GetAsync();
    Task EnsureRuntimeSessionPresentAsync();

    /// <summary>
    /// Test-only hook: deactivates the grain so the next request
    /// re-hydrates state from the persistent store. Production code
    /// should rely on Orleans' normal collection cycle (the default
    /// 15-minute quiet window) instead of forcing deactivation.
    /// </summary>
    Task DeactivateForTestAsync();

    /// <summary>
    /// Test-only hook: flushes pending session state and transcript data
    /// without waiting for the grain timer tick.
    /// </summary>
    Task FlushForTestAsync();
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
    [property: Id(4)] int? ProcessPid = null,
    [property: Id(5)] string? Runtime = null);

[GenerateSerializer]
public sealed record AppendAgentSessionRuntimeEventsCommand(
    [property: Id(0)] IReadOnlyList<AgentSessionRuntimeEventInput> RuntimeEvents = null!,
    [property: Id(1)] string RuntimeSessionId = "");

[GenerateSerializer]
public sealed record AppendAgentSessionSystemEventsCommand(
    [property: Id(0)] IReadOnlyList<AgentSessionRuntimeEventInput> RuntimeEvents = null!);

[GenerateSerializer]
public sealed record CompactAgentSessionCommand(
    [property: Id(0)] string? Summary = null,
    [property: Id(1)] int? MaxSummaryChars = null);

[GenerateSerializer]
public sealed record ResetAgentSessionCommand(
    [property: Id(0)] string? ExpectedRuntimeSessionId,
    [property: Id(1)] string ReplacementRuntimeSessionId,
    [property: Id(2)] string ReplacementRuntime = "opencode");

[GenerateSerializer]
public sealed record CompleteResetAgentSessionCommand(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string ReplacementRuntimeSessionId,
    [property: Id(2)] string ReplacementRuntime);

[GenerateSerializer]
public sealed record CompleteCompactAgentSessionCommand(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string? Summary = null,
    [property: Id(2)] int? MaxSummaryChars = null);

[GenerateSerializer]
public sealed record AgentSessionFollowupReservation(
    [property: Id(0)] string? OperationId,
    [property: Id(1)] bool StartsIdleTurn = false);

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
    [property: Id(6)] string CreatedAt,
    [property: Id(7)] string? StartedAt,
    [property: Id(8)] string? LastDataAt,
    [property: Id(9)] string? ResolvedModel,
    [property: Id(10)] long? InputTokens,
    [property: Id(11)] long? OutputTokens,
    [property: Id(12)] long? TotalTokens,
    [property: Id(13)] long? CachedReadTokens,
    [property: Id(14)] long? ThoughtTokens,
    [property: Id(15)] double? CostAmount,
    [property: Id(16)] string? CostCurrency,
    [property: Id(17)] long? ContextWindowUsed,
    [property: Id(18)] long? ContextWindowSize,
    [property: Id(19)] string? FailureCategory,
    [property: Id(20)] int? ToolCallCount,
    [property: Id(21)] int? ToolErrorCount,
    [property: Id(22)] string? Runtime);

[GenerateSerializer]
public sealed record AgentSessionRecoveryResult(
    [property: Id(0)] string Id,
    [property: Id(2)] string Status,
    [property: Id(3)] long? ContextWindowSize,
    [property: Id(4)] long? ContextWindowUsed,
    [property: Id(5)] double? ContextUsagePercent,
    [property: Id(6)] long? ContextWindowUsedBefore,
    [property: Id(7)] string? Operation,
    [property: Id(8)] bool WasCompacted);

[GenerateSerializer]
public sealed record AgentSessionRuntimeEventInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string? RuntimeSessionId,
    [property: Id(3)] long Sequence,
    [property: Id(4)] string Type,
    [property: Id(5)] string PayloadJson,
    [property: Id(6)] string CreatedAt);
