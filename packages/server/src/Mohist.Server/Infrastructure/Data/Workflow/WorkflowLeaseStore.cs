using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Infrastructure.Data.Workflow;

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
        return row is null ? null : Deserialize(row.State);
    }

    public async Task SaveAsync(string key, WorkLease state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowLeases.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowLeases.Add(new WorkflowLeaseRow { WorkflowRunId = key, State = json });
        else
            row.State = json;
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

    public Task<IReadOnlyList<WorkLease>> ListAsync() => throw new NotImplementedException();

    internal static WorkLease? Deserialize(string json) => WorkflowLeaseJson.Deserialize(json);

    internal static string Serialize(WorkLease state) => WorkflowLeaseJson.Serialize(state);
}
