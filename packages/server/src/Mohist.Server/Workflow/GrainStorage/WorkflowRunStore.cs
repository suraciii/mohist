using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.GrainStorage;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run);
    Task<WorkflowRun?> LoadAsync(string workflowRunId);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowRunStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task SaveAsync(WorkflowRun run)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkflowRuns.FindAsync(run.Id);

        var json = JsonSerializer.Serialize(run, JsonOptions);

        if (entity is null)
        {
            db.WorkflowRuns.Add(new WorkflowRunEntity { WorkflowRunId = run.Id, State = json });
        }
        else
        {
            entity.State = json;
            db.WorkflowRuns.Update(entity);
        }

        await db.SaveChangesAsync();
    }

    public async Task<WorkflowRun?> LoadAsync(string workflowRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkflowRuns.FindAsync(workflowRunId);
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
