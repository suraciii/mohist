using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run);
    Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default);
    Task<WorkflowRun?> LoadAsync(string workflowRunId);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private readonly MohistDbContext _db;
    private readonly IEventBus _eventBus;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowRunStore(MohistDbContext db, IEventBus eventBus)
    {
        _db = db;
        _eventBus = eventBus;
    }

    public async Task SaveAsync(WorkflowRun run)
    {
        await StageRunAsync(run);
        await _db.SaveChangesAsync();
    }

    public async Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageRunAsync(run, ct);
            var stagedEvents = await WorkflowEventPersistence.StageAsync(_db, run.Id, events, ct);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            Publish(stagedEvents);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task StageRunAsync(WorkflowRun run, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(run, JsonOptions);
        var entity = await _db.WorkflowRuns.FindAsync([run.Id], ct);

        if (entity is null)
        {
            var newEntity = new WorkflowRunRow { WorkflowRunId = run.Id, State = json };
            _db.WorkflowRuns.Add(newEntity);
            _db.Entry(newEntity).Property<long>("ETag").CurrentValue = 1;
            return;
        }

        entity.State = json;
        var entry = _db.Entry(entity);
        entry.Property<long>("ETag").CurrentValue = entry.Property<long>("ETag").OriginalValue + 1;
    }

    public async Task<WorkflowRun?> LoadAsync(string workflowRunId)
    {
        var entity = await _db.WorkflowRuns.FindAsync(workflowRunId);
        if (entity is null) return null;
        return Deserialize(entity.State);
    }

    private static WorkflowRun? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void Publish(IReadOnlyList<StagedWorkflowEvent> stagedEvents)
    {
        foreach (var e in stagedEvents)
        {
            var dto = WorkflowEventPersistence.ToDto(e);
            _eventBus.Emit(dto.Type, dto);
        }
    }

}
