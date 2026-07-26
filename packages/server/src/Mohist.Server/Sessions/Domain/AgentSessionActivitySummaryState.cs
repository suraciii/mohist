using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions.Domain;

internal sealed record AgentSessionActivitySummaryState
{
    public string? ResolvedModel { get; init; }
    public string? FailureCategory { get; init; }
    public int? ToolCallCount { get; init; }
    public int? ToolErrorCount { get; init; }
    public string? FailureReason { get; init; }
    public long CurrentTurnSequence { get; init; }
    public long CurrentPartSequence { get; init; }
    public IReadOnlyList<AgentSessionActivitySummaryPart> CurrentTurnParts { get; init; } = [];
    public IReadOnlyList<string> SealedToolCallIds { get; init; } = [];
    public IReadOnlyList<string> SealedFailedToolCallIds { get; init; } = [];
    public AgentSessionActivitySummaryCandidate? LatestActivity { get; init; }

    public static AgentSessionActivitySummaryState Empty { get; } = new();

    [JsonIgnore]
    public AgentSessionTranscriptSummary Summary => new(
        ResolvedModel,
        FailureCategory,
        ToolCallCount,
        ToolErrorCount,
        FailureReason);

    public AgentSessionActivitySummaryState Normalize() => this with
    {
        CurrentTurnParts = CurrentTurnParts ?? [],
        SealedToolCallIds = SealedToolCallIds ?? [],
        SealedFailedToolCallIds = SealedFailedToolCallIds ?? []
    };
}

internal sealed record AgentSessionActivitySummaryPart(
    string Type,
    string CorrelationKey,
    string PartId,
    long Sequence,
    bool IsFailed);

internal sealed record AgentSessionActivitySummaryCandidate(
    long TurnSequence,
    long PartSequence,
    string PartId,
    string? FailureCategory,
    string? FailureReason);
