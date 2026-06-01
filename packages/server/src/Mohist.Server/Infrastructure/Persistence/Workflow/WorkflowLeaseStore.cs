using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Persistence.Workflow;

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

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowLeases.FindAsync(key);
        if (row is not null)
        {
            db.WorkflowLeases.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    public Task<IReadOnlyList<WorkLease>> ListAsync() => throw new NotSupportedException();

    internal static WorkLease? Deserialize(string json) => WorkflowLeaseJson.Deserialize(json);

    internal static string Serialize(WorkLease state) => WorkflowLeaseJson.Serialize(state);
}
