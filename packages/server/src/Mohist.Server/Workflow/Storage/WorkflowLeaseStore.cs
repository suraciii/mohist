using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Storage;

public class WorkflowLeaseStore : IStateStore<WorkLease>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowLeaseStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkLease?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowLeases.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, WorkLease state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowLeases.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowLeases.Add(new WorkflowLeaseRow { WorkflowRunId = key, StateJson = json });
        else
            row.StateJson = json;
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();
    public Task<IReadOnlyList<WorkLease>> ListAsync() => throw new NotSupportedException();

    internal static WorkLease? Deserialize(string json) =>
        json == "null" ? null : JsonSerializer.Deserialize<WorkLease>(json, WorkflowStorageJson.Options);

    internal static string Serialize(WorkLease state) =>
        JsonSerializer.Serialize(state, WorkflowStorageJson.Options);
}

public class WorkflowLeaseRow
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string StateJson { get; set; } = "{}";
}
