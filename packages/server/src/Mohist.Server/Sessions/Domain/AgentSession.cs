using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions.Domain;

public sealed class AgentSession
{
    public required string Id { get; init; }
    public AgentSessionMetadata Metadata { get; set; } = new();
    public required AgentSessionRuntime Runtime { get; set; }
    public AgentSessionSettings Settings { get; set; } = new();
    public AgentSessionStatusSnapshot Status { get; set; } = AgentSessionStatusSnapshot.Created(DateTime.UtcNow);

    [JsonIgnore]
    public string ProjectId => Metadata.Label(AgentSessionMetadataKeys.ProjectId) ?? string.Empty;
    [JsonIgnore]
    public int IssueNumber => int.TryParse(Metadata.Label(AgentSessionMetadataKeys.IssueNumber), out var value) ? value : 0;
    [JsonIgnore]
    public string RunId => Metadata.Label(AgentSessionMetadataKeys.SourceId) ?? string.Empty;
    [JsonIgnore]
    public string SessionName => Metadata.Label(AgentSessionMetadataKeys.SessionName) ?? string.Empty;
    [JsonIgnore]
    public string? SourceKind => Metadata.Label(AgentSessionMetadataKeys.SourceKind);
    [JsonIgnore]
    public string? TaskId => Metadata.Label(AgentSessionMetadataKeys.WorkId);
    [JsonIgnore]
    public string? TaskKind => Metadata.Label(AgentSessionMetadataKeys.WorkType);
    [JsonIgnore]
    public string? Phase => Metadata.Label(AgentSessionMetadataKeys.Stage);
    [JsonIgnore]
    public string? Title => Metadata.Annotation(AgentSessionMetadataKeys.Title);
    [JsonIgnore]
    public string? ChangeDir => null;

    public static AgentSession Create(
        string id,
        string runnerId,
        string agentRuntime,
        string? workDir,
        string? model = null,
        AgentSessionMetadata? metadata = null,
        DateTime? now = null)
    {
        var createdAt = now ?? DateTime.UtcNow;
        return new AgentSession
        {
            Id = id,
            Metadata = metadata ?? new AgentSessionMetadata(),
            Runtime = new AgentSessionRuntime(runnerId, agentRuntime, workDir),
            Settings = new AgentSessionSettings(model),
            Status = AgentSessionStatusSnapshot.Created(createdAt)
        };
    }
}

public static class AgentSessionMetadataKeys
{
    public const string ProjectId = "mohist.io/project-id";
    public const string IssueNumber = "mohist.io/issue-number";
    public const string SourceKind = "mohist.io/source-kind";
    public const string SourceId = "mohist.io/source-id";
    public const string SessionName = "mohist.io/session-name";
    public const string WorkId = "mohist.io/work-id";
    public const string WorkType = "mohist.io/work-type";
    public const string Stage = "mohist.io/stage";
    public const string Title = "mohist.io/title";
}

public static class AgentSessionKey
{
    public const string Workflow = "workflow";

    public static string WorkflowSession(string workflowRunId, string sessionName) => $"workflows/{workflowRunId}/{sessionName}";
}

public sealed record AgentSessionMetadata(
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyDictionary<string, string>? Annotations = null)
{
    public string? Label(string key) => Labels is not null && Labels.TryGetValue(key, out var value) ? value : null;

    public string? Annotation(string key) => Annotations is not null && Annotations.TryGetValue(key, out var value) ? value : null;

    public AgentSessionMetadata WithLabel(string key, string? value) =>
        value is null ? this : this with { Labels = With(Labels, key, value) };

    public AgentSessionMetadata WithAnnotation(string key, string? value) =>
        value is null ? this : this with { Annotations = With(Annotations, key, value) };

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
    string AgentRuntime,
    string? WorkDir);

public sealed record AgentSessionSettings(string? Model = null);

public sealed record AgentSessionStatusSnapshot(
    AgentSessionStatus Phase = AgentSessionStatus.Created,
    string? AgentRuntimeSessionId = null,
    DateTime CreatedAt = default,
    DateTime? StartedAt = null,
    DateTime? LastDataAt = null,
    DateTime? CompletedAt = null,
    string? FailureReason = null,
    int? ExitCode = null,
    AgentUsageSummary? UsageSummary = null)
{
    public static AgentSessionStatusSnapshot Created(DateTime now) =>
        new(AgentSessionStatus.Created, CreatedAt: now, UsageSummary: new AgentUsageSummary());
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
