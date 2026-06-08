using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public class WorkflowBacklogStore : IStateStore<WorkflowBacklogState>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowBacklogStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowBacklogState?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.BacklogStates.FindAsync(key);
        return row is null ? null : Deserialize(row.State);
    }

    public async Task SaveAsync(string key, WorkflowBacklogState state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.BacklogStates.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
        {
            db.BacklogStates.Add(new BacklogStateRow { ProjectId = key, State = json });
        }
        else
        {
            row.State = json;
        }
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.BacklogStates.FindAsync(key);
        if (row is null) return;

        db.BacklogStates.Remove(row);
        await db.SaveChangesAsync();
    }

    public Task<IReadOnlyList<WorkflowBacklogState>> ListAsync() => throw new NotImplementedException();

    private static WorkflowBacklogState Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowBacklogState>(json)!;

    private static string Serialize(WorkflowBacklogState state) =>
        JsonSerializer.Serialize(state);
}
