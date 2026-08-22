namespace Mohist.Server.Infrastructure.Data.Events;

using System.Text.Json;

/// <summary>
/// Common shape for per-source event rows persisted by
/// <c>IEventStore</c>. Lets the per-source Id sequence assignment work
/// generically across <c>WorkflowRunEventRow</c>, <c>IssueEventRow</c>,
/// <c>EpicEventRow</c>, and <c>AgentSessionEventRow</c> without
/// duplicating the local + committed MAX(Id) logic per table.
/// </summary>
public interface IEventRow
{
    long Id { get; init; }
    string Source { get; init; }
}

/// <summary>
/// The CloudEvents envelope columns every event table carries. The
/// dispatch path reads and settles rows through this shape regardless of
/// which aggregate table stores them.
/// </summary>
public interface IEventEnvelopeRow : IEventRow
{
    string EventId { get; init; }
    string Type { get; init; }
    DateTimeOffset Time { get; init; }
    string SpecVersion { get; init; }
    string? Subject { get; init; }
    string DataContentType { get; init; }
    JsonElement Data { get; init; }
    string ExtensionsJson { get; init; }
    DateTimeOffset? DispatchedAt { get; set; }
}
