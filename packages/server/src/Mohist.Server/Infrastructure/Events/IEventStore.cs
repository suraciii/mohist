using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Events;

public interface IEventStore
{
    Task<WorkflowDomainEventDto> AppendWorkflowEventAsync(string workflowRunId, WorkflowEvent payload, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowDomainEventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default);
}

public sealed record WorkflowDomainEventDto(
    long Id,
    string Source,
    string Type,
    WorkflowEvent Data,
    DateTime Time,
    string SpecVersion);

public sealed record AgentSessionDomainEventDto(
    long Id,
    string Source,
    string Type,
    AgentSessionEvent Data,
    DateTime Time,
    string SpecVersion);
