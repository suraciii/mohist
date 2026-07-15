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
        return new AgentSession
        {
            Id = id,
            Metadata = metadata ?? new AgentSessionMetadata(),
            Runtime = new AgentSessionRuntime(runnerId, workDir, NormalizeRuntime(runtime)),
            Settings = new AgentSessionSettings(),
            Status = AgentSessionStatusSnapshot.Created(createdAt)
        };
    }

    private static string? NormalizeRuntime(string? runtime) =>
        string.IsNullOrWhiteSpace(runtime) ? null : runtime.Trim();

    public void ValidateState()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("AgentSession state requires a non-empty Id.");
        if (Runtime is null)
            throw new InvalidOperationException("AgentSession state requires a Runtime.");
        if (Status.CreatedAt == default)
            throw new InvalidOperationException("AgentSession state requires CreatedAt to be set.");
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
                next = next.WithLabel(key, value);
        if (other.Annotations is not null)
            foreach (var (key, value) in other.Annotations)
                next = next.WithAnnotation(key, value);
        return next;
    }

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

/// <summary>
/// One entry in the ordered lineage of runtime sessions bound to an
/// <see cref="AgentSession"/> during its lifetime. The Mohist
/// <see cref="AgentSession.Id"/> is the stable identity; each
/// <see cref="AgentRuntimeSessionId"/> is a mutable runtime facet
/// replaced after a reset or another runtime-boundary change
/// (<c>design/conventions.md#identity-terms</c>). Compaction preserves the
/// current runtime binding. The chain
/// <see cref="AgentSessionStatusSnapshot.RuntimeSessionLineage"/>
/// holds all such entries — predecessor/successor are derived by
/// position. Entries are append-only on rebind; the first entry
/// records the original runtime session bound by AttachPhysicalSession.
/// <see cref="Runtime"/> carries the execution-backend name that owned
/// the binding and remains null on legacy entries.
/// </summary>
[GenerateSerializer]
public sealed record RuntimeSessionLineageEntry(
    [property: Id(0)] string AgentRuntimeSessionId,
    [property: Id(1)] DateTime BoundAt,
    [property: Id(2)] string? Runtime = null);

public sealed record AgentSessionStatusSnapshot(
    string? AgentRuntimeSessionId = null,
    DateTime CreatedAt = default,
    DateTime? BoundAt = null,
    DateTime? LastDataAt = null,
    AgentUsageSummary? UsageSummary = null,
    IReadOnlyList<RuntimeSessionLineageEntry>? RuntimeSessionLineage = null,
    IReadOnlyList<ContextUsageHistoryEntry>? ContextUsageHistory = null)
{
    public static AgentSessionStatusSnapshot Created(DateTime now) =>
        new(CreatedAt: now, UsageSummary: new AgentUsageSummary(), RuntimeSessionLineage: [], ContextUsageHistory: []);
}

public sealed record AgentUsageSummary(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    long? CachedReadTokens = null,
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
/// stay small regardless of session length (issue-245 T-002, design D5).
/// </summary>
public sealed record ContextUsageHistoryEntry(
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("percent")] double Percent);
