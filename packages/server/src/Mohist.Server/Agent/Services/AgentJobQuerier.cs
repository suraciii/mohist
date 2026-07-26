using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public class AgentJobQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentJobQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<AgentJobListItem>> ListByAgentAsync(
        string projectId,
        string agentId,
        IReadOnlyCollection<AgentJobStatus>? statusSet = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var clampedLimit = Math.Clamp(limit, 1, 200);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.AgentJobs
            .AsNoTracking()
            .Where(r => r.AgentId == agentId && r.ProjectId == projectId);

        if (statusSet is { Count: > 0 })
        {
            var statuses = statusSet.Select(s => s.ToString().ToLowerInvariant()).ToList();
            query = query.Where(r => statuses.Contains(r.Status!));
        }

        var rows = await query
            .OrderByDescending(r => r.SubmittedAt)
            .ThenByDescending(r => r.JobKey)
            .Take(clampedLimit)
            .ToListAsync(ct);

        return rows.Select(ToItem).ToList();
    }

    public async Task<AgentJobListItem?> GetByKeyAsync(string jobKey, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.JobKey == jobKey, ct);
        return row is null ? null : ToItem(row);
    }

    private static AgentJobListItem ToItem(AgentJobRow row) => new(
        row.JobKey,
        row.AgentId,
        row.Status,
        row.SubmittedAt,
        row.TerminalAt);
}

public sealed record AgentJobListItem(
    string JobKey,
    string? AgentId,
    string? Status,
    string? SubmittedAt,
    string? TerminalAt);
