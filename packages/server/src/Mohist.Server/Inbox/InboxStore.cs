using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Inbox;

/// <summary>
/// Persistence boundary for the project inbox. Writes one row per
/// CloudEvent the projection accepts, using the source plus event id
/// unique index for idempotency. Mutations
/// (<see cref="MarkReadAsync"/>, <see cref="MarkAllReadAsync"/>,
/// <see cref="ArchiveAsync"/>) are project-scoped: they filter
/// <c>WHERE ProjectId = @projectId</c> so a caller scoped to project A
/// cannot read or mutate project B's items — affected row counts
/// reported back to the caller drive the API's 404 behavior.
/// </summary>
public sealed class InboxStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public InboxStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Inserts a new inbox item. Re-inserting the same
    /// <see cref="InboxItemDraft.SourceEventId"/> is treated as
    /// "already projected": the existing row is left untouched and
    /// <see cref="InboxInsertResult.AlreadyExisted"/> is true. The
    /// unique-constraint conflict is the dedup signal — no
    /// pre-flight SELECT is performed.
    /// </summary>
    public async Task<InboxInsertResult> InsertAsync(InboxItemDraft draft, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await InsertAsync(db, draft, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<InboxInsertResult> InsertAsync(
        MohistDbContext db,
        InboxItemDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (draft is null) throw new ArgumentNullException(nameof(draft));
        ValidateDraft(draft);

        var id = $"inb_{Guid.NewGuid():N}";
        var createdAt = draft.CreatedAt ?? DateTimeOffset.UtcNow;
        var row = new InboxItemRow
        {
            Id = id,
            ProjectId = draft.ProjectId,
            IssueNumber = draft.IssueNumber,
            IssueTitle = draft.IssueTitle ?? string.Empty,
            NotificationKind = draft.NotificationKind,
            SourceEventSource = draft.SourceEventSource,
            SourceEventId = draft.SourceEventId,
            CreatedAt = createdAt,
        };

        db.InboxItems.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return new InboxInsertResult(id, AlreadyExisted: false);
        }
        catch (DbUpdateException ex) when (IsSourceEventConflict(ex))
        {
            db.Entry(row).State = EntityState.Detached;
            var existing = await db.InboxItems.AsNoTracking()
                .Where(r => r.SourceEventSource == draft.SourceEventSource
                    && r.SourceEventId == draft.SourceEventId)
                .Select(r => new { r.Id })
                .FirstOrDefaultAsync(ct);
            return new InboxInsertResult(
                existing?.Id ?? draft.SourceEventId,
                AlreadyExisted: true);
        }
    }

    /// <summary>
    /// Marks a single inbox item read. Filter is project-scoped: an
    /// item whose <c>ProjectId</c> differs from <paramref name="projectId"/>
    /// yields 0 affected rows, which the API layer translates to 404.
    /// Archived items are also untouched by this call.
    /// </summary>
    public Task<int> MarkReadAsync(string projectId, string itemId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("itemId required", nameof(itemId));
        return MarkReadCoreAsync(projectId, itemId, ct);
    }

    private async Task<int> MarkReadCoreAsync(string projectId, string itemId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Filter only on project + id + archived state: an already-read
        // item is still "the item" and the call should be idempotent at
        // the API layer (a repeated "mark read" must not 404). The
        // ArchivedAt == null filter is what surfaces "this item is
        // archived" as a 404 at the route layer.
        return await db.InboxItems
            .Where(r => r.ProjectId == projectId
                && r.Id == itemId
                && r.ArchivedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.ReadAt, now), ct);
    }

    /// <summary>
    /// Marks every non-archived inbox item in the project read. Filter
    /// is project-scoped: items in other projects are not touched.
    /// Returns the count of rows that transitioned to read in this
    /// call (already-read items do not count).
    /// </summary>
    public Task<int> MarkAllReadAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
        return MarkAllReadCoreAsync(projectId, ct);
    }

    private async Task<int> MarkAllReadCoreAsync(string projectId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.InboxItems
            .Where(r => r.ProjectId == projectId
                && r.ReadAt == null
                && r.ArchivedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.ReadAt, now), ct);
    }

    /// <summary>
    /// Archives (dismisses) a single inbox item so it is excluded from
    /// the default list. Filter is project-scoped: an item whose
    /// <c>ProjectId</c> differs from <paramref name="projectId"/>
    /// yields 0 affected rows.
    /// </summary>
    public Task<int> ArchiveAsync(string projectId, string itemId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("itemId required", nameof(itemId));
        return ArchiveCoreAsync(projectId, itemId, ct);
    }

    private async Task<int> ArchiveCoreAsync(string projectId, string itemId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.InboxItems
            .Where(r => r.ProjectId == projectId
                && r.Id == itemId
                && r.ArchivedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.ArchivedAt, now), ct);
    }

    private static bool IsSourceEventConflict(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19 // SQLITE_CONSTRAINT
            && (sqlite.Message?.Contains("InboxItems", StringComparison.OrdinalIgnoreCase) ?? false);

    private static void ValidateDraft(InboxItemDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.ProjectId)) throw new ArgumentException("ProjectId required", nameof(draft));
        if (draft.IssueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(draft), "IssueNumber must be positive");
        if (!NotificationKinds.IsDefined(draft.NotificationKind)) throw new ArgumentException("NotificationKind must be one of the MVP inbox kinds", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.SourceEventSource)) throw new ArgumentException("SourceEventSource required", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.SourceEventId)) throw new ArgumentException("SourceEventId required", nameof(draft));
    }
}
