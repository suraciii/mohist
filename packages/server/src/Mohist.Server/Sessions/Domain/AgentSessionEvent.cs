namespace Mohist.Server.Sessions.Domain;

public union AgentSessionEvent(
    AgentSessionRuntimeBound,
    AgentSessionUsageRecorded,
    AgentSessionModelChanged,
    AgentSessionContextCompacted,
    AgentSessionContextExhausted,
    AgentSessionContextHealthUpdated,
    AgentSessionActivityConverged);

public sealed record AgentSessionRuntimeBound(
    string AgentRuntimeSessionId,
    string? Runtime = null);
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

/// <summary>
/// Durable Session-to-Job settlement fact emitted with the transition that
/// superseded Job-owned Turns under lifecycle evidence. The Job side
/// arbitrates idempotently against this fact; the Session never awaits a Job
/// that calls back into it. A Job whose Turn was not settled emits nothing,
/// so a completed old initial Turn produces no convergence fact.
/// </summary>
public sealed record AgentSessionActivityConverged(
    string SessionId,
    string Observation,
    long ContextGeneration,
    long BindingEpoch,
    IReadOnlyList<string> SettledTurnIds,
    IReadOnlyList<string> SettledJobIds,
    IReadOnlyList<string> SupersededOperationIds,
    DateTime RecordedAt);
