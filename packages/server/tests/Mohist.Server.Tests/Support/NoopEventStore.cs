using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Tests.Support;

public class NoopEventStore : IEventStore
{
    public Task<WorkflowDomainEventDto> AppendWorkflowEventAsync(string workflowRunId, WorkflowEvent payload, CancellationToken ct = default) =>
        Task.FromResult(new WorkflowDomainEventDto(
            0,
            $"/workflow-runs/{workflowRunId}",
            WorkflowEventSerializer.Type(payload),
            payload,
            DateTime.UtcNow,
            "1.0"));

    public Task<IReadOnlyList<WorkflowDomainEventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowDomainEventDto>>([]);
}
