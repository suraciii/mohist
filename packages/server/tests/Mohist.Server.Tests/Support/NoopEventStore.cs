using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Tests.Support;

public class NoopEventStore : IEventStore
{
    public Task<EventDto> AppendAsync(EventInput input, CancellationToken ct = default) =>
        Task.FromResult(new EventDto(
            "0",
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
            DateTime.UtcNow.ToString("o")));

    public Task<IReadOnlyList<EventDto>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EventDto>>([]);

    public Task<IReadOnlyList<EventDto>> ListIssueWorkflowLogAsync(string projectId, int issueNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EventDto>>([]);

    public Task<IReadOnlyList<EventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EventDto>>([]);

    public Task<IReadOnlyList<EventDto>> ListRecentAsync(string projectId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EventDto>>([]);
}
