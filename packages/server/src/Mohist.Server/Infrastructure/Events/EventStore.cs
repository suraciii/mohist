using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

public class EventStore : IEventStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;

    public EventStore(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
    }

    public async Task<WorkflowDomainEventDto> AppendWorkflowEventAsync(string workflowRunId, WorkflowEvent payload, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var staged = await WorkflowEventPersistence.StageAsync(db, workflowRunId, [payload], ct);
        await db.SaveChangesAsync(ct);

        var dto = WorkflowEventPersistence.ToDto(staged.Single());
        _eventBus.Emit(dto.Type, dto);
        return dto;
    }

    public async Task<IReadOnlyList<WorkflowDomainEventDto>> ListWorkflowEventsAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = WorkflowEventPersistence.WorkflowRunSource(workflowRunId);
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.Source == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .Select(e => new EventReadModel(
                e,
                EF.Property<string>(e, "Type"),
                EF.Property<string>(e, "SpecVersion")))
            .ToListAsync(ct);

        return rows.Select(e => WorkflowEventPersistence.ToDto(e.Row, e.Type, e.SpecVersion)).ToList();
    }

    private sealed record EventReadModel(EventRow Row, string Type, string SpecVersion);
}
