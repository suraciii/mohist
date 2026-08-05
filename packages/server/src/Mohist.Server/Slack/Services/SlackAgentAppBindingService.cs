using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class SlackAgentAppBindingService : IScopedService
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISlackAgentAppBindingPort _connections;
    private readonly TimeProvider _timeProvider;

    public SlackAgentAppBindingService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISlackAgentAppBindingPort connections,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _connections = connections;
        _timeProvider = timeProvider;
    }

    public Task<SlackAgentAppBindingResult> HandleVerifiedAuthorizationAsync(
        string agentAppId,
        CancellationToken ct = default) => ReconcileAsync(agentAppId, ct);

    public async Task<IReadOnlyList<SlackAgentAppBindingResult>> ProcessPendingAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pending = await db.SlackAgentAppBindingObligations.AsNoTracking()
            .Where(item => item.Status != SlackAgentAppBindingObligationStatus.Bound)
            .Select(item => new { item.AgentAppId, item.UpdatedAt })
            .ToListAsync(ct);
        var childIds = pending
            .OrderBy(item => item.UpdatedAt)
            .Select(item => item.AgentAppId)
            .ToList();
        var results = new List<SlackAgentAppBindingResult>(childIds.Count);
        foreach (var childId in childIds)
            results.Add(await ReconcileAsync(childId, ct));
        return results;
    }

    public async Task<SlackAgentAppBindingResult> ReconcileAsync(string agentAppId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentAppId);
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var agentApp = await db.ManagedSlackAgentApps.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == agentAppId, ct);
        if (agentApp is null) return SlackAgentAppBindingResult.NotFound;
        if (agentApp.AppLifecycle != SlackAppLifecycle.Created
            || agentApp.Authorization != SlackAuthorizationState.Authorized
            || string.IsNullOrWhiteSpace(agentApp.AppId)
            || string.IsNullOrWhiteSpace(agentApp.BotUserId))
            return SlackAgentAppBindingResult.NotVerified;

        var obligation = await db.SlackAgentAppBindingObligations
            .SingleOrDefaultAsync(item => item.AgentAppId == agentAppId, ct);
        if (obligation is null)
        {
            obligation = new SlackAgentAppBindingObligationRow
            {
                Id = $"bind_obligation_{Guid.NewGuid():N}",
                AgentAppId = agentApp.Id,
                AgentConnectionId = agentApp.AgentConnectionId,
                Status = SlackAgentAppBindingObligationStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.SlackAgentAppBindingObligations.Add(obligation);
            await db.SaveChangesAsync(ct);
        }

        var connection = await db.AgentConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == agentApp.AgentConnectionId, ct);
        var connectionDeleted = connection is null || connection.DeletedAt is not null;
        var staleClaim = obligation.Status == SlackAgentAppBindingObligationStatus.InProgress
            && obligation.LastAttemptAt is not null
            && obligation.LastAttemptAt.Value <= now - ClaimLease;
        if (obligation.Status == SlackAgentAppBindingObligationStatus.Bound)
        {
            if (connectionDeleted)
                return await RecordConnectionDeletedAfterBoundAsync(agentAppId, ct);
            return SlackAgentAppBindingResult.Bound;
        }
        if (obligation.Status == SlackAgentAppBindingObligationStatus.InProgress && !staleClaim)
            return SlackAgentAppBindingResult.InProgress;

        var claimToken = Guid.NewGuid().ToString("N");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claim = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackAgentAppBindingObligations"
            SET "Status" = {SlackAgentAppBindingObligationStatus.InProgress},
                "AttemptCount" = "AttemptCount" + 1,
                "LastAttemptAt" = {now},
                "ClaimToken" = {claimToken},
                "FailureClass" = NULL,
                "UpdatedAt" = {now}
            WHERE "Id" = {obligation.Id}
              AND "Status" = {obligation.Status}
              AND "Status" <> {SlackAgentAppBindingObligationStatus.Bound}
              AND ("Status" <> {SlackAgentAppBindingObligationStatus.InProgress}
                   OR "LastAttemptAt" IS NULL
                   OR "LastAttemptAt" <= {now - ClaimLease});
            """, ct);
        if (claim == 0)
        {
            await transaction.RollbackAsync(ct);
            return SlackAgentAppBindingResult.InProgress;
        }

        SlackStateTransitions.RequireBindingTransition(agentApp.BindingState, SlackAgentAppBindingState.InProgress);
        var childClaim = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId && item.BindingState == agentApp.BindingState)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackAgentAppBindingState.InProgress)
                .SetProperty(item => item.BindingErrorClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childClaim != 1)
        {
            await transaction.RollbackAsync(ct);
            return SlackAgentAppBindingResult.InProgress;
        }

        await transaction.CommitAsync(ct);

        if (connection is null || connection.DeletedAt is not null)
            return await RecordFailureAsync(agentAppId, claimToken, SlackAgentAppBindingState.ConnectionDeleted, "connection_deleted", ct);

        try
        {
            var bound = await _connections.BindSlackIdentityAsync(
                connection.ProjectId,
                connection.Id,
                agentApp.WorkspaceTeamId,
                agentApp.AppId,
                agentApp.BotUserId,
                botName: null,
                ct: ct,
                claimToken: claimToken);
            if (bound is null)
                return await RecordFailureAsync(agentAppId, claimToken, SlackAgentAppBindingState.ConnectionDeleted, "connection_deleted", ct);
            return await RecordSuccessAsync(agentAppId, claimToken, ct);
        }
        catch (AgentConnectionDuplicateException)
        {
            return await RecordFailureAsync(agentAppId, claimToken, SlackAgentAppBindingState.Conflict, "connection_identity_conflict", ct);
        }
        catch (AgentConnectionValidationException ex) when (ex.Code is "immutable_binding" or "team_mismatch" or "invalid_staged_binding")
        {
            return await RecordFailureAsync(agentAppId, claimToken, SlackAgentAppBindingState.Conflict, ex.Code, ct);
        }
        catch (AgentConnectionValidationException ex) when (ex.Code == "stale_binding_claim")
        {
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }
    }

    private async Task<SlackAgentAppBindingResult> RecordSuccessAsync(
        string agentAppId,
        string claimToken,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var obligationChanged = await db.SlackAgentAppBindingObligations
            .Where(item => item.AgentAppId == agentAppId
                && item.Status == SlackAgentAppBindingObligationStatus.InProgress
                && item.ClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, SlackAgentAppBindingObligationStatus.Bound)
                .SetProperty(item => item.FailureClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (obligationChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }

        var childChanged = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId && item.BindingState == SlackAgentAppBindingState.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackAgentAppBindingState.Bound)
                .SetProperty(item => item.BindingErrorClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }

        await transaction.CommitAsync(ct);
        return SlackAgentAppBindingResult.Bound;
    }

    private async Task<SlackAgentAppBindingResult> RecordFailureAsync(
        string agentAppId,
        string claimToken,
        string state,
        string errorClass,
        CancellationToken ct)
    {
        SlackStateTransitions.RequireBindingTransition(SlackAgentAppBindingState.InProgress, state);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var obligationChanged = await db.SlackAgentAppBindingObligations
            .Where(item => item.AgentAppId == agentAppId
                && item.Status == SlackAgentAppBindingObligationStatus.InProgress
                && item.ClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, state)
                .SetProperty(item => item.FailureClass, errorClass)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (obligationChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }

        var childChanged = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId && item.BindingState == SlackAgentAppBindingState.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, state)
                .SetProperty(item => item.BindingErrorClass, errorClass)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }

        await transaction.CommitAsync(ct);
        return state == SlackAgentAppBindingState.ConnectionDeleted
            ? SlackAgentAppBindingResult.ConnectionDeleted
            : SlackAgentAppBindingResult.Conflict(errorClass);
    }

    private async Task<SlackAgentAppBindingResult> RecordConnectionDeletedAfterBoundAsync(
        string agentAppId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var obligationChanged = await db.SlackAgentAppBindingObligations
            .Where(item => item.AgentAppId == agentAppId
                && item.Status == SlackAgentAppBindingObligationStatus.Bound)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, SlackAgentAppBindingObligationStatus.ConnectionDeleted)
                .SetProperty(item => item.FailureClass, "connection_deleted")
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (obligationChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }

        var childChanged = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId && item.BindingState == SlackAgentAppBindingState.Bound)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.BindingState, SlackAgentAppBindingState.ConnectionDeleted)
                .SetProperty(item => item.BindingErrorClass, "connection_deleted")
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (childChanged != 1)
        {
            await transaction.RollbackAsync(ct);
            return await ReadCurrentResultAsync(db, agentAppId, ct);
        }

        await transaction.CommitAsync(ct);
        return SlackAgentAppBindingResult.ConnectionDeleted;
    }

    private static async Task<SlackAgentAppBindingResult> ReadCurrentResultAsync(
        MohistDbContext db,
        string agentAppId,
        CancellationToken ct)
    {
        var current = await db.ManagedSlackAgentApps.AsNoTracking()
            .Where(item => item.Id == agentAppId)
            .Select(item => new { item.BindingState, item.BindingErrorClass })
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return SlackAgentAppBindingResult.NotFound;
        return current.BindingState switch
        {
            SlackAgentAppBindingState.Bound => SlackAgentAppBindingResult.Bound,
            SlackAgentAppBindingState.ConnectionDeleted => SlackAgentAppBindingResult.ConnectionDeleted,
            SlackAgentAppBindingState.Conflict => SlackAgentAppBindingResult.Conflict(current.BindingErrorClass ?? "connection_identity_conflict"),
            _ => SlackAgentAppBindingResult.InProgress,
        };
    }
}

public sealed record SlackAgentAppBindingResult(
    SlackAgentAppBindingStatus Status,
    string? ErrorClass = null)
{
    public static SlackAgentAppBindingResult NotFound { get; } = new(SlackAgentAppBindingStatus.NotFound);
    public static SlackAgentAppBindingResult NotVerified { get; } = new(SlackAgentAppBindingStatus.NotVerified);
    public static SlackAgentAppBindingResult InProgress { get; } = new(SlackAgentAppBindingStatus.InProgress);
    public static SlackAgentAppBindingResult Bound { get; } = new(SlackAgentAppBindingStatus.Bound);
    public static SlackAgentAppBindingResult ConnectionDeleted { get; } = new(SlackAgentAppBindingStatus.ConnectionDeleted);
    public static SlackAgentAppBindingResult Conflict(string errorClass) => new(SlackAgentAppBindingStatus.Conflict, errorClass);
}

public enum SlackAgentAppBindingStatus
{
    Bound,
    NotFound,
    NotVerified,
    InProgress,
    ConnectionDeleted,
    Conflict,
}
