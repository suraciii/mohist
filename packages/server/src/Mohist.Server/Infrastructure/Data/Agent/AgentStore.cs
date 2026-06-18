using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.Infrastructure.Data.Agent;

public class AgentStore : IStateStore<DomainAgent>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<DomainAgent?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Agents.FindAsync(key);
        return row is null ? null : Deserialize(row.State);
    }

    public async Task SaveAsync(string key, DomainAgent state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Agents.FindAsync(key);
        var json = Serialize(state);
        if (row is null)
            db.Agents.Add(new AgentRow { Id = key, State = json });
        else
            row.State = json;
        await db.SaveChangesAsync();
    }

    public Task DeleteAsync(string key) => throw new NotImplementedException();

    public Task<IReadOnlyList<DomainAgent>> ListAsync() => throw new NotImplementedException();

    public static DomainAgent? Deserialize(string json) => JSON.Deserialize<DomainAgent>(json);

    public static string Serialize(DomainAgent agent) => JSON.Serialize(agent);
}
