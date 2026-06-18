using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Agent.Services;

public class AgentQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AgentInfo?> GetByIdAsync(string projectId, string id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Agents.AsNoTracking()
            .Where(agent => agent.ProjectId == projectId)
            .ToListAsync();
        return rows
            .Select(row => AgentStore.Deserialize(row.State))
            .Where(agent => agent is not null)
            .Cast<Domain.Agent>()
            .Where(agent => agent.ProjectId == projectId && agent.Id == id)
            .Select(ToInfo)
            .FirstOrDefault();
    }

    public async Task<AgentInfo?> GetByNameAsync(string projectId, string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Agents.AsNoTracking()
            .FirstOrDefaultAsync(agent => agent.ProjectId == projectId && agent.Name == name);
        return row is null ? null : ToInfo(AgentStore.Deserialize(row.State)!);
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(string projectId, string? status = null, bool all = false)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Agents.AsNoTracking().Where(agent => agent.ProjectId == projectId);

        if (!all)
            query = query.Where(agent => agent.Status == (status ?? AgentStatus.Active));
        else if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(agent => agent.Status == status);

        var rows = await query.ToListAsync();
        return rows
            .Select(row => AgentStore.Deserialize(row.State))
            .Where(agent => agent is not null)
            .Cast<Domain.Agent>()
            .OrderByDescending(agent => agent.UpdatedAt)
            .Select(ToInfo)
            .ToList();
    }

    public static AgentInfo ToInfo(Domain.Agent agent) => new(
        agent.Id,
        agent.ProjectId,
        agent.Name,
        agent.Description,
        agent.Instructions,
        agent.AgentConfig?.Clone(),
        agent.Skills,
        agent.MaxConcurrentRuns,
        agent.Status,
        agent.CreatedAt.ToString("o"),
        agent.UpdatedAt.ToString("o"));
}
