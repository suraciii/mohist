using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Inbox;

/// <summary>
/// Read-only projection boundary for the project inbox. Returns
/// <see cref="InboxItemView"/>s for one project, excluding archived
/// items and ordering most-recent-first by <c>CreatedAt</c>.
///
/// All queries are project-scoped: an item whose <c>ProjectId</c>
/// differs from the resolved project is never returned. SQLite cannot
/// order <see cref="DateTimeOffset"/> columns, so the list query uses
/// the project/archive predicates in SQL and applies the final
/// most-recent-first ordering after materialization.
/// </summary>
public sealed class InboxQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public InboxQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Lists the project's non-archived inbox items, most-recent-first
    /// by <c>CreatedAt</c>. Returns an empty list when the project has
    /// no items (or only archived ones).
    /// </summary>
    public Task<IReadOnlyList<InboxItemView>> ListAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
        return ListCoreAsync(projectId, ct);
    }

    public Task<int> CountUnreadAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
        return CountUnreadCoreAsync(projectId, ct);
    }

    private async Task<int> CountUnreadCoreAsync(string projectId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.InboxItems.AsNoTracking()
            .CountAsync(r => r.ProjectId == projectId && r.ArchivedAt == null && r.ReadAt == null, ct);
    }

    private async Task<IReadOnlyList<InboxItemView>> ListCoreAsync(string projectId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Materialize first, then sort: SQLite does not support
        // ORDER BY on DateTimeOffset columns, and the
        // (ProjectId, CreatedAt) compound index still narrows the
        // candidate set efficiently even when the final ordering
        // happens in memory.
        var rows = await db.InboxItems.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ArchivedAt == null)
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(ToView)
            .ToList();
    }

    private static InboxItemView ToView(InboxItemRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        IssueNumber = row.IssueNumber,
        IssueTitle = row.IssueTitle,
        NotificationKind = row.NotificationKind,
        SourceEventSource = row.SourceEventSource,
        SourceEventId = row.SourceEventId,
        CreatedAt = row.CreatedAt,
        ReadAt = row.ReadAt,
        ArchivedAt = row.ArchivedAt,
    };
}
