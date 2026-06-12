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

public sealed record AgentSessionStatusSnapshot(
    AgentSessionStatus Phase = AgentSessionStatus.Opened,
    string? AgentRuntimeSessionId = null,
    DateTime CreatedAt = default,
    DateTime? BoundAt = null,
    DateTime? LastDataAt = null,
    AgentUsageSummary? UsageSummary = null)
{
    public static AgentSessionStatusSnapshot Created(DateTime now) =>
        new(AgentSessionStatus.Opened, CreatedAt: now, UsageSummary: new AgentUsageSummary());
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
