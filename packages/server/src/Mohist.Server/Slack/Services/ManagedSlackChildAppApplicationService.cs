using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;
using System.Text.Json;

namespace Mohist.Server.Slack.Services;

public sealed class ManagedSlackChildAppApplicationService : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISlackAppManagementPort _appManagement;
    private readonly ISlackAppManagementFactPort _appManagementFacts;
    private readonly TimeProvider _timeProvider;

    public ManagedSlackChildAppApplicationService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISlackAppManagementPort appManagement,
        ISlackAppManagementFactPort appManagementFacts,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _appManagement = appManagement;
        _appManagementFacts = appManagementFacts;
        _timeProvider = timeProvider;
    }

    public Task<ManagedSlackChildAppOperationResult> CreateAsync(string childAppId, CancellationToken ct = default) =>
        ExecuteAsync(childAppId, SlackChildAppOperation.Create, ct);

    public Task<ManagedSlackChildAppOperationResult> ReconcileCreateAsync(string childAppId, CancellationToken ct = default) =>
        ReconcileAsync(childAppId, SlackChildAppOperation.Create, ct);

    public async Task<ManagedSlackChildAppOperationResult> DeleteAsync(
        string childAppId,
        string confirmation,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmation);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!string.Equals(confirmation, "DELETE", StringComparison.Ordinal))
            return ManagedSlackChildAppOperationResult.NotAllowed(SlackAppLifecycle.Created, "confirmation_required");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var child = await db.ManagedSlackChildApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == childAppId, ct);
        if (child is null) return ManagedSlackChildAppOperationResult.NotFound;
        var connectionActive = await db.AgentConnections.AnyAsync(item =>
            item.Id == child.AgentConnectionId && item.DeletedAt == null, ct);
        if (connectionActive)
            return ManagedSlackChildAppOperationResult.NotAllowed(child.AppLifecycle, "active_connection_binding");
        if (child.AppLifecycle != SlackAppLifecycle.Created)
            return ManagedSlackChildAppOperationResult.NotAllowed(child.AppLifecycle, "permanent_delete_requires_created");
        return await ExecuteAsync(childAppId, SlackChildAppOperation.Delete, actor, ct);
    }

    public Task<ManagedSlackChildAppOperationResult> ReconcileDeleteAsync(string childAppId, CancellationToken ct = default) =>
        ReconcileAsync(childAppId, SlackChildAppOperation.Delete, ct);

    private Task<ManagedSlackChildAppOperationResult> ExecuteAsync(
        string childAppId,
        SlackChildAppOperation operation,
        CancellationToken ct) => ExecuteAsync(childAppId, operation, null, ct);

    private async Task<ManagedSlackChildAppOperationResult> ExecuteAsync(
        string childAppId,
        SlackChildAppOperation operation,
        string? actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAppId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var child = await db.ManagedSlackChildApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == childAppId, ct);
        if (child is null) return ManagedSlackChildAppOperationResult.NotFound;
        if (!CanStart(child, operation))
            return ManagedSlackChildAppOperationResult.NotAllowed(child.AppLifecycle, child.ErrorClass);

        var operationId = $"{operation.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}";
        var nextLifecycle = operation == SlackChildAppOperation.Create ? SlackAppLifecycle.Creating : SlackAppLifecycle.Deleting;
        SlackStateTransitions.RequireChildAppLifecycleTransition(child.AppLifecycle, nextLifecycle);
        var nextFence = child.OperationFence + 1;
        var now = _timeProvider.GetUtcNow();
        var changed = await db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId
                && item.OperationFence == child.OperationFence
                && item.AppLifecycle == child.AppLifecycle
                && item.OperationId == child.OperationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OperationFence, nextFence)
                .SetProperty(item => item.OperationId, operationId)
                .SetProperty(item => item.OperationKind, operation == SlackChildAppOperation.Create ? "create" : "delete")
                .SetProperty(item => item.OperationStartedAt, now)
                .SetProperty(item => item.UnknownOutcome, (string?)null)
                .SetProperty(item => item.ErrorClass, (string?)null)
                .SetProperty(item => item.AppLifecycle, operation == SlackChildAppOperation.Create ? SlackAppLifecycle.Creating : SlackAppLifecycle.Deleting)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
            return ManagedSlackChildAppOperationResult.Concurrent;

        var request = new SlackAppManagementRequest(child.EnrollmentId, child.Id, child.WorkspaceTeamId, child.AppId);
        var external = operation == SlackChildAppOperation.Create
            ? await _appManagement.CreateAsync(request, ct)
            : await _appManagement.DeleteAsync(request, ct);
        return await ApplyResultAsync(childAppId, nextFence, operationId, operation, external, actor, ct);
    }

    private async Task<ManagedSlackChildAppOperationResult> ReconcileAsync(
        string childAppId,
        SlackChildAppOperation operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAppId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var child = await db.ManagedSlackChildApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == childAppId, ct);
        if (child is null) return ManagedSlackChildAppOperationResult.NotFound;
        var expectedLifecycle = operation == SlackChildAppOperation.Create
            ? SlackAppLifecycle.CreateUnknown
            : SlackAppLifecycle.DeleteUnknown;
        if (child.AppLifecycle != expectedLifecycle)
            return ManagedSlackChildAppOperationResult.NotAllowed(child.AppLifecycle, child.ErrorClass);

        var operationId = $"reconcile_{operation.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}";
        var nextLifecycle = operation == SlackChildAppOperation.Create ? SlackAppLifecycle.Creating : SlackAppLifecycle.Deleting;
        SlackStateTransitions.RequireChildAppLifecycleTransition(expectedLifecycle, nextLifecycle);
        var nextFence = child.OperationFence + 1;
        var now = _timeProvider.GetUtcNow();
        var changed = await db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId
                && item.OperationFence == child.OperationFence
                && item.AppLifecycle == expectedLifecycle
                && item.OperationId == child.OperationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OperationFence, nextFence)
                .SetProperty(item => item.OperationId, operationId)
                .SetProperty(item => item.OperationKind, operation == SlackChildAppOperation.Create ? "reconcile_create" : "reconcile_delete")
                .SetProperty(item => item.OperationStartedAt, now)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
            return ManagedSlackChildAppOperationResult.Concurrent;

        var request = new SlackAppManagementRequest(child.EnrollmentId, child.Id, child.WorkspaceTeamId, child.AppId);
        var fact = await _appManagementFacts.InspectAsync(request, ct);
        return await ApplyFactAsync(childAppId, nextFence, operationId, operation, fact, ct);
    }

    private async Task<ManagedSlackChildAppOperationResult> ApplyResultAsync(
        string childAppId,
        int fence,
        string operationId,
        SlackChildAppOperation operation,
        SlackAppManagementResult result,
        string? actor,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var expectedLifecycle = operation == SlackChildAppOperation.Create
            ? SlackAppLifecycle.Creating
            : SlackAppLifecycle.Deleting;
        var lifecycle = operation == SlackChildAppOperation.Create ? SlackAppLifecycle.Created : SlackAppLifecycle.Deleted;
        var unknownLifecycle = operation == SlackChildAppOperation.Create ? SlackAppLifecycle.CreateUnknown : SlackAppLifecycle.DeleteUnknown;
        var failedLifecycle = operation == SlackChildAppOperation.Create ? SlackAppLifecycle.NotCreated : SlackAppLifecycle.Created;
        SlackStateTransitions.RequireChildAppLifecycleTransition(expectedLifecycle, lifecycle);
        SlackStateTransitions.RequireChildAppLifecycleTransition(expectedLifecycle, unknownLifecycle);
        SlackStateTransitions.RequireChildAppLifecycleTransition(expectedLifecycle, failedLifecycle);
        var query = db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId
                && item.OperationFence == fence
                && item.OperationId == operationId
                && item.AppLifecycle == expectedLifecycle);
        int changed;
        if (result.Outcome == SlackAppManagementOutcome.Succeeded)
        {
            changed = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AppLifecycle, lifecycle)
                .SetProperty(item => item.AppId, operation == SlackChildAppOperation.Create ? result.AppId ?? string.Empty : string.Empty)
                .SetProperty(item => item.BotUserId, string.Empty)
                .SetProperty(item => item.DeletedAt, operation == SlackChildAppOperation.Delete ? now : (DateTimeOffset?)null)
                .SetProperty(item => item.UnknownOutcome, (string?)null)
                .SetProperty(item => item.ErrorClass, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), ct);
        }
        else if (result.Outcome == SlackAppManagementOutcome.Unknown)
        {
            changed = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AppLifecycle, unknownLifecycle)
                .SetProperty(item => item.UnknownOutcome, SlackSecretRedactor.Redact(result.ErrorMessage ?? result.ErrorClass ?? "unknown"))
                .SetProperty(item => item.ErrorClass, result.ErrorClass)
                .SetProperty(item => item.UpdatedAt, now), ct);
        }
        else
        {
            changed = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AppLifecycle, failedLifecycle)
                .SetProperty(item => item.UnknownOutcome, (string?)null)
                .SetProperty(item => item.ErrorClass, result.ErrorClass ?? "definite_failure")
                .SetProperty(item => item.UpdatedAt, now), ct);
        }
        if (changed == 0)
            return ManagedSlackChildAppOperationResult.Stale;

        if (actor is not null)
        {
            var row = await db.ManagedSlackChildApps.SingleAsync(item =>
                item.Id == childAppId && item.OperationFence == fence && item.OperationId == operationId, ct);
            var audit = JsonSerializer.Deserialize<List<ManagedSlackAuditEntry>>(row.AuditJson) ?? [];
            audit.Add(new ManagedSlackAuditEntry("permanent_delete", actor, result.Outcome.ToString().ToLowerInvariant(), now));
            row.AuditJson = JsonSerializer.Serialize(audit);
            await db.SaveChangesAsync(ct);
        }
        return ManagedSlackChildAppOperationResult.Completed(result.Outcome, result.AppId, result.ErrorClass);
    }

    private async Task<ManagedSlackChildAppOperationResult> ApplyFactAsync(
        string childAppId,
        int fence,
        string operationId,
        SlackChildAppOperation operation,
        SlackAppManagementFact fact,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var query = db.ManagedSlackChildApps
            .Where(item => item.Id == childAppId && item.OperationFence == fence && item.OperationId == operationId);
        var outcome = fact.Outcome switch
        {
            SlackAppManagementFactOutcome.Present when operation == SlackChildAppOperation.Create =>
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AppLifecycle, SlackAppLifecycle.Created)
                    .SetProperty(item => item.AppId, fact.AppId ?? string.Empty)
                    .SetProperty(item => item.UnknownOutcome, (string?)null)
                    .SetProperty(item => item.ErrorClass, (string?)null)
                    .SetProperty(item => item.UpdatedAt, now), ct),
            SlackAppManagementFactOutcome.Absent when operation == SlackChildAppOperation.Create =>
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AppLifecycle, SlackAppLifecycle.NotCreated)
                    .SetProperty(item => item.AppId, string.Empty)
                    .SetProperty(item => item.UnknownOutcome, (string?)null)
                    .SetProperty(item => item.ErrorClass, "reconciled_absent")
                    .SetProperty(item => item.UpdatedAt, now), ct),
            SlackAppManagementFactOutcome.Absent when operation == SlackChildAppOperation.Delete =>
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AppLifecycle, SlackAppLifecycle.Deleted)
                    .SetProperty(item => item.AppId, string.Empty)
                    .SetProperty(item => item.BotUserId, string.Empty)
                    .SetProperty(item => item.UnknownOutcome, (string?)null)
                    .SetProperty(item => item.ErrorClass, (string?)null)
                    .SetProperty(item => item.UpdatedAt, now), ct),
            _ => await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AppLifecycle, operation == SlackChildAppOperation.Create ? SlackAppLifecycle.CreateUnknown : SlackAppLifecycle.DeleteUnknown)
                .SetProperty(item => item.UnknownOutcome, fact.ErrorClass ?? "manual_adjudication_required")
                .SetProperty(item => item.ErrorClass, fact.ErrorClass ?? "manual_adjudication_required")
                .SetProperty(item => item.UpdatedAt, now), ct),
        };

        var status = fact.Outcome switch
        {
            SlackAppManagementFactOutcome.Present when operation == SlackChildAppOperation.Create => ManagedSlackChildAppOperationStatus.Reconciled,
            SlackAppManagementFactOutcome.Absent when operation is SlackChildAppOperation.Create or SlackChildAppOperation.Delete => ManagedSlackChildAppOperationStatus.Reconciled,
            _ => ManagedSlackChildAppOperationStatus.ManualAdjudicationRequired,
        };
        return outcome == 1
            ? ManagedSlackChildAppOperationResult.Reconciled(status, fact.Outcome, fact.AppId, fact.ErrorClass)
            : ManagedSlackChildAppOperationResult.Stale;
    }

    private static bool CanStart(ManagedSlackChildAppRow child, SlackChildAppOperation operation) =>
        operation switch
        {
            SlackChildAppOperation.Create => child.AppLifecycle == SlackAppLifecycle.NotCreated,
            SlackChildAppOperation.Delete => child.AppLifecycle == SlackAppLifecycle.Created,
            _ => false,
        };
}

public enum SlackChildAppOperation
{
    Create,
    Delete,
}

public sealed record ManagedSlackChildAppOperationResult(
    ManagedSlackChildAppOperationStatus Status,
    SlackAppManagementOutcome? Outcome = null,
    string? AppId = null,
    string? ErrorClass = null,
    string? Lifecycle = null,
    SlackAppManagementFactOutcome? FactOutcome = null)
{
    public static ManagedSlackChildAppOperationResult NotFound { get; } = new(ManagedSlackChildAppOperationStatus.NotFound);
    public static ManagedSlackChildAppOperationResult Concurrent { get; } = new(ManagedSlackChildAppOperationStatus.Concurrent);
    public static ManagedSlackChildAppOperationResult Stale { get; } = new(ManagedSlackChildAppOperationStatus.Stale);
    public static ManagedSlackChildAppOperationResult NotAllowed(string lifecycle, string? errorClass) => new(ManagedSlackChildAppOperationStatus.NotAllowed, ErrorClass: errorClass, Lifecycle: lifecycle);
    public static ManagedSlackChildAppOperationResult Completed(SlackAppManagementOutcome outcome, string? appId, string? errorClass) => new(ManagedSlackChildAppOperationStatus.Completed, outcome, appId, errorClass);
    public static ManagedSlackChildAppOperationResult Reconciled(ManagedSlackChildAppOperationStatus status, SlackAppManagementFactOutcome factOutcome, string? appId, string? errorClass) => new(status, AppId: appId, ErrorClass: errorClass, FactOutcome: factOutcome);
}

public enum ManagedSlackChildAppOperationStatus
{
    Completed,
    Reconciled,
    ManualAdjudicationRequired,
    NotFound,
    NotAllowed,
    Concurrent,
    Stale,
}

public sealed record ManagedSlackAuditEntry(
    string Action,
    string Actor,
    string Outcome,
    DateTimeOffset At);
