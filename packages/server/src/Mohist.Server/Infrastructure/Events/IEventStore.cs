namespace Mohist.Server.Infrastructure.Events;

public interface IEventStore
{
    Task<EventDto> AppendAsync(EventInput input, CancellationToken ct = default);
    Task<IReadOnlyList<EventDto>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<EventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<EventDto>> ListRecentAsync(string projectId, int limit = 200, CancellationToken ct = default);
}

public sealed record EventInput(
    string ProjectId,
    int IssueNumber,
    string Category,
    string Type,
    string? IssueId = null,
    string? WorkflowRunId = null,
    string? Stage = null,
    string? TaskId = null,
    string? CheckName = null,
    string? RunnerId = null,
    string? Status = null,
    string? Message = null,
    object? Payload = null);

public sealed record EventDto(
    string Id,
    string ProjectId,
    string? IssueId,
    int IssueNumber,
    string? WorkflowRunId,
    string Category,
    string Type,
    string? Stage,
    string? TaskId,
    string? CheckName,
    string? RunnerId,
    string? Status,
    string? Message,
    object? Payload,
    string CreatedAt) : IProjectScoped;
