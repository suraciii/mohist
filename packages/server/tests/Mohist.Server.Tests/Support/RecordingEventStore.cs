using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Tests.Support;

public class RecordingEventStore : IEventStore
{
    private readonly List<RecordedWorkflowEvent> _events = [];
    private readonly Lock _gate = new();

    public Task<WorkflowDomainEventDto> AppendWorkflowEventAsync(string workflowRunId, WorkflowEvent payload, CancellationToken ct = default)
    {
        WorkflowDomainEventDto dto;
        lock (_gate)
        {
            dto = new WorkflowDomainEventDto(
                _events.Count(e => e.WorkflowRunId == workflowRunId) + 1,
                $"/workflow-runs/{workflowRunId}",
                WorkflowEventSerializer.Type(payload),
                payload,
                DateTime.UtcNow,
                "1.0");
            _events.Add(new RecordedWorkflowEvent(workflowRunId, dto));
        }

        return Task.FromResult(dto);
    }

    public Task<IReadOnlyList<WorkflowDomainEventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowDomainEventDto>>(_events
                .Where(e => e.WorkflowRunId == workflowRunId)
                .TakeLast(limit)
                .Select(e => e.Event)
                .ToList());
        }
    }

    private sealed record RecordedWorkflowEvent(string WorkflowRunId, WorkflowDomainEventDto Event);
}
