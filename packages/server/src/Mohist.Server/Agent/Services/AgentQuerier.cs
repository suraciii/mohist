using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public class AgentQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentReadinessService? _readiness;

    public AgentQuerier(IDbContextFactory<MohistDbContext> dbFactory, AgentReadinessService? readiness = null)
    {
        _dbFactory = dbFactory;
        _readiness = readiness;
    }

    public async Task<AgentInfo?> GetByIdAsync(string projectId, string id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Agents.AsNoTracking()
            .Where(agent => agent.ProjectId == projectId)
            .ToListAsync();
        var agent = rows
            .Select(row => AgentStore.Deserialize(row.State))
            .Where(agent => agent is not null)
            .Cast<Domain.Agent>()
            .Where(agent => agent.ProjectId == projectId && agent.Id == id)
            .Select(ToInfo)
            .FirstOrDefault();
        return agent is null || _readiness is null
            ? agent
            : agent with { Readiness = await _readiness.GetAsync(projectId, agent) };
    }

    /// <summary>
    /// Resolves an Agent by name within a project, case-insensitively.
    /// Mention resolution (<c>@SuperVisor</c> → Agent named <c>supervisor</c>)
    /// and the Agent-name uniqueness check both go through this path, so both treat
    /// name equality as ordinal-ignorecase. Matches the client-side filter
    /// shape already used by <see cref="GetByIdAsync"/>: the rows are pulled
    /// by project, deserialized, and filtered in memory, so the comparison
    /// is the same on SQLite (default case-sensitive <c>=</c>) and Postgres.
    /// </summary>
    public async Task<AgentInfo?> GetByNameAsync(string projectId, string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Agents.AsNoTracking()
            .Where(agent => agent.ProjectId == projectId)
            .ToListAsync();
        var agent = rows
            .Select(row => AgentStore.Deserialize(row.State))
            .Where(agent => agent is not null)
            .Cast<Domain.Agent>()
            .Where(agent => agent.ProjectId == projectId
                && string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(ToInfo)
            .FirstOrDefault();
        return agent is null || _readiness is null
            ? agent
            : agent with { Readiness = await _readiness.GetAsync(projectId, agent) };
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(
        string projectId,
        string? status = null,
        bool all = false,
        CancellationToken ct = default)
    {
        var infos = await ListDefinitionsAsync(projectId, status, all, ct);
        if (_readiness is null)
            return infos;
        var hydrated = new List<AgentInfo>(infos.Count);
        foreach (var info in infos)
        {
            hydrated.Add(info with { Readiness = await _readiness.GetAsync(projectId, info, ct) });
        }
        return hydrated;
    }

    public Task<IReadOnlyList<AgentInfo>> ListActiveDefinitionsAsync(
        string projectId,
        CancellationToken ct = default) =>
        ListDefinitionsAsync(projectId, AgentStatus.Active, all: false, ct);

    private async Task<IReadOnlyList<AgentInfo>> ListDefinitionsAsync(
        string projectId,
        string? status,
        bool all,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Agents.AsNoTracking().Where(agent => agent.ProjectId == projectId);

        if (!all)
            query = query.Where(agent => agent.Status == (status ?? AgentStatus.Active));
        else if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(agent => agent.Status == status);

        var rows = await query.ToListAsync(ct);
        var infos = rows
            .Select(row => AgentStore.Deserialize(row.State))
            .Where(agent => agent is not null)
            .Cast<Domain.Agent>()
            .OrderByDescending(agent => agent.UpdatedAt)
            .Select(ToInfo)
            .ToList();
        return infos;
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
