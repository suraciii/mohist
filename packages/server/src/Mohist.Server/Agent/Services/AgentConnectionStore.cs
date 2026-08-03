using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Agent.Services;

public sealed class AgentConnectionStore : IScopedService, ISlackChildAppBindingPort
{
    private static readonly HashSet<string> ImmutableBindingFields = new(StringComparer.Ordinal)
    {
        "projectId",
        "agentId",
        "providerKind",
        "workspaceTeamId",
        "appId",
        "botUserId",
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentQuerier _agentQuerier;
    private readonly ISecretStore _secretStore;
    private readonly IEnumerable<IAgentConnectionProviderCleanup> _providerCleanups;
    private readonly TimeProvider _timeProvider;

    public AgentConnectionStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentQuerier agentQuerier,
        ISecretStore secretStore,
        IEnumerable<IAgentConnectionProviderCleanup> providerCleanups,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _agentQuerier = agentQuerier;
        _secretStore = secretStore;
        _providerCleanups = providerCleanups;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<AgentConnection>> ListAsync(string projectId, bool includeDeleted = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.AgentConnections.AsNoTracking().Where(c => c.ProjectId == projectId);
        if (!includeDeleted)
            query = query.Where(c => c.DeletedAt == null);
        var rows = await query.OrderBy(c => c.Id).ToListAsync(ct);
        var agents = await _agentQuerier.ListAsync(projectId, all: true);
        var readinessByAgentId = agents.ToDictionary(
            agent => agent.Id,
            agent => AgentReadinessDeriver.Derive(agent.AgentConfig));
        return rows.Select(row => ToDomain(row, readinessByAgentId.GetValueOrDefault(row.AgentId))).ToList();
    }

    public async Task<AgentConnection?> GetAsync(string projectId, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        if (row is null) return null;
        var agent = await _agentQuerier.GetByIdAsync(projectId, row.AgentId);
        return ToDomain(row, agent is null ? null : AgentReadinessDeriver.Derive(agent.AgentConfig));
    }

    public async Task<IReadOnlyList<SlackAdapterConnection>> ListForAdapterAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.AgentConnections.AsNoTracking()
            .Where(connection => connection.DeletedAt == null && connection.DesiredState == DesiredStateKind.Enabled)
            .OrderBy(connection => connection.ProjectId)
            .ThenBy(connection => connection.Id)
            .Select(connection => new SlackAdapterConnection(connection.ProjectId, connection.Id))
            .ToListAsync(ct);

        var configured = new List<SlackAdapterConnection>();
        foreach (var candidate in candidates)
        {
            var appToken = await _secretStore.LoadAsync(
                new SecretStoreAddress(candidate.ProjectId, candidate.ConnectionId, SecretKind.AppToken), ct);
            var botToken = await _secretStore.LoadAsync(
                new SecretStoreAddress(candidate.ProjectId, candidate.ConnectionId, SecretKind.BotToken), ct);
            if (appToken is { Length: > 0 } && botToken is { Length: > 0 })
                configured.Add(candidate);
        }
        return configured;
    }

    /// <summary>
    /// Enumerates Slack Connections currently in
    /// <see cref="ConnectionHealthKind.Degraded"/> on a Slack provider
    /// backpressure reason, that are still
    /// <see cref="DesiredStateKind.Enabled"/> and not soft-deleted. Used
    /// by the Slack outbox dispatcher's recovery sweep to drive the
    /// reason-guarded flip back to Healthy. Returns a lightweight id
    /// projection (no full entity hydration) so the sweep does not
    /// pay for fields it never reads.
    /// </summary>
    public async Task<IReadOnlyList<BackpressuredConnection>> ListBackpressuredAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AgentConnections.AsNoTracking()
            .Where(connection => connection.DeletedAt == null
                && connection.DesiredState == DesiredStateKind.Enabled
                && connection.ConnectionHealth == ConnectionHealthKind.Degraded
                && (connection.HealthReason == SlackConnectionBackpressureReasons.InboxOverflow
                    || connection.HealthReason == SlackConnectionBackpressureReasons.OutboxOverflow))
            .OrderBy(connection => connection.ProjectId)
            .ThenBy(connection => connection.Id)
            .Select(connection => new BackpressuredConnection(connection.ProjectId, connection.Id, connection.HealthReason!))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Workspace-scoped projection of the active, identity-bound Mohist
    /// Bots in this workspace. Each row pairs the Slack <c>BotUserId</c>
    /// with the owning <see cref="AgentConnection"/> so the channel
    /// state machine can resolve <c>mentionedUserIds ∩ workspaceBots</c>
    /// and pick the right Connection to address. Only Connections with a
    /// bound Slack identity (<c>WorkspaceTeamId</c>, <c>AppId</c>,
    /// <c>BotUserId</c>), non-deleted, Enabled, and the requested
    /// <see cref="DesiredStateKind.Enabled"/> are returned. Returns an
    /// empty list when no Bot lives in the workspace — every channel
    /// message then degenerates to "not mine" or "ignored" without
    /// adapter-held state.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceBoundBot>> ListBoundBotsByWorkspaceAsync(
        string workspaceTeamId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(workspaceTeamId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AgentConnections.AsNoTracking()
            .Where(connection => connection.DeletedAt == null
                && connection.DesiredState == DesiredStateKind.Enabled
                && connection.WorkspaceTeamId == workspaceTeamId
                && connection.BotUserId != string.Empty)
            .OrderBy(connection => connection.Id)
            .Select(connection => new WorkspaceBoundBot(
                connection.ProjectId,
                connection.Id,
                connection.AgentId,
                connection.BotUserId,
                connection.OwnerSlackUserId))
            .ToListAsync(ct);
    }

    public async Task<AgentConnection> CreateAsync(AgentConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!string.IsNullOrWhiteSpace(connection.AppId) || !string.IsNullOrWhiteSpace(connection.BotUserId))
            throw new AgentConnectionValidationException("App and Bot identities must both be empty until the Connection is bound.", "invalid_staged_binding");

        return await CreateCoreAsync(connection, requireWorkspaceReservation: false, ct);
    }

    public async Task<AgentConnection> CreateStagedAsync(AgentConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(connection.WorkspaceTeamId))
            throw new AgentConnectionValidationException("A Slack workspace team is required when creating a staged Connection.", "workspace_required");
        if (!string.IsNullOrWhiteSpace(connection.AppId) || !string.IsNullOrWhiteSpace(connection.BotUserId))
            throw new AgentConnectionValidationException("App and Bot identities must both be empty until the Connection is bound.", "invalid_staged_binding");

        return await CreateCoreAsync(connection, requireWorkspaceReservation: true, ct);
    }

    private async Task<AgentConnection> CreateCoreAsync(
        AgentConnection connection,
        bool requireWorkspaceReservation,
        CancellationToken ct)
    {
        if (requireWorkspaceReservation && string.IsNullOrWhiteSpace(connection.WorkspaceTeamId))
            throw new AgentConnectionValidationException("A Slack workspace team is required when creating a staged Connection.", "workspace_required");

        await ValidateActiveAgentAsync(connection.ProjectId, connection.AgentId, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AgentConnections.AnyAsync(
            c => c.ProjectId == connection.ProjectId
                 && c.AgentId == connection.AgentId
                 && c.WorkspaceTeamId == connection.WorkspaceTeamId
                 && c.DeletedAt == null,
            ct);
        if (!existing && requireWorkspaceReservation)
        {
            existing = await db.ManagedSlackChildApps.AnyAsync(child =>
                child.DeletedAt == null
                && child.WorkspaceTeamId == connection.WorkspaceTeamId
                && db.AgentConnections.Any(candidate =>
                    candidate.Id == child.AgentConnectionId
                    && candidate.ProjectId == connection.ProjectId
                    && candidate.AgentId == connection.AgentId), ct);
        }
        if (existing)
            throw new AgentConnectionDuplicateException(
                connection.ProjectId, connection.AgentId, connection.WorkspaceTeamId);

        var now = _timeProvider.GetUtcNow();
        connection.CreatedAt = now;
        connection.UpdatedAt = now;
        var row = ToRow(connection);
        db.AgentConnections.Add(row);
        await db.SaveChangesAsync(ct);
        return connection;
    }

    public async Task<AgentConnection?> UpdateAsync(
        string projectId,
        string id,
        IReadOnlySet<string> fields,
        string? botName = null,
        string? avatarHash = null,
        string? verifiedBotName = null,
        string? verifiedBotIconUrl = null,
        string? setupProgress = null,
        string? desiredState = null,
        string? connectionHealth = null,
        string? healthReason = null,
        string? agentReadiness = null,
        string? ownerSlackUserId = null,
        string? accessPolicy = null,
        DateTimeOffset? lastHeartbeatAt = null,
        DateTimeOffset? offlineGapAt = null,
        bool clearOfflineGapAt = false,
        CancellationToken ct = default)
    {
        var immutableField = fields.FirstOrDefault(ImmutableBindingFields.Contains);
        if (immutableField is not null)
            throw new AgentConnectionValidationException(
                $"Connection binding field '{immutableField}' cannot be changed.",
                "immutable_binding");

        var existing = await GetAsync(projectId, id, ct);
        if (existing is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentConnections.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        if (row is null) return null;

        if (fields.Contains(nameof(botName))) row.BotName = botName?.Trim() ?? string.Empty;
        if (fields.Contains(nameof(avatarHash))) row.AvatarHash = avatarHash;
        if (fields.Contains(nameof(verifiedBotName))) row.VerifiedBotName = verifiedBotName;
        if (fields.Contains(nameof(verifiedBotIconUrl))) row.VerifiedBotIconUrl = verifiedBotIconUrl;
        if (fields.Contains(nameof(setupProgress))) row.SetupProgress = setupProgress ?? existing.SetupProgress;
        if (fields.Contains(nameof(desiredState))) row.DesiredState = desiredState ?? existing.DesiredState;
        if (fields.Contains(nameof(connectionHealth))) row.ConnectionHealth = connectionHealth ?? existing.ConnectionHealth;
        if (fields.Contains(nameof(healthReason))) row.HealthReason = healthReason;
        if (fields.Contains(nameof(agentReadiness))) row.AgentReadiness = agentReadiness ?? existing.AgentReadiness;
        if (fields.Contains(nameof(ownerSlackUserId))) row.OwnerSlackUserId = ownerSlackUserId;
        if (fields.Contains(nameof(accessPolicy))) row.AccessPolicy = accessPolicy ?? existing.AccessPolicy;
        if (fields.Contains(nameof(lastHeartbeatAt))) row.LastHeartbeatAt = lastHeartbeatAt;
        if (clearOfflineGapAt) row.OfflineGapAt = null;
        else if (fields.Contains(nameof(offlineGapAt))) row.OfflineGapAt = offlineGapAt;

        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<AgentConnection?> BindSlackIdentityAsync(
        string projectId,
        string id,
        string workspaceTeamId,
        string appId,
        string botUserId,
        string? botName,
        CancellationToken ct = default,
        string? claimToken = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceTeamId)
            || string.IsNullOrWhiteSpace(appId)
            || string.IsNullOrWhiteSpace(botUserId))
            throw new AgentConnectionValidationException("Slack workspace, App, and Bot identity are required.", "invalid_slack_identity");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var current = await db.AgentConnections.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        if (current is null) return null;

        var currentAppBound = !string.IsNullOrWhiteSpace(current.AppId);
        var currentBotBound = !string.IsNullOrWhiteSpace(current.BotUserId);
        if (currentAppBound != currentBotBound)
            throw new AgentConnectionValidationException("The Connection contains a half-bound Slack identity.", "invalid_staged_binding");
        var workspaceWasUnreserved = string.IsNullOrWhiteSpace(current.WorkspaceTeamId);
        if (!workspaceWasUnreserved && !string.Equals(current.WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal))
            throw new AgentConnectionValidationException("The Slack workspace cannot be changed after Connection creation.", "team_mismatch");
        if (currentAppBound)
        {
            if (string.Equals(current.AppId, appId, StringComparison.Ordinal)
                && string.Equals(current.BotUserId, botUserId, StringComparison.Ordinal))
                return ToDomain(current);
            throw new AgentConnectionValidationException("Slack App and Bot identity are already bound and cannot be changed.", "immutable_binding");
        }

        var duplicate = await db.AgentConnections.AnyAsync(c => c.ProjectId == projectId
            && c.Id != id
            && c.AgentId == current.AgentId
            && c.WorkspaceTeamId == workspaceTeamId
            && c.DeletedAt == null
            && (!string.IsNullOrEmpty(c.AppId) || !string.IsNullOrEmpty(c.BotUserId)), ct);
        if (duplicate)
            throw new AgentConnectionDuplicateException(projectId, current.AgentId, workspaceTeamId);

        var now = _timeProvider.GetUtcNow();
        var update = db.AgentConnections
            .Where(c => c.ProjectId == projectId
                && c.Id == id
                && c.DeletedAt == null
                && (workspaceWasUnreserved || c.WorkspaceTeamId == workspaceTeamId)
                && c.AppId == string.Empty
                && c.BotUserId == string.Empty);
        if (claimToken is not null)
        {
            update = update.Where(connection => db.SlackChildAppBindingObligations.Any(obligation =>
                obligation.AgentConnectionId == connection.Id
                && obligation.Status == "in_progress"
                && obligation.ClaimToken == claimToken));
        }

        var changed = await update.ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.WorkspaceTeamId, workspaceTeamId)
                .SetProperty(c => c.AppId, appId)
                .SetProperty(c => c.BotUserId, botUserId)
                .SetProperty(c => c.BotName, botName?.Trim() ?? current.BotName)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (changed == 0)
        {
            var afterRace = await db.AgentConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
            if (afterRace is not null
                && afterRace.WorkspaceTeamId == workspaceTeamId
                && afterRace.AppId == appId
                && afterRace.BotUserId == botUserId)
                return ToDomain(afterRace);
            if (claimToken is not null && !await db.SlackChildAppBindingObligations.AnyAsync(obligation =>
                    obligation.AgentConnectionId == id
                    && obligation.Status == "in_progress"
                    && obligation.ClaimToken == claimToken, ct))
                throw new AgentConnectionValidationException(
                    "The Slack binding claim is no longer current.",
                    "stale_binding_claim");
            throw new AgentConnectionValidationException("Slack identity was bound by another operation and cannot be changed.", "immutable_binding");
        }

        var bound = await db.AgentConnections.AsNoTracking().SingleAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        return ToDomain(bound);
    }

    /// <summary>
    /// Clears <see cref="AgentConnection.OfflineGapAt"/> when set. Returns
    /// the affected row count. The diagnostic notice hangs around only
    /// until the first new ingress is accepted (proven liveness) or the
    /// operator explicitly acknowledges; the column is otherwise additive
    /// and a no-op on a Connection that never had the gap stamped.
    /// </summary>
    public async Task<int> ClearOfflineGapIfSetAsync(string projectId, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AgentConnections
            .Where(row => row.ProjectId == projectId
                && row.Id == id
                && row.DeletedAt == null
                && row.OfflineGapAt != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.OfflineGapAt, (DateTimeOffset?)null)
                .SetProperty(row => row.UpdatedAt, now), ct);
    }

    public async Task<AgentConnection?> DeleteAsync(string projectId, string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentConnections.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        if (row is null) return null;

        await DeleteProviderRecordsAsync(projectId, id, ct);
        if (row.DeletedAt is not null) return ToDomain(row);

        var now = _timeProvider.GetUtcNow();
        row.DeletedAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    private async Task DeleteProviderRecordsAsync(string projectId, string connectionId, CancellationToken ct)
    {
        await _secretStore.DeleteAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), ct);
        await _secretStore.DeleteAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
        foreach (var cleanup in _providerCleanups)
        {
            await cleanup.DeleteForConnectionAsync(projectId, connectionId, ct);
        }
    }

    private async Task ValidateActiveAgentAsync(string projectId, string agentId, CancellationToken ct)
    {
        var agent = await _agentQuerier.GetByIdAsync(projectId, agentId);
        if (agent is null)
            throw new AgentConnectionValidationException($"Agent '{agentId}' was not found in project '{projectId}'.", "agent_not_found");
        if (!string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
            throw new AgentConnectionValidationException($"Agent '{agentId}' is archived.", "agent_archived");
    }

    public static bool HasBoundIdentity(AgentConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.AppId)
        || !string.IsNullOrWhiteSpace(connection.BotUserId);

    public static bool HasBoundIdentity(AgentConnectionRow row) =>
        !string.IsNullOrWhiteSpace(row.AppId)
        || !string.IsNullOrWhiteSpace(row.BotUserId);

    private static AgentConnection ToDomain(AgentConnectionRow row, string? derivedReadiness = null) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        AgentId = row.AgentId,
        ProviderKind = row.ProviderKind,
        WorkspaceTeamId = row.WorkspaceTeamId,
        AppId = row.AppId,
        BotUserId = row.BotUserId,
        BotName = row.BotName,
        AvatarHash = row.AvatarHash,
        VerifiedBotName = row.VerifiedBotName,
        VerifiedBotIconUrl = row.VerifiedBotIconUrl,
        SetupProgress = row.SetupProgress,
        DesiredState = row.DesiredState,
        ConnectionHealth = row.ConnectionHealth,
        HealthReason = row.HealthReason,
        AgentReadiness = derivedReadiness ?? row.AgentReadiness,
        OwnerSlackUserId = row.OwnerSlackUserId,
        AccessPolicy = string.IsNullOrEmpty(row.AccessPolicy) ? AccessPolicyKind.OwnerOnly : row.AccessPolicy,
        LastHeartbeatAt = row.LastHeartbeatAt,
        OfflineGapAt = row.OfflineGapAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private static AgentConnectionRow ToRow(AgentConnection connection) => new()
    {
        Id = connection.Id,
        ProjectId = connection.ProjectId,
        AgentId = connection.AgentId,
        ProviderKind = connection.ProviderKind,
        WorkspaceTeamId = connection.WorkspaceTeamId,
        AppId = connection.AppId,
        BotUserId = connection.BotUserId,
        BotName = connection.BotName,
        AvatarHash = connection.AvatarHash,
        VerifiedBotName = connection.VerifiedBotName,
        VerifiedBotIconUrl = connection.VerifiedBotIconUrl,
        SetupProgress = connection.SetupProgress,
        DesiredState = connection.DesiredState,
        ConnectionHealth = connection.ConnectionHealth,
        HealthReason = connection.HealthReason,
        AgentReadiness = connection.AgentReadiness,
        OwnerSlackUserId = connection.OwnerSlackUserId,
        AccessPolicy = string.IsNullOrEmpty(connection.AccessPolicy) ? AccessPolicyKind.OwnerOnly : connection.AccessPolicy,
        LastHeartbeatAt = connection.LastHeartbeatAt,
        OfflineGapAt = connection.OfflineGapAt,
        CreatedAt = connection.CreatedAt,
        UpdatedAt = connection.UpdatedAt,
        DeletedAt = connection.DeletedAt,
    };
}

public sealed class AgentConnectionValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AgentConnectionDuplicateException(string projectId, string agentId, string workspaceTeamId)
    : Exception($"A connection for agent '{agentId}' in workspace '{workspaceTeamId}' already exists in project '{projectId}'.")
{
    public string ProjectId { get; } = projectId;
    public string AgentId { get; } = agentId;
    public string WorkspaceTeamId { get; } = workspaceTeamId;
}

public sealed record SlackAdapterConnection(string ProjectId, string ConnectionId);

public sealed record BackpressuredConnection(string ProjectId, string ConnectionId, string HealthReason);

/// <summary>
/// Workspace-scoped projection of the identity-bound Mohist Bots in
/// <see cref="AgentConnectionStore"/>. Returned by
/// <see cref="AgentConnectionStore.ListBoundBotsByWorkspaceAsync"/> so
/// the channel ingress can compute the set of Bots that the workspace
/// actually exposes ("W" in D4 of the channel design) and resolve
/// <c>M ∩ W</c> (mentioned user ids that are Mohist Bots) without
/// trusting arbitrary human mentions as Bots. Each row pairs the
/// Bot's stable Slack user id with the Connection that owns the
/// routing, so the channel state machine can identify which
/// Connection is being addressed.
/// </summary>
public sealed record WorkspaceBoundBot(
    string ProjectId,
    string ConnectionId,
    string AgentId,
    string BotUserId,
    string? OwnerSlackUserId = null);
