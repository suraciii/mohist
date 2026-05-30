using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.GrainStorage;

public class WorkflowVariablesStore : IStateStore<WorkflowExecutionContext>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowVariablesStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowExecutionContext?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowVariables.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, WorkflowExecutionContext state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowVariables.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowVariables.Add(new WorkflowVariablesRow { WorkflowRunId = key, StateJson = json });
        else
            row.StateJson = json;
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();
    public Task<IReadOnlyList<WorkflowExecutionContext>> ListAsync() => throw new NotSupportedException();

    internal static WorkflowExecutionContext? Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowExecutionContext>(json, WorkflowStorageJson.Options);

    internal static string Serialize(WorkflowExecutionContext state) =>
        JsonSerializer.Serialize(state, WorkflowStorageJson.Options);
}

public static class WorkflowStorageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
