using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Storage;

public class WorkflowRunStore : IStateStore<WorkflowRunState>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowRunState?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunStates.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, WorkflowRunState state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunStates.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowRunStates.Add(new WorkflowRunStateRow { WorkflowRunId = key, StateJson = json });
        else
            row.StateJson = json;
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();
    public Task<IReadOnlyList<WorkflowRunState>> ListAsync() => throw new NotSupportedException();

    internal static WorkflowRunState? Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowRunState>(json, WorkflowRunJson.Options);

    internal static string Serialize(WorkflowRunState state) =>
        JsonSerializer.Serialize(state, WorkflowRunJson.Options);
}

public static class WorkflowRunJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

public class WorkflowRunStateRow
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string StateJson { get; set; } = "{}";
}
