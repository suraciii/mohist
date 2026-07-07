using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Persistence boundary for project-scoped Agent subscriptions. One row per
/// subscription; queries return domain <see cref="AgentSubscription"/>
/// projections. Mutations expose lifecycle transitions used by the
/// subscription CRUD API (issue-391 T-002).
/// </summary>
public sealed class AgentSubscriptionStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public AgentSubscriptionStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Fetches a subscription by id. Returns <c>null</c> when no row exists;
    /// the caller is responsible for cross-scope ownership checks
    /// (project/agent) via the route or service layer.
    /// </summary>
    public async Task<AgentSubscription?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id required", nameof(id));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    /// <summary>
    /// Lists subscriptions owned by an Agent, ordered most-recently updated
    /// first so the Web UI mutation list surfaces fresh items at the top.
    /// </summary>
    public async Task<IReadOnlyList<AgentSubscription>> ListByAgentAsync(
        string projectId,
        string agentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Materialize then sort: SQLite does not support ORDER BY on
        // DateTimeOffset columns. Subscription volume per Agent is small,
        // so this in-memory pass is cheap; the
        // IX_AgentSubscriptions_ProjectId_AgentId index still narrows the
        // candidate set efficiently in SQL.
        var rows = await db.AgentSubscriptions.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.AgentId == agentId)
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.UpdatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(ToDomain)
            .ToList();
    }

    /// <summary>
    /// Lists subscriptions for a project — used by the dispatch handler
    /// (T-003) to fetch the candidate set for a CloudEvent.
    /// </summary>
    public async Task<IReadOnlyList<AgentSubscription>> ListByProjectAsync(
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSubscriptions.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.UpdatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(ToDomain)
            .ToList();
    }

    /// <summary>
    /// Inserts a new subscription row. Throws <see cref="AgentSubscriptionNameConflictException"/>
    /// when the Agent already owns a subscription with the same
    /// <see cref="AgentSubscription.Name"/>. The unique index
    /// <c>UX_AgentSubscriptions_AgentId_Name</c> is the source of truth.
    /// </summary>
    public async Task<AgentSubscription> CreateAsync(
        AgentSubscription subscription,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (string.IsNullOrWhiteSpace(subscription.Id))
            throw new ArgumentException("id required", nameof(subscription));
        if (string.IsNullOrWhiteSpace(subscription.ProjectId))
            throw new ArgumentException("projectId required", nameof(subscription));
        if (string.IsNullOrWhiteSpace(subscription.AgentId))
            throw new ArgumentException("agentId required", nameof(subscription));

        var now = _timeProvider.GetUtcNow();
        subscription.CreatedAt = now;
        subscription.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(subscription.Status))
            subscription.Status = SubscriptionStatus.Active;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = ToRow(subscription);
        db.AgentSubscriptions.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new AgentSubscriptionNameConflictException(subscription.AgentId, subscription.Name);
        }
        return subscription;
    }

    /// <summary>
    /// Updates mutable subscription fields in place and bumps
    /// <see cref="AgentSubscription.UpdatedAt"/>. Does NOT touch
    /// <see cref="AgentSubscription.Status"/> (archive/restore go through
    /// dedicated transitions). Returns <c>null</c> when the row no longer
    /// exists.
    /// </summary>
    public async Task<AgentSubscription?> UpdateAsync(
        string id,
        string? name,
        SubscriptionFilter? filter,
        string? responsePrompt,
        int? priority,
        bool priorityTouched,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id required", nameof(id));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentSubscriptions
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;

        if (name is not null) row.Name = name;
        if (filter is not null)
        {
            row.FilterType = filter.Type;
            row.FilterSource = filter.Source;
            row.FilterSubject = filter.Subject;
        }
        if (responsePrompt is not null) row.ResponsePrompt = responsePrompt;
        if (priorityTouched) row.Priority = priority;
        row.UpdatedAt = _timeProvider.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new AgentSubscriptionNameConflictException(row.AgentId, row.Name);
        }
        return ToDomain(row);
    }

    /// <summary>
    /// Transitions the subscription to <c>archived</c>. Idempotent — archiving
    /// an already-archived subscription returns the current row without
    /// advancing <see cref="AgentSubscription.UpdatedAt"/>. Returns
    /// <c>null</c> when the row no longer exists.
    /// </summary>
    public async Task<AgentSubscription?> ArchiveAsync(string id, CancellationToken ct = default)
    {
        return await SetStatusAsync(id, SubscriptionStatus.Active, SubscriptionStatus.Archived, ct);
    }

    /// <summary>
    /// Transitions the subscription to <c>active</c>. Idempotent — restoring
    /// an already-active subscription returns the current row without
    /// advancing <see cref="AgentSubscription.UpdatedAt"/>. Returns
    /// <c>null</c> when the row no longer exists.
    /// </summary>
    public async Task<AgentSubscription?> RestoreAsync(string id, CancellationToken ct = default)
    {
        return await SetStatusAsync(id, SubscriptionStatus.Archived, SubscriptionStatus.Active, ct);
    }

    /// <summary>
    /// Deletes the subscription row. Returns whether a row was removed.
    /// Already-running sessions remain unaffected — that lifecycle belongs to
    /// <c>AgentJobGrain</c>, not to subscription storage.
    /// </summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id required", nameof(id));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentSubscriptions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return false;
        db.AgentSubscriptions.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<AgentSubscription?> SetStatusAsync(
        string id,
        string expectedCurrentStatus,
        string newStatus,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id required", nameof(id));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentSubscriptions.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        if (string.Equals(row.Status, newStatus, StringComparison.Ordinal))
            return ToDomain(row);

        if (!string.Equals(row.Status, expectedCurrentStatus, StringComparison.Ordinal))
            return ToDomain(row);

        row.Status = newStatus;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    private static AgentSubscription ToDomain(AgentSubscriptionRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        AgentId = row.AgentId,
        Name = row.Name,
        Filter = new SubscriptionFilter
        {
            Type = row.FilterType,
            Source = row.FilterSource,
            Subject = row.FilterSubject,
        },
        ResponsePrompt = row.ResponsePrompt,
        Priority = row.Priority,
        Status = row.Status,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static AgentSubscriptionRow ToRow(AgentSubscription subscription) => new()
    {
        Id = subscription.Id,
        ProjectId = subscription.ProjectId,
        AgentId = subscription.AgentId,
        Name = subscription.Name,
        FilterType = subscription.Filter.Type,
        FilterSource = subscription.Filter.Source,
        FilterSubject = subscription.Filter.Subject,
        ResponsePrompt = subscription.ResponsePrompt,
        Priority = subscription.Priority,
        Status = subscription.Status,
        CreatedAt = subscription.CreatedAt,
        UpdatedAt = subscription.UpdatedAt,
    };

    private static bool IsNameConflict(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && sqlite.Message.Contains("AgentSubscriptions", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Raised when a subscription name collides with an existing row on the
/// same Agent. Maps to a 409 conflict on the subscription CRUD API.
/// </summary>
public sealed class AgentSubscriptionNameConflictException : Exception
{
    public string AgentId { get; }
    public string Name { get; }

    public AgentSubscriptionNameConflictException(string agentId, string name)
        : base($"Agent '{agentId}' already owns a subscription named '{name}'.")
    {
        AgentId = agentId;
        Name = name;
    }
}
