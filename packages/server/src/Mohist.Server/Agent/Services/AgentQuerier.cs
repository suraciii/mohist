using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public class AgentQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentReadinessService? _readiness;

    public AgentQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentReadinessService? readiness = null)
    {
        _dbFactory = dbFactory;
        _readiness = readiness;
    }

    public async Task<AgentInfo?> GetByIdAsync(string projectId, string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Agents.AsNoTracking()
            .Where(agent => agent.ProjectId == projectId)
            .ToListAsync(ct);
        var agent = rows
            .Select(row => AgentStore.Deserialize(row.State))
            .Where(agent => agent is not null)
            .Cast<Domain.Agent>()
            .Where(agent => agent.ProjectId == projectId && agent.Id == id)
            .Select(ToInfo)
            .FirstOrDefault();
        return agent is null
            ? null
            : await HydrateAsync(projectId, agent, ct);
    }

    /// <summary>
    /// Resolves a stored Project Agent by name within a project,
    /// case-insensitively. Mention resolution (<c>@SuperVisor</c> → Agent
    /// named <c>supervisor</c>) and the Agent-name uniqueness check both go
    /// through this path, so both treat name equality as ordinal-ignorecase.
    /// Matches the client-side filter shape already used by
    /// <see cref="GetByIdAsync"/>: the rows are pulled by project,
    /// deserialized, and filtered in memory, so the comparison is the same
    /// on SQLite (default case-sensitive <c>=</c>) and Postgres. Built-in
    /// definitions are not stored rows and resolve through
    /// <see cref="GetEffectiveByNameAsync"/>.
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
        return agent is null
            ? null
            : await HydrateAsync(projectId, agent);
    }

    /// <summary>
    /// Resolves the Project's effective Agent for a name: the stored Project
    /// Agent when one exists (it shadows a built-in with the same name, also
    /// while archived), otherwise the unshadowed built-in Workflow Agent
    /// definition.
    /// </summary>
    public async Task<AgentInfo?> GetEffectiveByNameAsync(
        string projectId,
        string name,
        CancellationToken ct = default)
    {
        var stored = await GetByNameAsync(projectId, name);
        if (stored is not null) return stored;

        var definition = BuiltInAgentCatalog.FindWorkflow(name);
        return definition is null
            ? null
            : await HydrateAsync(projectId, BuiltInAgentCatalog.Resolve(definition.Name, projectId), ct);
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(
        string projectId,
        string? status = null,
        bool all = false,
        CancellationToken ct = default)
    {
        var infos = await ListDefinitionsAsync(projectId, status, all, ct);
        var hydrated = new List<AgentInfo>(infos.Count + BuiltInAgentCatalog.WorkflowDefinitions.Count);
        foreach (var info in infos)
        {
            hydrated.Add(await HydrateAsync(projectId, info, ct));
        }

        if (status is null or AgentStatus.Active)
        {
            // An archived same-name Project Agent remains the shadowing
            // entry, so the shadow set comes from every stored name rather
            // than from the filtered list.
            var shadowed = await ListStoredNamesAsync(projectId, ct);
            foreach (var definition in BuiltInAgentCatalog.WorkflowDefinitions)
            {
                if (shadowed.Contains(definition.Name)) continue;
                hydrated.Add(await HydrateAsync(
                    projectId,
                    BuiltInAgentCatalog.Resolve(definition.Name, projectId),
                    ct));
            }
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

    private async Task<HashSet<string>> ListStoredNamesAsync(string projectId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var names = await db.Agents.AsNoTracking()
            .Where(agent => agent.ProjectId == projectId)
            .Select(agent => agent.Name)
            .ToListAsync(ct);
        return new HashSet<string>(names.Where(name => name is not null)!, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<AgentInfo> HydrateAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        var effective = ExecutionConfigResolver.Resolve(
            callerHint: null,
            definition: ExecutionConfigResolver.FromAgentConfig(agent.AgentConfig));
        var hydrated = agent with
        {
            EffectiveExecutionConfig = new AgentEffectiveExecutionConfig(
                effective.Runtime,
                effective.Model,
                effective.Variant),
        };
        return _readiness is null
            ? hydrated
            : hydrated with { Executability = await _readiness.GetAsync(projectId, hydrated, ct) };
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
        agent.UpdatedAt.ToString("o"),
        AllowedSubagentAgentIds: agent.AllowedSubagentAgentIds,
        Avatar: agent.Avatar,
        Purpose: agent.Purpose,
        Permissions: agent.Permissions,
        Origin: AgentOrigins.Project,
        OverridesBuiltIn: BuiltInAgentCatalog.FindWorkflow(agent.Name) is not null);
}
