using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Workflow;

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
        return row is null ? null : Deserialize(row.State);
    }

    public async Task SaveAsync(string key, WorkflowExecutionContext state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowVariables.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowVariables.Add(new WorkflowVariablesRow { WorkflowRunId = key, State = json });
        else
            row.State = json;
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotImplementedException();
    public Task<IReadOnlyList<WorkflowExecutionContext>> ListAsync() => throw new NotImplementedException();

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
