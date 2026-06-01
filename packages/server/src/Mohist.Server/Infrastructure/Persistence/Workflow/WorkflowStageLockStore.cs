using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Persistence.Workflow;

public class WorkflowStageLockStore : IStateStore<WorkflowStageLockState>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowStageLockStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowStageLockState?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowStageLocks.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, WorkflowStageLockState state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowStageLocks.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowStageLocks.Add(new WorkflowStageLockRow { Key = key, StateJson = json });
        else
            row.StateJson = json;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowStageLocks.FindAsync(key);
        if (row is null) return;
        db.WorkflowStageLocks.Remove(row);
        await db.SaveChangesAsync();
    }

    public Task<IReadOnlyList<WorkflowStageLockState>> ListAsync() => throw new NotSupportedException();

    private static WorkflowStageLockState Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowStageLockState>(json)!;

    private static string Serialize(WorkflowStageLockState state) =>
        JsonSerializer.Serialize(state);
}
