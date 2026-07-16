namespace Mohist.Server.Sessions.Domain;

public union AgentSessionEvent(
    AgentSessionRuntimeBound,
    AgentSessionUsageRecorded,
    AgentSessionModelChanged,
    AgentSessionContextCompacted,
    AgentSessionContextExhausted,
    AgentSessionContextHealthUpdated);

/// <summary>
/// Emitted whenever the session binds to a (possibly new)
/// runtime session id. On the initial binding <see cref="PreviousAgentRuntimeSessionId"/>
/// is <c>null</c>; on a rebind after reset or another runtime-boundary change
/// it carries the predecessor runtime session id retained in
/// <see cref="AgentSessionStatusSnapshot.RuntimeSessionLineage"/>.
/// Realtime consumers use this to render a lineage link without
/// re-querying the session.
/// </summary>
public sealed record AgentSessionRuntimeBound(
    string AgentRuntimeSessionId,
    string? PreviousAgentRuntimeSessionId = null);
public sealed record AgentSessionUsageRecorded(AgentUsageSummary Usage);
public sealed record AgentSessionModelChanged(string? Model);
public sealed record AgentSessionContextCompacted(
    long? ContextWindowUsedBefore,
    long? ContextWindowUsedAfter,
    long? ContextWindowSize,
    string? Strategy,
    string? Summary,
    DateTime RecordedAt);

/// <summary>
/// Emitted when a session close event is classified as context
/// exhaustion (final usage ≥ 90% and the session ended in a failed
/// state). Carries the context usage percent at failure time so
/// downstream consumers (UI, workflow retry guard) can render
/// "Context window exhausted (94%)" messages and decide whether to
/// block retries.
/// </summary>
public sealed record AgentSessionContextExhausted(
    string? FailureCategory,
    double? ContextUsagePercent,
    long? ContextWindowUsed,
    long? ContextWindowSize,
    DateTime RecordedAt);

/// <summary>
/// Emitted whenever the session's context health status changes
/// (colour threshold crossing or &gt;=10pp swing). Carries the
/// green/yellow/red health status plus the current context window
/// metrics so frontend charts and warning banners can refresh
/// without re-querying the session.
/// </summary>
public sealed record AgentSessionContextHealthUpdated(
    string HealthStatus,
    double? ContextUsagePercent,
    long? ContextWindowUsed,
    long? ContextWindowSize,
    DateTime RecordedAt);
