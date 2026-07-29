using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Agent.Services;

public sealed class AgentConnectionStore : IScopedService
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
    private readonly TimeProvider _timeProvider;

    public AgentConnectionStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentQuerier agentQuerier,
        ISecretStore secretStore,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _agentQuerier = agentQuerier;
        _secretStore = secretStore;
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
        return rows.Select(ToDomain).ToList();
    }

    public async Task<AgentConnection?> GetAsync(string projectId, string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<AgentConnection> CreateAsync(AgentConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ValidateActiveAgentAsync(connection.ProjectId, connection.AgentId, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AgentConnections.AnyAsync(
            c => c.ProjectId == connection.ProjectId
                 && c.AgentId == connection.AgentId
                 && c.WorkspaceTeamId == connection.WorkspaceTeamId
                 && c.DeletedAt == null,
            ct);
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
        string? setupProgress = null,
        string? desiredState = null,
        string? connectionHealth = null,
        string? healthReason = null,
        string? agentReadiness = null,
        string? ownerSlackUserId = null,
        DateTimeOffset? lastHeartbeatAt = null,
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
        if (fields.Contains(nameof(setupProgress))) row.SetupProgress = setupProgress ?? existing.SetupProgress;
        if (fields.Contains(nameof(desiredState))) row.DesiredState = desiredState ?? existing.DesiredState;
        if (fields.Contains(nameof(connectionHealth))) row.ConnectionHealth = connectionHealth ?? existing.ConnectionHealth;
        if (fields.Contains(nameof(healthReason))) row.HealthReason = healthReason;
        if (fields.Contains(nameof(agentReadiness))) row.AgentReadiness = agentReadiness ?? existing.AgentReadiness;
        if (fields.Contains(nameof(ownerSlackUserId))) row.OwnerSlackUserId = ownerSlackUserId;
        if (fields.Contains(nameof(lastHeartbeatAt))) row.LastHeartbeatAt = lastHeartbeatAt;

        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<AgentConnection?> DeleteAsync(string projectId, string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentConnections.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Id == id, ct);
        if (row is null) return null;

        await DeleteCredentialsAsync(projectId, id, ct);
        if (row.DeletedAt is not null) return ToDomain(row);

        var now = _timeProvider.GetUtcNow();
        row.DeletedAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    private async Task DeleteCredentialsAsync(string projectId, string connectionId, CancellationToken ct)
    {
        await _secretStore.DeleteAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), ct);
        await _secretStore.DeleteAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
    }

    private async Task ValidateActiveAgentAsync(string projectId, string agentId, CancellationToken ct)
    {
        var agent = await _agentQuerier.GetByIdAsync(projectId, agentId);
        if (agent is null)
            throw new AgentConnectionValidationException($"Agent '{agentId}' was not found in project '{projectId}'.", "agent_not_found");
        if (!string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
            throw new AgentConnectionValidationException($"Agent '{agentId}' is archived.", "agent_archived");
    }

    private static AgentConnection ToDomain(AgentConnectionRow row) => new()
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
        SetupProgress = row.SetupProgress,
        DesiredState = row.DesiredState,
        ConnectionHealth = row.ConnectionHealth,
        HealthReason = row.HealthReason,
        AgentReadiness = row.AgentReadiness,
        OwnerSlackUserId = row.OwnerSlackUserId,
        LastHeartbeatAt = row.LastHeartbeatAt,
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
        SetupProgress = connection.SetupProgress,
        DesiredState = connection.DesiredState,
        ConnectionHealth = connection.ConnectionHealth,
        HealthReason = connection.HealthReason,
        AgentReadiness = connection.AgentReadiness,
        OwnerSlackUserId = connection.OwnerSlackUserId,
        LastHeartbeatAt = connection.LastHeartbeatAt,
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