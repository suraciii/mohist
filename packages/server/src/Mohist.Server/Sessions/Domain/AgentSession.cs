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
        DateTime? now = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        return new AgentSession
        {
            Id = id,
            Metadata = metadata ?? new AgentSessionMetadata(),
            Runtime = new AgentSessionRuntime(runnerId, workDir),
            Settings = new AgentSessionSettings(),
            Status = AgentSessionStatusSnapshot.Created(createdAt)
        };
    }

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
    string? WorkDir);

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
/// </summary>
[GenerateSerializer]
public sealed record RuntimeSessionLineageEntry(
    [property: Id(0)] string AgentRuntimeSessionId,
    [property: Id(1)] DateTime BoundAt);

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
