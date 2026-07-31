using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Persistence boundary for the Slack provider inbox. Mirrors
/// <see cref="Mohist.Server.Inbox.InboxStore"/>'s dedup-on-insert idiom:
/// a unique index on <c>(ConnectionId, SlackMessageIdentity)</c> turns a
/// duplicate insert into a constraint violation the caller resolves to
/// "already accepted" without a pre-flight SELECT.
/// </summary>
/// <remarks>
/// The capacity check is intentionally <c>WHERE DispatchedAt IS NULL</c>
/// — only pending (unprocessed) entries count toward the cap. Once the
/// launcher has been invoked for a message the entry is dispatched and
/// frees a slot, matching the spec's "refuse new ingress without
/// dropping accepted events".
/// </remarks>
public sealed class SlackProviderInboxStore : IScopedService, IAgentConnectionProviderCleanup
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISlackConnectionHealthBackpressurer _healthBackpressurer;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<SlackProviderOptions> _options;

    public SlackProviderInboxStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider,
        IOptions<SlackProviderOptions> options,
        ISlackConnectionHealthBackpressurer healthBackpressurer)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _options = options;
        _healthBackpressurer = healthBackpressurer;
    }

    /// <summary>
    /// Accepts one Slack message identity. The unique-index conflict
    /// resolves to <see cref="SlackProviderInboxAcceptResult.AlreadyExisted"/>
    /// — no second write, no second SessionInput. Capacity is checked
    /// against the count of pending rows for the connection, NOT the
    /// total rows; a flooded but processed inbox must not refuse new
    /// events just because history is long.
    /// </summary>
    public async Task<SlackProviderInboxAcceptResult> AcceptAsync(
        SlackProviderInboxDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var identityError = draft.Identity.Validate();
        if (!string.IsNullOrEmpty(identityError))
            throw new ArgumentException(identityError, nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.ProjectId))
            throw new ArgumentException("ProjectId is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.ConnectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.SlackUserId))
            throw new ArgumentException("SlackUserId is required.", nameof(draft));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var identity = draft.Identity.AsKey();
        var existing = await db.SlackProviderInboxRows.AsNoTracking()
            .Where(row => row.ConnectionId == draft.ConnectionId
                && row.SlackMessageIdentity == identity)
            .Select(row => new { row.Id })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new SlackProviderInboxAcceptResult(existing.Id, AlreadyExisted: true);

        var pendingCount = await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == draft.ConnectionId
                && row.DispatchedAt == null)
            .CountAsync(ct);
        if (pendingCount >= _options.Value.InboxCapacityPerConnection)
        {
            await transaction.RollbackAsync(ct);
            await _healthBackpressurer.FlipBackpressuredAsync(
                draft.ProjectId,
                draft.ConnectionId,
                SlackProviderBackpressureReasons.InboxOverflow,
                ct);
            throw new SlackProviderInboxCapacityExceededException(
                draft.ProjectId, draft.ConnectionId, _options.Value.InboxCapacityPerConnection);
        }

        var now = _timeProvider.GetUtcNow();
        var row = new SlackProviderInboxRow
        {
            Id = $"slkinb_{Guid.NewGuid():N}",
            ProjectId = draft.ProjectId,
            ConnectionId = draft.ConnectionId,
            SlackMessageIdentity = identity,
            WorkspaceTeamId = draft.Identity.WorkspaceTeamId,
            DmConversationId = draft.Identity.DmConversationId,
            SlackUserId = draft.SlackUserId,
            AcceptedAt = now,
            CreatedAt = now,
        };
        db.SlackProviderInboxRows.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SlackProviderInboxAcceptResult(row.Id, AlreadyExisted: false);
        }
        catch (DbUpdateException ex) when (IsIdentityConflict(ex))
        {
            db.Entry(row).State = EntityState.Detached;
            var duplicate = await db.SlackProviderInboxRows.AsNoTracking()
                .Where(r => r.ConnectionId == draft.ConnectionId
                    && r.SlackMessageIdentity == identity)
                .Select(r => new { r.Id })
                .FirstOrDefaultAsync(ct);
            return new SlackProviderInboxAcceptResult(
                duplicate?.Id ?? $"slkinb_duplicate:{identity}",
                AlreadyExisted: true);
        }
    }

    public async Task<SlackProviderInboxRoute> GetOrAssignRouteAsync(
        string projectId,
        string id,
        SlackProviderInboxRouteDraft draft,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Kind);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _ = await db.SlackProviderInboxRows
            .Where(row => row.ProjectId == projectId && row.Id == id && row.RouteKind == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.RouteKind, draft.Kind)
                .SetProperty(row => row.RouteSessionId, draft.SessionId)
                .SetProperty(row => row.RouteTurnId, draft.TurnId), ct);
        var route = await db.SlackProviderInboxRows.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Id == id)
            .Select(row => new { row.RouteKind, row.RouteSessionId, row.RouteTurnId })
            .SingleOrDefaultAsync(ct);
        if (route?.RouteKind is null)
            throw new InvalidOperationException($"Slack inbox entry {id} does not exist.");

        return new SlackProviderInboxRoute(route.RouteKind, route.RouteSessionId, route.RouteTurnId);
    }

    public async Task<SlackProviderInboxRoute?> GetRouteAsync(
        string projectId,
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var route = await db.SlackProviderInboxRows.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Id == id)
            .Select(row => new { row.RouteKind, row.RouteSessionId, row.RouteTurnId })
            .SingleOrDefaultAsync(ct);
        return route?.RouteKind is null
            ? null
            : new SlackProviderInboxRoute(route.RouteKind, route.RouteSessionId, route.RouteTurnId);
    }

    public async Task<string> SetRouteSessionIdAsync(
        string projectId,
        string id,
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.SlackProviderInboxRows
            .Where(row => row.ProjectId == projectId && row.Id == id && row.RouteSessionId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RouteSessionId, sessionId), ct);
        var persisted = await db.SlackProviderInboxRows.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Id == id)
            .Select(row => row.RouteSessionId)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(persisted))
            throw new InvalidOperationException($"Slack inbox entry {id} does not have a routed session.");
        if (!string.Equals(persisted, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Slack inbox entry {id} resolved to a different session.");

        return persisted;
    }

    /// <summary>
    /// Marks an accepted inbox entry dispatched. Called after the
    /// launcher has been invoked for that identity; freeing the slot is
    /// what keeps the per-connection capacity bounded against
    /// already-handed-off events. Returns the affected row count so the
    /// caller can detect double-dispatch on retry.
    /// </summary>
    public async Task<int> MarkDispatchedAsync(string projectId, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackProviderInboxRows
            .Where(row => row.ProjectId == projectId
                && row.Id == id
                && row.DispatchedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.DispatchedAt, now), ct);
    }

    public async Task<SlackProviderInboxList> ListAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackProviderInboxRows.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        return new SlackProviderInboxList(rows.Select(ToEntry).ToList());
    }

    public Task<int> CountPendingAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        return CountPendingCoreAsync(projectId, connectionId, ct);
    }

    private async Task<int> CountPendingCoreAsync(string projectId, string connectionId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackProviderInboxRows
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.DispatchedAt == null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Cascades provider rows for one Connection. Called from
    /// <c>AgentConnectionStore.DeleteAsync</c> in addition to
    /// credentials. Idempotent: a missing Connection deletes zero rows.
    /// </summary>
    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackProviderInboxRows
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }

    private static bool IsIdentityConflict(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && (sqlite.Message?.Contains("SlackProviderInboxRows", StringComparison.OrdinalIgnoreCase) ?? false);

    private static SlackProviderInboxEntry ToEntry(SlackProviderInboxRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ConnectionId = row.ConnectionId,
        SlackMessageIdentity = row.SlackMessageIdentity,
        WorkspaceTeamId = row.WorkspaceTeamId,
        DmConversationId = row.DmConversationId,
        SlackUserId = row.SlackUserId,
        AcceptedAt = row.AcceptedAt,
        DispatchedAt = row.DispatchedAt,
        CreatedAt = row.CreatedAt,
    };
}
