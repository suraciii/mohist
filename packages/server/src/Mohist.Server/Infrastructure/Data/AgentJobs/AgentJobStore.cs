using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.AgentJobs;

public interface IAgentJobStore
{
    Task<string?> LoadAsync(string key);
    Task SaveAsync(string key, string stateJson);
}

public class AgentJobStore : IAgentJobStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<AgentJobStore> _log;

    public AgentJobStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<AgentJobStore> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    public async Task<string?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AgentJobs.AsNoTracking()
            .Where(r => r.JobKey == key)
            .Select(r => r.State)
            .FirstOrDefaultAsync();
        return row;
    }

    public async Task SaveAsync(string key, string stateJson)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = new AgentJobRow { JobKey = key, State = stateJson };
        var existing = await db.AgentJobs.FindAsync(key);
        if (existing is null)
        {
            db.AgentJobs.Add(row);
        }
        else
        {
            existing.State = stateJson;
        }
        await db.SaveChangesAsync();
    }
}
