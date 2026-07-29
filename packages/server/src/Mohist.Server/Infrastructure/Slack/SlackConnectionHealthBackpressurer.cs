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
}
