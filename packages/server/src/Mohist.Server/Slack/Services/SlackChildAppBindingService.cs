using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class SlackChildAppBindingService : IScopedService
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISlackChildAppBindingPort _connections;
    private readonly TimeProvider _timeProvider;

    public SlackChildAppBindingService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISlackChildAppBindingPort connections,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _connections = connections;
        _timeProvider = timeProvider;
    }

    public Task<SlackChildAppBindingResult> HandleVerifiedAuthorizationAsync(
        string childAppId,
        CancellationToken ct = default) => ReconcileAsync(childAppId, ct);

    public async Task<IReadOnlyList<SlackChildAppBindingResult>> ProcessPendingAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pending = await db.SlackChildAppBindingObligations.AsNoTracking()
            .Where(item => item.Status != SlackChildAppBindingObligationStatus.Bound)
            .Select(item => new { item.ChildAppId, item.UpdatedAt })
            .ToListAsync(ct);
        var childIds = pending
            .OrderBy(item => item.UpdatedAt)
            .Select(item => item.ChildAppId)
            .ToList();
        var results = new List<SlackChildAppBindingResult>(childIds.Count);
        foreach (var childId in childIds)
            results.Add(await ReconcileAsync(childId, ct));
        return results;
    }

    public async Task<SlackChildAppBindingResult> ReconcileAsync(string childAppId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAppId);
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var child = await db.ManagedSlackAgentApps.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == childAppId, ct);
        if (child is null) return SlackChildAppBindingResult.NotFound;
        if (child.AppLifecycle != SlackAppLifecycle.Created
            || child.Authorization != SlackAuthorizationState.Authorized
            || string.IsNullOrWhiteSpace(child.AppId)
            || string.IsNullOrWhiteSpace(child.BotUserId))
            return SlackChildAppBindingResult.NotVerified;

        var obligation = await db.SlackChildAppBindingObligations
            .SingleOrDefaultAsync(item => item.ChildAppId == childAppId, ct);
        if (obligation is null)
        {
            obligation = new SlackChildAppBindingObligationRow
            {
                Id = $"bind_obligation_{Guid.NewGuid():N}",
                ChildAppId = child.Id,
                AgentConnectionId = child.AgentConnectionId,
                Status = SlackChildAppBindingObligationStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.SlackChildAppBindingObligations.Add(obligation);
            await db.SaveChangesAsync(ct);
        }

        var connection = await db.AgentConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == child.AgentConnectionId, ct);
        var connectionDeleted = connection is null || connection.DeletedAt is not null;
        var staleClaim = obligation.Status == SlackChildAppBindingObligationStatus.InProgress
            && obligation.LastAttemptAt is not null
            && obligation.LastAttemptAt.Value <= now - ClaimLease;
        if (obligation.Status == SlackChildAppBindingObligationStatus.Bound)
        {
            if (connectionDeleted)
                return await RecordConnectionDeletedAfterBoundAsync(childAppId, ct);
            return SlackChildAppBindingResult.Bound;
        }
        if (obligation.Status == SlackChildAppBindingObligationStatus.InProgress && !staleClaim)
            return SlackChildAppBindingResult.InProgress;

        var claimToken = Guid.NewGuid().ToString("N");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claim = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackChildAppBindingObligations"
            SET "Status" = {SlackChildAppBindingObligationStatus.InProgress},
                "AttemptCount" = "AttemptCount" + 1,
                "LastAttemptAt" = {now},
                "ClaimToken" = {claimToken},
                "FailureClass" = NULL,
                "UpdatedAt" = {now}
            WHERE "Id" = {obligation.Id}
              AND "Status" = {obligation.Status}
              AND "Status" <> {SlackChildAppBindingObligationStatus.Bound}
              AND ("Status" <> {SlackChildAppBindingObligationStatus.InProgress}
                   OR "LastAttemptAt" IS NULL
                   OR "LastAttemptAt" <= {now - ClaimLease});
            """, ct);
        if (claim == 0)
        {
            await transaction.RollbackAsync(ct);
            return SlackChildAppBindingResult.InProgress;
        }

        SlackStateTransitions.RequireBindingTransition(child.BindingState, SlackChildAppBindingState.InProgress);
        var childClaim = await db.ManagedSlackAgentApps
            .Where(item => item.Id == childAppId && item.BindingState == child.BindingState)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackChildAppBindingState.InProgress)
                .SetProperty(item => item.BindingErrorClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childClaim != 1)
        {
            await transaction.RollbackAsync(ct);
            return SlackChildAppBindingResult.InProgress;
        }

        await transaction.CommitAsync(ct);

        if (connection is null || connection.DeletedAt is not null)
            return await RecordFailureAsync(childAppId, claimToken, SlackChildAppBindingState.ConnectionDeleted, "connection_deleted", ct);

        try
        {
            var bound = await _connections.BindSlackIdentityAsync(
                connection.ProjectId,
                connection.Id,
                child.WorkspaceTeamId,
                child.AppId,
                child.BotUserId,
                botName: null,
                ct: ct,
                claimToken: claimToken);
            if (bound is null)
                return await RecordFailureAsync(childAppId, claimToken, SlackChildAppBindingState.ConnectionDeleted, "connection_deleted", ct);
            return await RecordSuccessAsync(childAppId, claimToken, ct);
        }
        catch (AgentConnectionDuplicateException)
        {
            return await RecordFailureAsync(childAppId, claimToken, SlackChildAppBindingState.Conflict, "connection_identity_conflict", ct);
        }
        catch (AgentConnectionValidationException ex) when (ex.Code is "immutable_binding" or "team_mismatch" or "invalid_staged_binding")
        {
            return await RecordFailureAsync(childAppId, claimToken, SlackChildAppBindingState.Conflict, ex.Code, ct);
        }
        catch (AgentConnectionValidationException ex) when (ex.Code == "stale_binding_claim")
        {
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }
    }

    private async Task<SlackChildAppBindingResult> RecordSuccessAsync(
        string childAppId,
        string claimToken,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var obligationChanged = await db.SlackChildAppBindingObligations
            .Where(item => item.ChildAppId == childAppId
                && item.Status == SlackChildAppBindingObligationStatus.InProgress
                && item.ClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, SlackChildAppBindingObligationStatus.Bound)
                .SetProperty(item => item.FailureClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (obligationChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }

        var childChanged = await db.ManagedSlackAgentApps
            .Where(item => item.Id == childAppId && item.BindingState == SlackChildAppBindingState.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackChildAppBindingState.Bound)
                .SetProperty(item => item.BindingErrorClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }

        await transaction.CommitAsync(ct);
        return SlackChildAppBindingResult.Bound;
    }

    private async Task<SlackChildAppBindingResult> RecordFailureAsync(
        string childAppId,
        string claimToken,
        string state,
        string errorClass,
        CancellationToken ct)
    {
        SlackStateTransitions.RequireBindingTransition(SlackChildAppBindingState.InProgress, state);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var obligationChanged = await db.SlackChildAppBindingObligations
            .Where(item => item.ChildAppId == childAppId
                && item.Status == SlackChildAppBindingObligationStatus.InProgress
                && item.ClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, state)
                .SetProperty(item => item.FailureClass, errorClass)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (obligationChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }

        var childChanged = await db.ManagedSlackAgentApps
            .Where(item => item.Id == childAppId && item.BindingState == SlackChildAppBindingState.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, state)
                .SetProperty(item => item.BindingErrorClass, errorClass)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }

        await transaction.CommitAsync(ct);
        return state == SlackChildAppBindingState.ConnectionDeleted
            ? SlackChildAppBindingResult.ConnectionDeleted
            : SlackChildAppBindingResult.Conflict(errorClass);
    }

    private async Task<SlackChildAppBindingResult> RecordConnectionDeletedAfterBoundAsync(
        string childAppId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var obligationChanged = await db.SlackChildAppBindingObligations
            .Where(item => item.ChildAppId == childAppId
                && item.Status == SlackChildAppBindingObligationStatus.Bound)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, SlackChildAppBindingObligationStatus.ConnectionDeleted)
                .SetProperty(item => item.FailureClass, "connection_deleted")
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (obligationChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }

        var childChanged = await db.ManagedSlackAgentApps
            .Where(item => item.Id == childAppId && item.BindingState == SlackChildAppBindingState.Bound)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackChildAppBindingState.ConnectionDeleted)
                .SetProperty(item => item.BindingErrorClass, "connection_deleted")
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, childAppId, ct);
        }

        await transaction.CommitAsync(ct);
        return SlackChildAppBindingResult.ConnectionDeleted;
    }

    private static async Task<SlackChildAppBindingResult> ReadCurrentResultAsync(
        MohistDbContext db,
        string childAppId,
        CancellationToken ct)
    {
        var current = await db.ManagedSlackAgentApps.AsNoTracking()
            .Where(item => item.Id == childAppId)
            .Select(item => new { item.BindingState, item.BindingErrorClass })
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return SlackChildAppBindingResult.NotFound;
        return current.BindingState switch
        {
            SlackChildAppBindingState.Bound => SlackChildAppBindingResult.Bound,
            SlackChildAppBindingState.ConnectionDeleted => SlackChildAppBindingResult.ConnectionDeleted,
            SlackChildAppBindingState.Conflict => SlackChildAppBindingResult.Conflict(current.BindingErrorClass ?? "connection_identity_conflict"),
            _ => SlackChildAppBindingResult.InProgress,
        };
    }
}

public sealed record SlackChildAppBindingResult(
    SlackChildAppBindingStatus Status,
    string? ErrorClass = null)
{
    public static SlackChildAppBindingResult NotFound { get; } = new(SlackChildAppBindingStatus.NotFound);
    public static SlackChildAppBindingResult NotVerified { get; } = new(SlackChildAppBindingStatus.NotVerified);
    public static SlackChildAppBindingResult InProgress { get; } = new(SlackChildAppBindingStatus.InProgress);
    public static SlackChildAppBindingResult Bound { get; } = new(SlackChildAppBindingStatus.Bound);
    public static SlackChildAppBindingResult ConnectionDeleted { get; } = new(SlackChildAppBindingStatus.ConnectionDeleted);
    public static SlackChildAppBindingResult Conflict(string errorClass) => new(SlackChildAppBindingStatus.Conflict, errorClass);
}

public enum SlackChildAppBindingStatus
{
    Bound,
    NotFound,
    NotVerified,
    InProgress,
    ConnectionDeleted,
    Conflict,
}
