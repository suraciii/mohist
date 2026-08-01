using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackThreadLaunchReservationStore : IScopedService, IAgentConnectionProviderCleanup
{
    internal static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackThreadLaunchReservationStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<SlackThreadLaunchReservation> ReserveAsync(
        string projectId,
        string workspaceTeamId,
        string connectionId,
        string conversationId,
        string threadTs,
        string launchMessageTs,
        string slackUserId,
        CancellationToken ct = default)
    {
        Validate(projectId, workspaceTeamId, connectionId, conversationId, threadTs, launchMessageTs, slackUserId);

        var now = _timeProvider.GetUtcNow();
        var staleCutoff = now - StaleThreshold;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "SlackThreadLaunchReservations"
            WHERE "ConnectionId" = {connectionId}
              AND "WorkspaceTeamId" = {workspaceTeamId}
              AND "ConversationId" = {conversationId}
              AND "ThreadTs" = {threadTs}
              AND "SessionId" IS NULL
              AND "CreatedAt" < {staleCutoff};
            INSERT INTO "SlackThreadLaunchReservations" (
                "Id", "ProjectId", "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs",
                "LaunchMessageTs", "SlackUserId", "SessionId", "CreatedAt", "UpdatedAt")
            VALUES (
                {$"slkthrlaunch_{Guid.NewGuid():N}"}, {projectId}, {connectionId}, {workspaceTeamId},
                {conversationId}, {threadTs}, {launchMessageTs}, {slackUserId}, NULL, {now}, {now})
            ON CONFLICT("ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs") DO NOTHING;
            """, ct);

        var row = await db.SlackThreadLaunchReservations.AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId
                && candidate.ConnectionId == connectionId
                && candidate.WorkspaceTeamId == workspaceTeamId
                && candidate.ConversationId == conversationId
                && candidate.ThreadTs == threadTs)
            .Select(candidate => new
            {
                candidate.LaunchMessageTs,
                candidate.SessionId,
            })
            .SingleAsync(ct);

        var ownsLaunch = string.Equals(row.LaunchMessageTs, launchMessageTs, StringComparison.Ordinal);
        var kind = ownsLaunch
            ? SlackThreadLaunchReservationKind.Owner
            : row.SessionId is null
                ? SlackThreadLaunchReservationKind.InProgress
                : SlackThreadLaunchReservationKind.Bound;
        return new SlackThreadLaunchReservation(kind, row.SessionId, row.LaunchMessageTs);
    }

    public async Task<string> BindSessionAsync(
        string projectId,
        string workspaceTeamId,
        string connectionId,
        string conversationId,
        string threadTs,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));
        Validate(projectId, workspaceTeamId, connectionId, conversationId, threadTs, "launch-message", "slack-user");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await db.SlackThreadLaunchReservations
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.ThreadTs == threadTs
                && row.SessionId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.SessionId, sessionId)
                .SetProperty(row => row.UpdatedAt, now), ct);

        var stored = await db.SlackThreadLaunchReservations.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.ThreadTs == threadTs)
            .Select(row => row.SessionId)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(stored))
            throw new InvalidOperationException("Slack thread launch reservation does not exist.");
        return stored;
    }

    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackThreadLaunchReservations
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }

    private static void Validate(
        string projectId,
        string workspaceTeamId,
        string connectionId,
        string conversationId,
        string threadTs,
        string launchMessageTs,
        string slackUserId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(workspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(workspaceTeamId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentException("ConversationId is required.", nameof(conversationId));
        if (string.IsNullOrWhiteSpace(threadTs))
            throw new ArgumentException("ThreadTs is required.", nameof(threadTs));
        if (string.IsNullOrWhiteSpace(launchMessageTs))
            throw new ArgumentException("LaunchMessageTs is required.", nameof(launchMessageTs));
        if (string.IsNullOrWhiteSpace(slackUserId))
            throw new ArgumentException("SlackUserId is required.", nameof(slackUserId));
    }
}

public enum SlackThreadLaunchReservationKind
{
    Owner,
    InProgress,
    Bound,
}

public sealed record SlackThreadLaunchReservation(
    SlackThreadLaunchReservationKind Kind,
    string? SessionId,
    string LaunchMessageTs);
