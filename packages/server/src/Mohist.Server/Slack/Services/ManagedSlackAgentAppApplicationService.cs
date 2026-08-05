using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class ManagedSlackAgentAppApplicationService : IScopedService
{
    private const string ProductCapabilityVersion = "p0-agent-app";
    private const int ManifestVersion = 2;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISlackAppManagementPort _appManagement;
    private readonly ISlackAppManagementFactPort _appManagementFacts;
    private readonly SlackManifestGenerator _manifests;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _timeProvider;

    public ManagedSlackAgentAppApplicationService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISlackAppManagementPort appManagement,
        ISlackAppManagementFactPort appManagementFacts,
        SlackManifestGenerator manifests,
        ISecretStore secrets,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _appManagement = appManagement;
        _appManagementFacts = appManagementFacts;
        _manifests = manifests;
        _secrets = secrets;
        _timeProvider = timeProvider;
    }

    public Task<ManagedSlackAgentAppOperationResult> CreateAsync(string agentAppId, CancellationToken ct = default) =>
        ExecuteAsync(agentAppId, SlackAgentAppOperation.Create, ct);

    public Task<ManagedSlackAgentAppOperationResult> ReconcileCreateAsync(string agentAppId, CancellationToken ct = default) =>
        ReconcileAsync(agentAppId, SlackAgentAppOperation.Create, ct);

    public async Task<ManagedSlackAgentAppOperationResult> DeleteAsync(
        string agentAppId,
        string confirmation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmation);
        if (!string.Equals(confirmation, "DELETE", StringComparison.Ordinal))
            return ManagedSlackAgentAppOperationResult.NotAllowed(SlackAppLifecycle.Created, "confirmation_required");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var agentApp = await db.ManagedSlackAgentApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == agentAppId, ct);
        if (agentApp is null) return ManagedSlackAgentAppOperationResult.NotFound;
        var connectionActive = await db.AgentConnections.AnyAsync(item =>
            item.Id == agentApp.AgentConnectionId && item.DeletedAt == null, ct);
        if (connectionActive)
            return ManagedSlackAgentAppOperationResult.NotAllowed(agentApp.AppLifecycle, "active_connection_binding");
        if (agentApp.AppLifecycle != SlackAppLifecycle.Created)
            return ManagedSlackAgentAppOperationResult.NotAllowed(agentApp.AppLifecycle, "permanent_delete_requires_created");
        return await ExecuteAsync(agentAppId, SlackAgentAppOperation.Delete, ct);
    }

    public Task<ManagedSlackAgentAppOperationResult> ReconcileDeleteAsync(string agentAppId, CancellationToken ct = default) =>
        ReconcileAsync(agentAppId, SlackAgentAppOperation.Delete, ct);

    private async Task<ManagedSlackAgentAppOperationResult> ExecuteAsync(
        string agentAppId,
        SlackAgentAppOperation operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentAppId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var agentApp = await db.ManagedSlackAgentApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == agentAppId, ct);
        if (agentApp is null) return ManagedSlackAgentAppOperationResult.NotFound;
        if (!CanStart(agentApp, operation))
            return ManagedSlackAgentAppOperationResult.NotAllowed(agentApp.AppLifecycle, agentApp.ErrorClass);

        var operationId = $"{operation.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}";
        var nextLifecycle = operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.Creating : SlackAppLifecycle.Deleting;
        SlackStateTransitions.RequireAgentAppLifecycleTransition(agentApp.AppLifecycle, nextLifecycle);
        var nextFence = agentApp.OperationFence + 1;
        var now = _timeProvider.GetUtcNow();
        var changed = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId
                && item.OperationFence == agentApp.OperationFence
                && item.AppLifecycle == agentApp.AppLifecycle
                && item.OperationId == agentApp.OperationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OperationFence, nextFence)
                .SetProperty(item => item.OperationId, operationId)
                .SetProperty(item => item.OperationKind, operation == SlackAgentAppOperation.Create ? "create" : "delete")
                .SetProperty(item => item.OperationStartedAt, now)
                .SetProperty(item => item.UnknownOutcome, (string?)null)
                .SetProperty(item => item.ErrorClass, (string?)null)
                .SetProperty(item => item.AppLifecycle, operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.Creating : SlackAppLifecycle.Deleting)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
            return ManagedSlackAgentAppOperationResult.Concurrent;

        var request = new SlackAppManagementRequest(
            agentApp.EnrollmentId,
            agentApp.Id,
            agentApp.WorkspaceTeamId,
            agentApp.AppId,
            operation == SlackAgentAppOperation.Create ? await BuildManifestJsonAsync(db, agentApp, ct).ConfigureAwait(false) : null);
        var external = operation == SlackAgentAppOperation.Create
            ? await _appManagement.CreateAsync(request, ct)
            : await _appManagement.DeleteAsync(request, ct);
        return await ApplyResultAsync(agentAppId, nextFence, operationId, operation, external, ct);
    }

    private async Task<ManagedSlackAgentAppOperationResult> ReconcileAsync(
        string agentAppId,
        SlackAgentAppOperation operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentAppId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var agentApp = await db.ManagedSlackAgentApps.AsNoTracking().SingleOrDefaultAsync(item => item.Id == agentAppId, ct);
        if (agentApp is null) return ManagedSlackAgentAppOperationResult.NotFound;
        var expectedLifecycle = operation == SlackAgentAppOperation.Create
            ? SlackAppLifecycle.CreateUnknown
            : SlackAppLifecycle.DeleteUnknown;
        if (agentApp.AppLifecycle != expectedLifecycle)
            return ManagedSlackAgentAppOperationResult.NotAllowed(agentApp.AppLifecycle, agentApp.ErrorClass);

        var operationId = $"reconcile_{operation.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}";
        var nextLifecycle = operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.Creating : SlackAppLifecycle.Deleting;
        SlackStateTransitions.RequireAgentAppLifecycleTransition(expectedLifecycle, nextLifecycle);
        var nextFence = agentApp.OperationFence + 1;
        var now = _timeProvider.GetUtcNow();
        var changed = await db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId
                && item.OperationFence == agentApp.OperationFence
                && item.AppLifecycle == expectedLifecycle
                && item.OperationId == agentApp.OperationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OperationFence, nextFence)
                .SetProperty(item => item.OperationId, operationId)
                .SetProperty(item => item.OperationKind, operation == SlackAgentAppOperation.Create ? "reconcile_create" : "reconcile_delete")
                .SetProperty(item => item.OperationStartedAt, now)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed == 0)
            return ManagedSlackAgentAppOperationResult.Concurrent;

        var request = new SlackAppManagementRequest(agentApp.EnrollmentId, agentApp.Id, agentApp.WorkspaceTeamId, agentApp.AppId);
        var fact = await _appManagementFacts.InspectAsync(request, ct);
        return await ApplyFactAsync(agentAppId, nextFence, operationId, operation, fact, ct);
    }

    private async Task<ManagedSlackAgentAppOperationResult> ApplyResultAsync(
        string agentAppId,
        int fence,
        string operationId,
        SlackAgentAppOperation operation,
        SlackAppManagementResult result,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var expectedLifecycle = operation == SlackAgentAppOperation.Create
            ? SlackAppLifecycle.Creating
            : SlackAppLifecycle.Deleting;
        var lifecycle = operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.Created : SlackAppLifecycle.Deleted;
        var unknownLifecycle = operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.CreateUnknown : SlackAppLifecycle.DeleteUnknown;
        var failedLifecycle = operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.NotCreated : SlackAppLifecycle.Created;
        SlackStateTransitions.RequireAgentAppLifecycleTransition(expectedLifecycle, lifecycle);
        SlackStateTransitions.RequireAgentAppLifecycleTransition(expectedLifecycle, unknownLifecycle);
        SlackStateTransitions.RequireAgentAppLifecycleTransition(expectedLifecycle, failedLifecycle);
        var query = db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId
                && item.OperationFence == fence
                && item.OperationId == operationId
                && item.AppLifecycle == expectedLifecycle);
        if (result.Outcome == SlackAppManagementOutcome.Succeeded && operation == SlackAgentAppOperation.Create)
            await PersistAppCredentialsAsync(agentAppId, result, ct);
        int changed;
        if (result.Outcome == SlackAppManagementOutcome.Succeeded)
        {
            changed = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AppLifecycle, lifecycle)
                .SetProperty(item => item.AppId, operation == SlackAgentAppOperation.Create ? result.AppId ?? string.Empty : string.Empty)
                .SetProperty(item => item.BotUserId, string.Empty)
                .SetProperty(item => item.InstallUrl, operation == SlackAgentAppOperation.Create ? result.InstallUrl ?? string.Empty : string.Empty)
                .SetProperty(item => item.ClientSecretRef, operation == SlackAgentAppOperation.Create && !string.IsNullOrEmpty(result.ClientSecret) ? agentAppId : string.Empty)
                .SetProperty(item => item.SigningSecretRef, operation == SlackAgentAppOperation.Create && !string.IsNullOrEmpty(result.SigningSecret) ? agentAppId : string.Empty)
                .SetProperty(item => item.DeletedAt, operation == SlackAgentAppOperation.Delete ? now : (DateTimeOffset?)null)
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
            return ManagedSlackAgentAppOperationResult.Stale;

        if (operation == SlackAgentAppOperation.Delete)
        {
            var row = await db.ManagedSlackAgentApps.SingleAsync(item =>
                item.Id == agentAppId && item.OperationFence == fence && item.OperationId == operationId, ct);
            var audit = JsonSerializer.Deserialize<List<ManagedSlackAuditEntry>>(row.AuditJson) ?? [];
            audit.Add(new ManagedSlackAuditEntry("permanent_delete", result.Outcome.ToString().ToLowerInvariant(), now));
            row.AuditJson = JsonSerializer.Serialize(audit);
            await db.SaveChangesAsync(ct);
        }
        return ManagedSlackAgentAppOperationResult.Completed(result.Outcome, result.AppId, result.ErrorClass);
    }

    private async Task PersistAppCredentialsAsync(
        string agentAppId,
        SlackAppManagementResult result,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(result.ClientSecret))
        {
            await _secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.ClientSecret),
                Encoding.UTF8.GetBytes(result.ClientSecret),
                ct);
        }
        if (!string.IsNullOrEmpty(result.SigningSecret))
        {
            await _secrets.StoreAsync(
                SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.SigningSecret),
                Encoding.UTF8.GetBytes(result.SigningSecret),
                ct);
        }
    }

    private async Task<ManagedSlackAgentAppOperationResult> ApplyFactAsync(
        string agentAppId,
        int fence,
        string operationId,
        SlackAgentAppOperation operation,
        SlackAppManagementFact fact,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var query = db.ManagedSlackAgentApps
            .Where(item => item.Id == agentAppId && item.OperationFence == fence && item.OperationId == operationId);
        var outcome = fact.Outcome switch
        {
            SlackAppManagementFactOutcome.Present when operation == SlackAgentAppOperation.Create =>
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AppLifecycle, SlackAppLifecycle.Created)
                    .SetProperty(item => item.AppId, fact.AppId ?? string.Empty)
                    .SetProperty(item => item.UnknownOutcome, (string?)null)
                    .SetProperty(item => item.ErrorClass, (string?)null)
                    .SetProperty(item => item.UpdatedAt, now), ct),
            SlackAppManagementFactOutcome.Absent when operation == SlackAgentAppOperation.Create =>
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AppLifecycle, SlackAppLifecycle.NotCreated)
                    .SetProperty(item => item.AppId, string.Empty)
                    .SetProperty(item => item.UnknownOutcome, (string?)null)
                    .SetProperty(item => item.ErrorClass, "reconciled_absent")
                    .SetProperty(item => item.UpdatedAt, now), ct),
            SlackAppManagementFactOutcome.Absent when operation == SlackAgentAppOperation.Delete =>
                await query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AppLifecycle, SlackAppLifecycle.Deleted)
                    .SetProperty(item => item.AppId, string.Empty)
                    .SetProperty(item => item.BotUserId, string.Empty)
                    .SetProperty(item => item.UnknownOutcome, (string?)null)
                    .SetProperty(item => item.ErrorClass, (string?)null)
                    .SetProperty(item => item.UpdatedAt, now), ct),
            _ => await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AppLifecycle, operation == SlackAgentAppOperation.Create ? SlackAppLifecycle.CreateUnknown : SlackAppLifecycle.DeleteUnknown)
                .SetProperty(item => item.UnknownOutcome, fact.ErrorClass ?? "manual_adjudication_required")
                .SetProperty(item => item.ErrorClass, fact.ErrorClass ?? "manual_adjudication_required")
                .SetProperty(item => item.UpdatedAt, now), ct),
        };

        var status = fact.Outcome switch
        {
            SlackAppManagementFactOutcome.Present when operation == SlackAgentAppOperation.Create => ManagedSlackAgentAppOperationStatus.Reconciled,
            SlackAppManagementFactOutcome.Absent when operation is SlackAgentAppOperation.Create or SlackAgentAppOperation.Delete => ManagedSlackAgentAppOperationStatus.Reconciled,
            _ => ManagedSlackAgentAppOperationStatus.ManualAdjudicationRequired,
        };
        return outcome == 1
            ? ManagedSlackAgentAppOperationResult.Reconciled(status, fact.Outcome, fact.AppId, fact.ErrorClass)
            : ManagedSlackAgentAppOperationResult.Stale;
    }

    private async Task<string> BuildManifestJsonAsync(
        MohistDbContext db,
        ManagedSlackAgentAppRow agentApp,
        CancellationToken ct)
    {
        var connection = await db.AgentConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == agentApp.AgentConnectionId, ct)
            .ConfigureAwait(false);
        if (connection is null)
            throw new InvalidOperationException("The Agent App's staged Connection was not found.");
        var manifest = _manifests.Generate(new SlackManifestInput(
            connection.BotName is { Length: > 0 } ? connection.BotName : "agent-app",
            string.Empty,
            ProductCapabilityVersion,
            new SlackManifestIdentitySnapshot(connection.Id, connection.AgentId, connection.WorkspaceTeamId),
            SlackManifestKind.AgentApp,
            ManifestVersion));
        return manifest.CanonicalJson;
    }

    private static bool CanStart(ManagedSlackAgentAppRow agentApp, SlackAgentAppOperation operation) =>
        operation switch
        {
            SlackAgentAppOperation.Create => agentApp.AppLifecycle == SlackAppLifecycle.NotCreated,
            SlackAgentAppOperation.Delete => agentApp.AppLifecycle == SlackAppLifecycle.Created,
            _ => false,
        };
}

public enum SlackAgentAppOperation
{
    Create,
    Delete,
}

public sealed record ManagedSlackAgentAppOperationResult(
    ManagedSlackAgentAppOperationStatus Status,
    SlackAppManagementOutcome? Outcome = null,
    string? AppId = null,
    string? ErrorClass = null,
    string? Lifecycle = null,
    SlackAppManagementFactOutcome? FactOutcome = null)
{
    public static ManagedSlackAgentAppOperationResult NotFound { get; } = new(ManagedSlackAgentAppOperationStatus.NotFound);
    public static ManagedSlackAgentAppOperationResult Concurrent { get; } = new(ManagedSlackAgentAppOperationStatus.Concurrent);
    public static ManagedSlackAgentAppOperationResult Stale { get; } = new(ManagedSlackAgentAppOperationStatus.Stale);
    public static ManagedSlackAgentAppOperationResult NotAllowed(string lifecycle, string? errorClass) => new(ManagedSlackAgentAppOperationStatus.NotAllowed, ErrorClass: errorClass, Lifecycle: lifecycle);
    public static ManagedSlackAgentAppOperationResult Completed(SlackAppManagementOutcome outcome, string? appId, string? errorClass) => new(ManagedSlackAgentAppOperationStatus.Completed, outcome, appId, errorClass);
    public static ManagedSlackAgentAppOperationResult Reconciled(ManagedSlackAgentAppOperationStatus status, SlackAppManagementFactOutcome factOutcome, string? appId, string? errorClass) => new(status, AppId: appId, ErrorClass: errorClass, FactOutcome: factOutcome);
}

public enum ManagedSlackAgentAppOperationStatus
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
    string Outcome,
    DateTimeOffset At);
