using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class WatchEntryStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentQuerier _agentQuerier;
    private readonly TimeProvider _timeProvider;

    public WatchEntryStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentQuerier agentQuerier,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _agentQuerier = agentQuerier;
        _timeProvider = timeProvider;
    }

    public async Task<WatchEntry> AddAsync(string projectId, int issueNumber, string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await ValidateActiveAgentAsync(projectId, agentId, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WatchEntries.FirstOrDefaultAsync(
            entry => entry.ProjectId == projectId && entry.IssueNumber == issueNumber && entry.AgentId == agentId,
            ct);
        var now = _timeProvider.GetUtcNow();
        if (existing is null)
        {
            var row = new WatchEntryRow
            {
                ProjectId = projectId,
                IssueNumber = issueNumber,
                AgentId = agentId,
                State = WatchEntryState.Watching,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WatchEntries.Add(row);
            await db.SaveChangesAsync(ct);
            return ToDomain(row);
        }
        if (existing.State == WatchEntryState.Watching)
            return ToDomain(existing);
        existing.State = WatchEntryState.Watching;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToDomain(existing);
    }

    public async Task<WatchEntry?> RemoveAsync(string projectId, int issueNumber, string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await ValidateActiveAgentAsync(projectId, agentId, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WatchEntries.FirstOrDefaultAsync(
            entry => entry.ProjectId == projectId && entry.IssueNumber == issueNumber && entry.AgentId == agentId,
            ct);
        var now = _timeProvider.GetUtcNow();
        if (existing is null)
        {
            var row = new WatchEntryRow
            {
                ProjectId = projectId,
                IssueNumber = issueNumber,
                AgentId = agentId,
                State = WatchEntryState.Muted,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WatchEntries.Add(row);
            await db.SaveChangesAsync(ct);
            return ToDomain(row);
        }
        if (existing.State == WatchEntryState.Muted)
            return ToDomain(existing);
        db.WatchEntries.Remove(existing);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<WatchEntryGroups> ListAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WatchEntries.AsNoTracking()
            .Where(entry => entry.ProjectId == projectId && entry.IssueNumber == issueNumber)
            .ToListAsync(ct);
        var watching = rows
            .Where(row => row.State == WatchEntryState.Watching)
            .Select(ToDomain)
            .ToList();
        var muted = rows
            .Where(row => row.State == WatchEntryState.Muted)
            .Select(ToDomain)
            .ToList();
        return new WatchEntryGroups(watching, muted);
    }

    private async Task ValidateActiveAgentAsync(string projectId, string agentId, CancellationToken ct)
    {
        var agent = await _agentQuerier.GetByIdAsync(projectId, agentId);
        if (agent is null)
            throw new WatchEntryValidationException($"Agent '{agentId}' was not found in project '{projectId}'.", "agent_not_found");
        if (!string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
            throw new WatchEntryValidationException($"Agent '{agentId}' is archived.", "agent_archived");
    }

    private static WatchEntry ToDomain(WatchEntryRow row) => new()
    {
        ProjectId = row.ProjectId,
        IssueNumber = row.IssueNumber,
        AgentId = row.AgentId,
        State = row.State,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}

public sealed record WatchEntryGroups(IReadOnlyList<WatchEntry> Watching, IReadOnlyList<WatchEntry> Muted);

public sealed class WatchEntryValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
