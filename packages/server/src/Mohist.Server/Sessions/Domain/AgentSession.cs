using System.Text.Json.Serialization;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Sessions.Domain;

public sealed class AgentSession
{
    public required string Id { get; init; }
    public AgentSessionMetadata Metadata { get; set; } = new();
    public required AgentSessionRuntime Runtime { get; set; }
    public AgentSessionSettings Settings { get; set; } = new();
    [JsonInclude]
    [JsonPropertyName("activitySummary")]
    internal AgentSessionActivitySummaryState PersistedActivitySummary { get; set; } = AgentSessionActivitySummaryState.Empty;
    [JsonIgnore]
    public AgentSessionTranscriptSummary ActivitySummary => PersistedActivitySummary.Summary;
    public AgentSessionStatusSnapshot Status { get; set; } = AgentSessionStatusSnapshot.Created(DateTime.UtcNow);

    public static AgentSession Create(
        string id,
        string runnerId,
        string? workDir,
        AgentSessionMetadata? metadata = null,
        DateTime? now = null,
        string? runtime = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        var session = new AgentSession
        {
            Id = id,
            Metadata = metadata ?? new AgentSessionMetadata(),
            Runtime = new AgentSessionRuntime(runnerId, workDir, NormalizeRuntime(runtime)),
            Settings = new AgentSessionSettings(),
            Status = AgentSessionStatusSnapshot.Created(createdAt)
        };
        session.ValidateState();
        return session;
    }

    private static string? NormalizeRuntime(string? runtime) =>
        string.IsNullOrWhiteSpace(runtime) ? null : runtime.Trim();

    public void ValidateState(bool allowLegacySource = false)
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("AgentSession state requires a non-empty Id.");
        if (Runtime is null)
            throw new InvalidOperationException("AgentSession state requires a Runtime.");
        if (Status.CreatedAt == default)
            throw new InvalidOperationException("AgentSession state requires CreatedAt to be set.");
        PersistedActivitySummary = (PersistedActivitySummary ?? AgentSessionActivitySummaryState.Empty).Normalize();
        Metadata.ValidateSource(allowLegacySource);
    }
}

[Serializable]
[GenerateSerializer]
public sealed class RuntimeSessionMissingException : InvalidOperationException
{
    public RuntimeSessionMissingException(string sessionId, string? runtimeSessionId, string? runtime)
        : base(BuildMessage(sessionId, runtimeSessionId, runtime))
    {
        SessionId = sessionId;
        RuntimeSessionId = runtimeSessionId;
        Runtime = runtime;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string? RuntimeSessionId { get; }
    [Id(2)]
    public string? Runtime { get; }

    private static string BuildMessage(string sessionId, string? runtimeSessionId, string? runtime)
    {
        var details = string.IsNullOrEmpty(runtimeSessionId)
            ? "no runtime session is bound"
            : $"runtime session {runtimeSessionId} uses unavailable runtime '{runtime ?? "unknown"}'";
        return $"Runtime session missing for AgentSession {sessionId}: {details}. Reset the session to establish a new binding.";
    }
}

[Serializable]
[GenerateSerializer]
public sealed class StaleRuntimeSessionBindingException : InvalidOperationException
{
    public StaleRuntimeSessionBindingException(
        string sessionId,
        string? expectedRuntimeSessionId,
        string? actualRuntimeSessionId)
        : base(BuildMessage(sessionId, expectedRuntimeSessionId, actualRuntimeSessionId))
    {
        SessionId = sessionId;
        ExpectedRuntimeSessionId = expectedRuntimeSessionId;
        ActualRuntimeSessionId = actualRuntimeSessionId;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string? ExpectedRuntimeSessionId { get; }
    [Id(2)]
    public string? ActualRuntimeSessionId { get; }

    private static string BuildMessage(
        string sessionId,
        string? expectedRuntimeSessionId,
        string? actualRuntimeSessionId) =>
        $"Reset rejected for AgentSession {sessionId}: expected runtime session " +
        $"'{expectedRuntimeSessionId ?? "none"}', but the current binding is " +
        $"'{actualRuntimeSessionId ?? "none"}'.";
}

[Serializable]
[GenerateSerializer]
public sealed class RecoveryOperationInProgressException : InvalidOperationException
{
    public RecoveryOperationInProgressException(string sessionId, string operation)
        : base($"AgentSession {sessionId} already has a {operation} recovery operation in progress.")
    {
        SessionId = sessionId;
        Operation = operation;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string Operation { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class FollowupOperationInProgressException : InvalidOperationException
{
    public FollowupOperationInProgressException(string sessionId)
        : base($"AgentSession {sessionId} already has a follow-up delivery in progress.")
    {
        SessionId = sessionId;
    }

    [Id(0)]
    public string SessionId { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class AgentSessionFollowupCapacityExceededException : InvalidOperationException
{
    public AgentSessionFollowupCapacityExceededException(string sessionId, int capacity)
        : base($"AgentSession {sessionId} has reached its follow-up capacity of {capacity} non-terminal turns; new follow-ups are rejected.")
    {
        SessionId = sessionId;
        Capacity = capacity;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public int Capacity { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class StopOperationInProgressException : InvalidOperationException
{
    public StopOperationInProgressException(string sessionId, string turnId)
        : base($"AgentSession {sessionId} is stopping turn {turnId}.")
    {
        SessionId = sessionId;
        TurnId = turnId;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string TurnId { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class FollowupConcurrencyLimitException : InvalidOperationException
{
    public FollowupConcurrencyLimitException(string sessionId, string agentId)
        : base($"AgentSession {sessionId} cannot start a follow-up; Agent '{agentId}' is at its MaxConcurrentRuns limit.")
    {
        SessionId = sessionId;
        AgentId = agentId;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string AgentId { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class SessionActivityUnknownException : InvalidOperationException
{
    public SessionActivityUnknownException(string sessionId)
        : base($"AgentSession {sessionId} has an unknown runtime activity state.")
    {
        SessionId = sessionId;
    }

    [Id(0)]
    public string SessionId { get; }
}

[GenerateSerializer]
public sealed record AgentSessionMetadata(
    [property: Id(0)] IReadOnlyDictionary<string, string>? Labels = null,
    [property: Id(1)] IReadOnlyDictionary<string, string>? Annotations = null)
{
    public string? Label(string key) => Labels is not null && Labels.TryGetValue(key, out var value) ? value : null;

    public string? Annotation(string key) => Annotations is not null && Annotations.TryGetValue(key, out var value) ? value : null;

    public AgentSessionMetadata WithLabel(string key, string? value) =>
        value is null ? this : this with { Labels = With(Labels, key, value) };

    public AgentSessionMetadata WithAnnotation(string key, string? value) =>
        value is null ? this : this with { Annotations = With(Annotations, key, value) };

    public AgentSessionMetadata Merge(AgentSessionMetadata? other)
    {
        if (other is null) return this;
        var next = this;
        if (other.Labels is not null)
            foreach (var (key, value) in other.Labels)
            {
                if (IsSourceLabel(key))
                {
                    var current = next.Label(key);
                    if (current is null)
                        throw new InvalidOperationException($"AgentSession source label '{key}' cannot be added after creation.");
                    if (!string.Equals(current, value, StringComparison.Ordinal))
                        throw new InvalidOperationException($"AgentSession source label '{key}' is immutable.");
                    continue;
                }
                next = next.WithLabel(key, value);
            }
        if (other.Annotations is not null)
            foreach (var (key, value) in other.Annotations)
                next = next.WithAnnotation(key, value);
        next.ValidateSource();
        return next;
    }

    public void ValidateSource(bool allowLegacySource = false)
    {
        var kind = Label(SourceKindKey);
        if (kind is null)
        {
            if (allowLegacySource) return;
            throw new InvalidOperationException("AgentSession source requires exactly one known source kind.");
        }

        if (string.IsNullOrWhiteSpace(Label(ProjectIdKey)))
            throw new InvalidOperationException("AgentSession source requires a project label.");

        if (string.Equals(kind, "workflow", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(Label(WorkflowRunIdKey)) || string.IsNullOrWhiteSpace(Label(SessionNameKey)))
                throw new InvalidOperationException("Workflow AgentSession source requires workflow run and session name labels.");
            return;
        }

        if (string.Equals(kind, "agent-launch", StringComparison.Ordinal)
            || string.Equals(kind, "agent-connection", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(Label(AgentIdKey)))
                throw new InvalidOperationException("Agent-launch AgentSession source requires an agent label.");
            return;
        }

        throw new InvalidOperationException($"Unknown AgentSession source kind '{kind}'.");
    }

    private const string ProjectIdKey = "mohist.io/project-id";
    private const string SourceKindKey = "mohist.io/source-kind";
    private const string WorkflowRunIdKey = "mohist.io/source-id";
    private const string SessionNameKey = "mohist.io/session-name";
    private const string AgentIdKey = "mohist.io/agent-id";

    private static bool IsSourceLabel(string key) =>
        key is ProjectIdKey or SourceKindKey or WorkflowRunIdKey or SessionNameKey or AgentIdKey;

    private static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string>? source, string key, string value)
    {
        var next = source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);
        next[key] = value;
        return next;
    }
}

public sealed record AgentSessionRuntime(
    string RunnerId,
    string? WorkDir,
    string? Runtime = null);

public sealed record AgentSessionSettings(
    string? Model = null,
    AgentExecutionDefinition? Definition = null);

public enum AgentSessionActivity
{
    Idle,
    Active,
    Unknown,
}

public sealed record AgentRuntimeBinding(string RunnerId, string? Runtime, string? RuntimeSessionId);

public sealed record AgentSessionStatusSnapshot(
    string? AgentRuntimeSessionId = null,
    DateTime CreatedAt = default,
    DateTime? BoundAt = null,
    DateTime? LastDataAt = null,
    AgentUsageSummary? UsageSummary = null,
    IReadOnlyList<ContextUsageHistoryEntry>? ContextUsageHistory = null,
    AgentSessionResetReservation? PendingReset = null,
    AgentSessionFollowupLease? PendingFollowup = null,
    IReadOnlyList<AgentSessionFollowupLease>? PendingFollowups = null,
    IReadOnlyList<AgentSessionTranscriptEvidence>? PendingTranscriptEvidence = null,
    DateTime? CurrentTurnEndedAt = null,
    AgentSessionActivity Activity = AgentSessionActivity.Idle,
    /// <summary>
    /// Ordered child inputs created against this session. The first
    /// entry is the launch-time input recorded by the
    /// <see cref="Mohist.Server.Agent.Grains.AgentLaunchCoordinatorGrain"/>
    /// before the AgentJob dispatch. Subsequent entries are follow-up
    /// inputs (out of scope for the manual launch surface).
    /// </summary>
    IReadOnlyList<AgentSessionInputRecord>? Inputs = null,
    /// <summary>
    /// Ordered child turns created against this session. The first
    /// entry is the launch-time turn associated with the AgentJob
    /// grain. <see cref="AgentTurnRecord.JobId"/> links the turn to
    /// the AgentJob that owns its first-execution result; the
    /// AgentSession remains the authority for input acceptance and
    /// turn status, not for the AgentJob's terminal result.
    /// </summary>
    IReadOnlyList<AgentTurnRecord>? Turns = null,
    AgentSessionStopClaim? PendingStop = null)
{
    public static AgentSessionStatusSnapshot Created(DateTime now) =>
        new(CreatedAt: now, UsageSummary: new AgentUsageSummary(), ContextUsageHistory: []);
}

public sealed record AgentUsageSummary(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    long? CachedReadTokens = null,
    long? CachedWriteTokens = null,
    long? ThoughtTokens = null,
    double? CostAmount = null,
    string? CostCurrency = null,
    long? ContextWindowUsed = null,
    long? ContextWindowSize = null);

/// <summary>
/// One sample in the bounded context-usage history retained on
/// <see cref="AgentSessionStatusSnapshot.ContextUsageHistory"/>. The
/// list is appended on <c>usage.updated</c> / <c>context_health_update</c>,
/// hard-capped at a small fixed size (~24 samples) and time-thinned to
/// a coarse bucket (~30s) so the grain state and downstream payloads
/// stay small regardless of session length.
/// </summary>
public sealed record ContextUsageHistoryEntry(
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("percent")] double Percent);

public sealed record AgentSessionResetReservation(
    string OperationId,
    string? ExpectedRuntimeSessionId,
    string Runtime,
    DateTime StartedAt,
    string Command = "reset",
    AgentSessionRecoveryOutcome? Outcome = null,
    string? IdempotencyKey = null,
    IReadOnlyList<string>? AdditionalIdempotencyKeys = null);

public sealed record AgentSessionRecoveryOutcome(
    string Id,
    string Status,
    long? ContextWindowSize,
    long? ContextWindowUsed,
    double? ContextUsagePercent,
    long? ContextWindowUsedBefore,
    string? Operation,
    bool WasCompacted);

[GenerateSerializer]
public sealed record AgentSessionFollowupLease(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string RuntimeSessionId,
    [property: Id(2)] bool Accepted = false,
    [property: Id(3)] DateTime? AcceptedAt = null,
    [property: Id(4)] DateTime? StartedAt = null,
    /// <summary>
    /// When non-null, this follow-up lease occupies a per-agent
    /// concurrency permit acquired at <c>BeginFollowupAsync</c>. The
    /// permit is released when the lease is cleared by an idle
    /// activity event, the lease-expiration sweep, or an explicit
    /// abandon. Null on leases created for follow-ups that join an
    /// already-active session (per-session serial, no new permit).
    /// Append-only Orleans field id.
    /// </summary>
    [property: Id(5)] string? ConcurrencyToken = null,
    /// <summary>
    /// Agent identity stamped on the lease when the concurrency
    /// permit is acquired, so the lease-clearing release path can
    /// route back to the same per-agent gate as the launch path.
    /// Null when <see cref="ConcurrencyToken"/> is null.
    /// </summary>
    [property: Id(6)] string? ConcurrencyAgentId = null,
    [property: Id(7)] string? InputId = null,
    [property: Id(8)] string? TurnId = null,
    [property: Id(9)] bool Dispatching = false,
    [property: Id(10)] bool PayloadSealed = false);

/// <summary>
/// Result of a single <see cref="AgentSessionExtensions.AcceptFollowup"/>
/// transition. Stable input / turn / operation ids are returned
/// synchronously; <see cref="AlreadyAccepted"/> indicates the
/// request was an idempotent retry against an existing input;
/// <see cref="ShouldRedeliver"/> is true when the call is the
/// first identity-creating accept for a queued turn (or the
/// retry was issued against a still-queued turn the runner
/// has not yet started executing) — the caller should dispatch
/// to the runner. When false the retry is identity-only and
/// must NOT cause a second dispatch.
/// </summary>
[GenerateSerializer]
public sealed record AgentSessionFollowupAcceptResult(
    [property: Id(0)] string InputId,
    [property: Id(1)] string TurnId,
    [property: Id(2)] string OperationId,
    [property: Id(3)] bool AlreadyAccepted,
    [property: Id(4)] bool ShouldRedeliver,
    [property: Id(5)] AgentSessionInputAcceptance InputAcceptance = AgentSessionInputAcceptance.Accepted,
    [property: Id(6)] AgentTurnStatus TurnStatus = AgentTurnStatus.Queued,
    /// <summary>
    /// Accepted attachment descriptors for the input. The list is
    /// empty when the input is text-only or carries no accepted
    /// attachments. Append-only Orleans field id (next free after
    /// <see cref="TurnStatus"/>).
    /// </summary>
    [property: Id(7)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    /// <summary>
    /// Per-attachment verdicts the route validated at acceptance.
    /// Preserves the caller's original id order so the response
    /// surface renders the same order the user submitted; rejected
    /// entries carry a stable reason code and human-readable
    /// message. Append-only Orleans field id (next free after
    /// <see cref="Attachments"/>).
    /// </summary>
    [property: Id(8)] IReadOnlyList<AgentInputAttachmentAcceptance>? AttachmentResults = null);

[GenerateSerializer]
public sealed record AgentSessionFollowupDispatch(
    [property: Id(0)] string TurnId,
    [property: Id(1)] string OperationId,
    /// <summary>
    /// Per-input text payloads flattened across all inputs assigned
    /// to the turn. Preserved verbatim for the existing single-text
    /// dispatch envelope.
    /// </summary>
    [property: Id(2)] IReadOnlyList<string> InputTexts,
    /// <summary>
    /// Accepted attachment descriptors carried by the dispatched
    /// turn. Empty when the turn is text-only. Append-only Orleans
    /// field id (next free after <see cref="InputTexts"/>).
    /// </summary>
    [property: Id(3)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    /// <summary>
    /// The owning input id for the dispatched attachment content route.
    /// A follow-up currently consumes one input; null preserves the
    /// legacy multi-input shape without inventing an owner scope.
    /// </summary>
    [property: Id(4)] string? InputId = null);

/// <summary>
/// Lookup result of <see cref="AgentSessionExtensions.FindFollowupInputByIdempotencyKey"/>.
/// <see cref="Turn"/> is <c>null</c> when the input was never
/// assigned to a turn (an edge that should not normally happen —
/// the search is the gateway for idempotent retry, so the input
/// is expected to be linked to a turn).
/// </summary>
public sealed record AgentSessionFollowupInputLookup(
    AgentSessionInputRecord Input,
    AgentTurnRecord? Turn,
    string? OperationId = null);

public sealed record AgentSessionTranscriptEvidence(
    string Id,
    string? RuntimeSessionId,
    string Type,
    string PayloadJson,
    DateTime CreatedAt,
    string PromptKind);

/// <summary>
/// One child input recorded on the AgentSession. Inputs are addressed
/// through their Session and surfaced via the composite observation
/// surface — they are not independent top-level
/// resources. The launch-time input is created by the
/// <see cref="Mohist.Server.Agent.Grains.AgentLaunchCoordinatorGrain"/>
/// before the AgentJob dispatches so the durable launch identity
/// exists at acceptance time. Acceptance is the Server-side verdict
/// that the prompt is recorded; the runtime's eventual delivery
/// result is recorded as a separate turn status update, not as
/// reverting the input acceptance.
///
/// <para>
/// <see cref="Attachments"/> carries the ordered list of attachments
/// accepted at input validation time. Attachments are persisted as a
/// child record of the input so the accepted set is authoritative
/// across restart and is queryable via the same input/turn surface as
/// the text. Append-only Orleans field id (next free after
/// <see cref="IdempotencyKey"/>); absent on records written before
/// attachments were attached to inputs — the runtime treats an absent
/// or empty list as no attachments.
/// </para>
/// </summary>
[GenerateSerializer]
public sealed record AgentSessionInputProvenance(
    [property: Id(0)] string ProviderKind,
    [property: Id(1)] string WorkspaceId,
    [property: Id(2)] string ConversationId,
    [property: Id(3)] string? ThreadId,
    [property: Id(4)] string MemberId,
    [property: Id(5)] string MessageId,
    [property: Id(6)] string? ConnectionId = null);

[GenerateSerializer]
public sealed record AgentSessionInputRecord(
    [property: Id(0)] string Id,
    [property: Id(1)] long Sequence,
    [property: Id(2)] string Text,
    [property: Id(3)] string Source,
    [property: Id(4)] AgentSessionInputAcceptance Acceptance,
    [property: Id(5)] DateTime RecordedAt,
    [property: Id(6)] string? JobId = null,
    [property: Id(7)] string? IdempotencyKey = null,
    [property: Id(8)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    [property: Id(9)] AgentSessionInputProvenance? Provenance = null);

public enum AgentSessionInputAcceptance
{
    Accepted,
    Pending,
    Rejected,
}

[GenerateSerializer]
public sealed record AgentTurnRecord(
    [property: Id(0)] string Id,
    [property: Id(1)] long Sequence,
    [property: Id(2)] IReadOnlyList<string> InputIds,
    [property: Id(3)] AgentTurnStatus Status,
    [property: Id(4)] string? JobId = null,
    [property: Id(5)] AgentTurnResult? Result = null,
    [property: Id(6)] DateTime? RecordedAt = null,
    [property: Id(7)] DateTime? UpdatedAt = null);

[GenerateSerializer]
public sealed record AgentSessionStopClaim(
    [property: Id(0)] string TurnId,
    [property: Id(1)] string OperationId,
    [property: Id(2)] bool DispatchStarted = false);

public enum AgentTurnStatus
{
    Queued,
    Executing,
    Completed,
    Failed,
    Unknown,
    Cancelled,
}

[GenerateSerializer]
public sealed record AgentTurnResult(
    [property: Id(0)] string? Message = null,
    [property: Id(1)] string? Output = null,
    [property: Id(2)] string? FailureReason = null,
    [property: Id(3)] string? FailureCategory = null,
    [property: Id(4)] int? ExitCode = null);

/// <summary>
/// Shared read-only projection of a single
/// <see cref="AgentTurnRecord"/> for cancel/stop targeting. Returned
/// by the Session grain's turn-control resolver so later cancel and
/// stop operations can classify a Turn without re-reading the full
/// AgentSession status snapshot. A null result means the id does not
/// resolve (turn-not-found).
/// </summary>
[GenerateSerializer]
public sealed record AgentTurnControlState(
    [property: Id(0)] string TurnId,
    [property: Id(1)] AgentTurnStatus Status,
    [property: Id(2)] AgentTurnControlClassification Classification,
    [property: Id(3)] bool IsLaunchTurn,
    [property: Id(4)] string? JobId = null);

[GenerateSerializer]
public sealed record AgentTurnCancelResult(
    [property: Id(0)] AgentTurnControlState? Control,
    [property: Id(1)] bool Cancelled);

[GenerateSerializer]
public sealed record AgentTurnStopClaimResult(
    [property: Id(0)] AgentTurnControlState? Control,
    [property: Id(1)] bool CanDispatch,
    [property: Id(2)] string? OperationId);

[GenerateSerializer]
public enum AgentTurnControlClassification
{
    /// <summary>
    /// No Turn matches the supplied id. The caller treats this as
    /// turn-not-found and reports the stale entry back to the user
    /// without touching any Turn.
    /// </summary>
    TurnNotFound,
    /// <summary>
    /// The Turn is queued (not yet executing). Cancel applies; stop
    /// is rejected and the caller is directed to cancel.
    /// </summary>
    Queued,
    /// <summary>
    /// The Turn is executing. Stop applies; cancel is rejected and
    /// the caller is directed to stop.
    /// </summary>
    Executing,
    /// <summary>
    /// The Turn already ended
    /// (<see cref="AgentTurnStatus.Completed"/>,
    /// <see cref="AgentTurnStatus.Failed"/>,
    /// <see cref="AgentTurnStatus.Cancelled"/>, or
    /// <see cref="AgentTurnStatus.Unknown"/>). Both cancel and stop
    /// report turn-already-ended.
    /// </summary>
    Terminal,
}
