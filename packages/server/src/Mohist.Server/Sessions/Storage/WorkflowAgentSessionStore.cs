using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Sessions.Storage;

public class WorkflowAgentSessionStore : IStateStore<WorkflowAgentSession>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowAgentSessionStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowAgentSession?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowAgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key);
    }

    public async Task<IReadOnlyList<WorkflowAgentSession>> ListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowAgentSessions.AsNoTracking().ToListAsync();
    }

    public async Task SaveAsync(string key, WorkflowAgentSession state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        state.Id = key;
        db.WorkflowAgentSessions.Update(state);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.WorkflowAgentSessions.FindAsync(key);
        if (session is not null)
        {
            db.WorkflowAgentSessions.Remove(session);
            await db.SaveChangesAsync();
        }
    }
}