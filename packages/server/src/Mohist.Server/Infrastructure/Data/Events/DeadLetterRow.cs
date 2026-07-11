using System.Text.Json;

namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// Persisted record of a poison event whose handler retries exhausted.
/// One row per failed handler delivery; the original event row remains in its
/// truth table (WorkflowRunEvents / IssueEvents / EpicEvents /
/// AgentSessionEvents) with its <c>DispatchedAt</c> set so the dispatcher
/// stops retrying it. Dead-letter rows are queryable for operator
/// inspection and feed the manual re-delivery path.
/// </summary>
public sealed class DeadLetterRow
{
    public long DeadLetterId { get; init; }
    public required string Origin { get; init; }
    public required long Id { get; init; }
    public required string Source { get; init; }
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required string SpecVersion { get; init; }
    public string? Subject { get; init; }
    public required string DataContentType { get; init; }
    public required JsonElement Data { get; init; }
    public required string ExtensionsJson { get; init; }
    public required string FailingHandler { get; init; }
    public required string ErrorMessage { get; init; }
    public string? ErrorStack { get; init; }
    public required int AttemptCount { get; init; }
    public required DateTimeOffset DeadLetteredAt { get; init; }
}
