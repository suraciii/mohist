namespace Mohist.Server.Infrastructure.Data.Events;

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