using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Tests.Support;

public class RecordingEventStore : IEventStore
{
    private readonly List<EventDto> _events = [];
    private readonly Lock _gate = new();

    public Task<EventDto> AppendAsync(EventInput input, CancellationToken ct = default)
    {
        EventDto dto;
        lock (_gate)
        {
            dto = new EventDto(
                (_events.Count + 1).ToString(),
                input.ProjectId,
                input.IssueId,
                input.IssueNumber,
                input.WorkflowRunId,
                input.Category,
                input.Type,
                input.Stage,
                input.TaskId,
                input.CheckName,
                input.RunnerId,
                input.Status,
                input.Message,
                input.Payload,
                DateTime.UtcNow.ToString("o"));
            _events.Add(dto);
        }

        return Task.FromResult(dto);
    }

    public Task<IReadOnlyList<EventDto>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EventDto>>(_events
                .Where(e => e.ProjectId == projectId && e.IssueNumber == issueNumber)
                .TakeLast(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<EventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EventDto>>(_events
                .Where(e => e.WorkflowRunId == workflowRunId)
                .TakeLast(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<EventDto>> ListRecentAsync(string projectId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EventDto>>(_events
                .Where(e => e.ProjectId == projectId)
                .TakeLast(limit)
                .ToList());
        }
    }
}
