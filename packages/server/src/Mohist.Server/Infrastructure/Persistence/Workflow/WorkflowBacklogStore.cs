using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Persistence.Workflow;

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
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, WorkflowBacklogState state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.BacklogStates.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
        {
            db.BacklogStates.Add(new BacklogStateRow { ProjectId = key, StateJson = json });
        }
        else
        {
            row.StateJson = json;
        }
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorkflowBacklogState>> ListAsync() => throw new NotSupportedException();

    private static WorkflowBacklogState Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowBacklogState>(json)!;

    private static string Serialize(WorkflowBacklogState state) =>
        JsonSerializer.Serialize(state);
}
