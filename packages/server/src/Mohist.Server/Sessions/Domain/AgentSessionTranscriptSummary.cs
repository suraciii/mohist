namespace Mohist.Server.Sessions.Domain;

public sealed record AgentSessionTranscriptSummary(
    string? ResolvedModel,
    string? FailureCategory,
    int? ToolCallCount,
    int? ToolErrorCount,
    string? FailureReason = null)
{
    public static readonly AgentSessionTranscriptSummary Empty = new(null, null, null, null, null);
}
