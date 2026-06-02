using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Persistence.Workflow;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run);
    Task<WorkflowRun?> LoadAsync(string workflowRunId);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private readonly MohistDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowRunStore(MohistDbContext db)
    {
        _db = db;
    }

    public async Task SaveAsync(WorkflowRun run)
    {
        var json = JsonSerializer.Serialize(run, JsonOptions);
        var entity = await _db.WorkflowRuns.FindAsync(run.Id);

        if (entity is null)
        {
            var newEntity = new WorkflowRunRow { WorkflowRunId = run.Id, State = json };
            _db.WorkflowRuns.Add(newEntity);
            _db.Entry(newEntity).Property<long>("ETag").CurrentValue = 1;
            await _db.SaveChangesAsync();
            return;
        }

        entity.State = json;
        var entry = _db.Entry(entity);
        entry.Property<long>("ETag").CurrentValue = entry.Property<long>("ETag").OriginalValue + 1;
        await _db.SaveChangesAsync();
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

}
