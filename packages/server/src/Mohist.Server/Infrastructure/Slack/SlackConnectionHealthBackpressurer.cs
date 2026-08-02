using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackConnectionHealthBackpressurer : IScopedService, ISlackConnectionHealthBackpressurer
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackConnectionHealthBackpressurer(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task FlipBackpressuredAsync(
        string projectId,
        string connectionId,
        string reason,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.AgentConnections
            .Where(row => row.ProjectId == projectId
                && row.Id == connectionId
                && row.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Degraded)
                .SetProperty(row => row.HealthReason, reason)
                .SetProperty(row => row.UpdatedAt, _timeProvider.GetUtcNow()), ct);
    }

    public async Task<int> RecoverBackpressuredAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AgentConnections
            .Where(row => row.ProjectId == projectId
                && row.Id == connectionId
                && row.DeletedAt == null
                && row.ConnectionHealth == ConnectionHealthKind.Degraded
                && (row.HealthReason == SlackProviderBackpressureReasons.InboxOverflow
                    || row.HealthReason == SlackProviderBackpressureReasons.OutboxOverflow))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ConnectionHealth, ConnectionHealthKind.Healthy)
                .SetProperty(row => row.HealthReason, (string?)null)
                .SetProperty(row => row.UpdatedAt, _timeProvider.GetUtcNow()), ct);
    }
}
