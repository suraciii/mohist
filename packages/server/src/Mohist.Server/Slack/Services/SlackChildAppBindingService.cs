using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class SlackChildAppBindingService : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentConnectionStore _connections;
    private readonly TimeProvider _timeProvider;

    public SlackChildAppBindingService(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentConnectionStore connections,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _connections = connections;
        _timeProvider = timeProvider;
    }

    public Task<SlackChildAppBindingResult> HandleVerifiedAuthorizationAsync(
        string childAppId,
        CancellationToken ct = default) => ReconcileAsync(childAppId, ct);

    public async Task<SlackChildAppBindingResult> ReconcileAsync(string childAppId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAppId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var child = await db.ManagedSlackChildApps.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == childAppId, ct);
        if (child is null) return SlackChildAppBindingResult.NotFound;
        if (child.AppLifecycle != SlackAppLifecycle.Created
            || child.Authorization != SlackAuthorizationState.Authorized
            || string.IsNullOrWhiteSpace(child.AppId)
            || string.IsNullOrWhiteSpace(child.BotUserId))
            return SlackChildAppBindingResult.NotVerified;

        var connection = await db.AgentConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == child.AgentConnectionId, ct);
        if (connection is null || connection.DeletedAt is not null)
            return await RecordFailureAsync(childAppId, SlackChildAppBindingState.ConnectionDeleted, "connection_deleted", ct);

        try
        {
            var bound = await _connections.BindSlackIdentityAsync(
                connection.ProjectId,
                connection.Id,
                child.WorkspaceTeamId,
                child.AppId,
                child.BotUserId,
                botName: null,
                ct);
            if (bound is null)
                return await RecordFailureAsync(childAppId, SlackChildAppBindingState.ConnectionDeleted, "connection_deleted", ct);
            return await RecordSuccessAsync(childAppId, ct);
        }
        catch (AgentConnectionDuplicateException)
        {
            return await RecordFailureAsync(childAppId, SlackChildAppBindingState.Conflict, "connection_identity_conflict", ct);
        }
        catch (AgentConnectionValidationException ex) when (ex.Code is "immutable_binding" or "team_mismatch" or "invalid_staged_binding")
        {
            return await RecordFailureAsync(childAppId, SlackChildAppBindingState.Conflict, ex.Code, ct);
        }
    }

    private async Task<SlackChildAppBindingResult> RecordSuccessAsync(string childAppId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var changed = await db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackChildAppBindingState.Bound)
                .SetProperty(item => item.BindingErrorClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        return changed == 1 ? SlackChildAppBindingResult.Bound : SlackChildAppBindingResult.NotFound;
    }

    private async Task<SlackChildAppBindingResult> RecordFailureAsync(
        string childAppId,
        string state,
        string errorClass,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var changed = await db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, state)
                .SetProperty(item => item.BindingErrorClass, errorClass)
                .SetProperty(item => item.UpdatedAt, now), ct);
        return changed == 1
            ? state == SlackChildAppBindingState.ConnectionDeleted
                ? SlackChildAppBindingResult.ConnectionDeleted
                : SlackChildAppBindingResult.Conflict(errorClass)
            : SlackChildAppBindingResult.NotFound;
    }
}

public sealed record SlackChildAppBindingResult(
    SlackChildAppBindingStatus Status,
    string? ErrorClass = null)
{
    public static SlackChildAppBindingResult NotFound { get; } = new(SlackChildAppBindingStatus.NotFound);
    public static SlackChildAppBindingResult NotVerified { get; } = new(SlackChildAppBindingStatus.NotVerified);
    public static SlackChildAppBindingResult Bound { get; } = new(SlackChildAppBindingStatus.Bound);
    public static SlackChildAppBindingResult ConnectionDeleted { get; } = new(SlackChildAppBindingStatus.ConnectionDeleted);
    public static SlackChildAppBindingResult Conflict(string errorClass) => new(SlackChildAppBindingStatus.Conflict, errorClass);
}

public enum SlackChildAppBindingStatus
{
    Bound,
    NotFound,
    NotVerified,
    ConnectionDeleted,
    Conflict,
}
