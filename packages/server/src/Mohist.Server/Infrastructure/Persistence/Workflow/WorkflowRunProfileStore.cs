using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Infrastructure.Persistence.Workflow;

public class WorkflowRunProfileStore : IStateStore<WorkflowRunProfile>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunProfileStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowRunProfile?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles.FindAsync(key);
        return row is null ? null : Deserialize(row.StateJson);
    }

    public async Task SaveAsync(string key, WorkflowRunProfile state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.WorkflowRunProfiles.Add(new WorkflowRunProfileRow { Key = key, StateJson = json });
        else
            row.StateJson = json;
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotSupportedException();
    public Task<IReadOnlyList<WorkflowRunProfile>> ListAsync() => throw new NotSupportedException();

    internal static WorkflowRunProfile? Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowRunProfile>(json);

    internal static string Serialize(WorkflowRunProfile state) =>
        JsonSerializer.Serialize(state);
}
