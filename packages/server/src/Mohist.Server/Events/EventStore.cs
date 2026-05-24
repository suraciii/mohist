using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Events;

public class EventStore : IEventStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;

    public EventStore(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
    }

    public async Task<EventDto> AppendAsync(EventInput input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entry = new WorkflowEventEntry
        {
            ProjectId = input.ProjectId,
            IssueId = input.IssueId,
            IssueNumber = input.IssueNumber,
            WorkflowRunId = input.WorkflowRunId,
            Category = input.Category,
            Type = input.Type,
            Stage = input.Stage,
            TaskId = input.TaskId,
            CheckName = input.CheckName,
            RunnerId = input.RunnerId,
            Status = input.Status,
            Message = input.Message,
            PayloadJson = input.Payload is not null ? JsonSerializer.Serialize(input.Payload) : null,
            CreatedAt = DateTime.UtcNow,
        };

        db.WorkflowEvents.Add(entry);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(entry);
        _eventBus.Emit(input.Type, dto);
        return dto;
    }

    public async Task<IReadOnlyList<EventDto>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowEvents.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.IssueNumber == issueNumber)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<EventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowEvents.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<EventDto>> ListRecentAsync(string projectId, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowEvents.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private static EventDto ToDto(WorkflowEventEntry entry) => new(
        entry.Id.ToString(),
        entry.ProjectId,
        entry.IssueId,
        entry.IssueNumber,
        entry.WorkflowRunId,
        entry.Category,
        entry.Type,
        entry.Stage,
        entry.TaskId,
        entry.CheckName,
        entry.RunnerId,
        entry.Status,
        entry.Message,
        ParsePayload(entry.PayloadJson),
        entry.CreatedAt.ToString("o"));

    private static object? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
