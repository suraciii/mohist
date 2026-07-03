using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Runner;

/// <summary>
/// Dedicated persistence and cursor-paginated query for ops task
/// execution logs. Mirrors <see cref="RunnerWorkStore"/>'s
/// placement convention and writes directly with no grain
/// involvement, matching the artifact-upload independence
/// described in design D1 / D8.
/// </summary>
public class TaskLogStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public TaskLogStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    /// <summary>
    /// Persists a single batch of entries for the given
    /// <paramref name="workId"/> directly to the dedicated store.
    /// Writes the batch metadata (truncation flag) in the same
    /// transaction so the upload is atomic. Existing rows for the
    /// same work item are removed first because the runner emits a
    /// single terminal batch per work item (design D6); a retry
    /// therefore replaces the previous attempt cleanly.
    /// </summary>
    public async Task AppendAsync(
        string ownerKind,
        string ownerId,
        string workId,
        IReadOnlyList<TaskLogLine> entries,
        bool truncated,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerKind))
            throw new ArgumentException("ownerKind must be provided", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("ownerId must be provided", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workId must be provided", nameof(workId));
        ArgumentNullException.ThrowIfNull(entries);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var existingEntries = await db.TaskLogEntries
            .Where(e => e.OwnerKind == ownerKind && e.OwnerId == ownerId && e.WorkId == workId)
            .ToListAsync(ct);
        if (existingEntries.Count > 0)
        {
            db.TaskLogEntries.RemoveRange(existingEntries);
        }

        var existingBatch = await db.TaskLogBatches
            .FirstOrDefaultAsync(b => b.OwnerKind == ownerKind && b.OwnerId == ownerId && b.WorkId == workId, ct);
        if (existingBatch is null)
        {
            db.TaskLogBatches.Add(new TaskLogBatchRow
            {
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                WorkId = workId,
                Truncated = truncated,
                UploadedAt = _time.GetUtcNow(),
            });
        }
        else
        {
            existingBatch.Truncated = truncated;
            existingBatch.UploadedAt = _time.GetUtcNow();
        }

        if (entries.Count > 0)
        {
            var rows = entries.Select(e => new TaskLogEntryRow
            {
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                WorkId = workId,
                Seq = e.Seq,
                Timestamp = e.Timestamp,
                Source = e.Source,
                Text = e.Text,
            });
            db.TaskLogEntries.AddRange(rows);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Cursor-paginated query over a work item's captured entries
    /// in ascending <c>Seq</c> order. The cursor is the last seq
    /// seen on the previous page; the returned page starts strictly
    /// after it. Pagination is stable because the runner does not
    /// reuse seq values for discarded head lines (design D6).
    /// </summary>
    /// <param name="afterSeq">
    /// When null, the first page (the smallest seq) is returned.
    /// When non-null, entries with seq &gt; afterSeq are returned.
    /// </param>
    /// <param name="limit">
    /// Maximum number of entries to return. Values &lt;= 0 default
    /// to <see cref="DefaultLimit"/>.
    /// </param>
    public async Task<TaskLogPage> QueryAsync(
        string ownerKind,
        string ownerId,
        string workId,
        long? afterSeq,
        int? limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerKind))
            throw new ArgumentException("ownerKind must be provided", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("ownerId must be provided", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workId must be provided", nameof(workId));

        var pageSize = limit is null or <= 0 ? DefaultLimit : limit.Value;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var batch = await db.TaskLogBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.OwnerKind == ownerKind && b.OwnerId == ownerId && b.WorkId == workId, ct);

        var query = db.TaskLogEntries.AsNoTracking()
            .Where(e => e.OwnerKind == ownerKind && e.OwnerId == ownerId && e.WorkId == workId);
        if (afterSeq.HasValue)
            query = query.Where(e => e.Seq > afterSeq.Value);

        // Take one extra to know whether another page exists; this
        // avoids a second COUNT roundtrip while keeping the contract
        // simple (caller never sees the sentinel row).
        var rows = await query
            .OrderBy(e => e.Seq)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        var visible = hasMore ? rows.Take(pageSize).ToList() : rows;

        var lines = visible
            .Select(r => new TaskLogLine(r.Seq, r.Timestamp, r.Source, r.Text))
            .ToList();
        long? nextCursor = hasMore && lines.Count > 0 ? lines[^1].Seq : null;

        return new TaskLogPage(lines, nextCursor, batch?.Truncated ?? false);
    }

    public const int DefaultLimit = 500;
}