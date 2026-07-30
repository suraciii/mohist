using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure;

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

    public async Task<bool> HoldsConcurrencyPermitAsync(
        string jobKey,
        string token,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stateJson = await db.AgentJobs
            .AsNoTracking()
            .Where(row => row.JobKey == jobKey)
            .Select(row => row.State)
            .FirstOrDefaultAsync(ct);
        if (stateJson is null)
            return false;

        var state = JSON.Deserialize<AgentJobState>(stateJson);
        return state?.ConcurrencyPermitHeld == true
            && string.Equals(state.ConcurrencyPermitToken, token, StringComparison.Ordinal);
    }

    public async Task<AgentExecutionHistory?> GetLatestExecutionAsync(
        string projectId,
        string agentId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var states = await db.AgentJobs.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.AgentId == agentId && r.TerminalAt != null)
            .OrderByDescending(r => r.TerminalAt)
            .ThenByDescending(r => r.JobKey)
            .Take(20)
            .Select(r => r.State)
            .ToListAsync(ct);

        foreach (var stateJson in states)
        {
            var state = JSON.Deserialize<AgentJobState>(stateJson);
            if (state?.Input is null || state.Status is not (AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Unknown))
                continue;
            return new AgentExecutionHistory(
                state.Status,
                state.PendingSessionClose?.FailureCategory ?? state.PendingFailureEvent?.FailureCategory,
                state.Input,
                state.TerminalAt);
        }
        return null;
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

public sealed record AgentExecutionHistory(
    AgentJobStatus Status,
    string? FailureCategory,
    AgentJobInput Input,
    DateTimeOffset? TerminalAt);
