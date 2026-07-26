using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions.Domain;

public sealed class AgentSession
{
    public required string Id { get; init; }
    public AgentSessionMetadata Metadata { get; set; } = new();
    public required AgentSessionRuntime Runtime { get; set; }
    public AgentSessionSettings Settings { get; set; } = new();
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

        if (string.Equals(kind, "agent-launch", StringComparison.Ordinal))
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

public sealed record AgentSessionSettings(string? Model = null);

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
    AgentSessionActivity Activity = AgentSessionActivity.Idle)
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
    [property: Id(4)] DateTime? StartedAt = null);

public sealed record AgentSessionTranscriptEvidence(
    string Id,
    string? RuntimeSessionId,
    string Type,
    string PayloadJson,
    DateTime CreatedAt,
    string PromptKind);
