using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Runner;

/// <summary>
/// Dedicated persistence and cursor-paginated query for ops task
/// execution logs. Mirrors <see cref="RunnerWorkStore"/>'s
/// placement convention and writes directly with no grain
/// involvement, matching the artifact-upload independence.
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
    /// transaction so the upload is atomic. Existing entry rows are
    /// kept for incremental batches; terminal reconciliation batches
    /// first remove persisted rows that are no longer in the runner's
    /// retained snapshot. Incoming rows whose <c>Seq</c> is already
    /// present are skipped so batches merge idempotently by sequence.
    /// </summary>
    public async Task AppendAsync(
        string ownerKind,
        string ownerId,
        string workId,
        IReadOnlyList<TaskLogLine> entries,
        bool truncated,
        bool terminal = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerKind))
            throw new ArgumentException("ownerKind must be provided", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("ownerId must be provided", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workId must be provided", nameof(workId));
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > TaskLogUploadLimits.MaxEntries)
            throw new ArgumentException($"Too many task-log entries ({entries.Count}); max {TaskLogUploadLimits.MaxEntries}", nameof(entries));
        ValidateEntries(entries);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (terminal)
        {
            var retainedSeqs = entries.Select(e => e.Seq).ToHashSet();
            var staleRows = await db.TaskLogEntries
                .Where(e => e.OwnerKind == ownerKind && e.OwnerId == ownerId && e.WorkId == workId)
                .Where(e => !retainedSeqs.Contains(e.Seq))
                .ToListAsync(ct);
            db.TaskLogEntries.RemoveRange(staleRows);
        }

        var existingSeqs = await db.TaskLogEntries
            .Where(e => e.OwnerKind == ownerKind && e.OwnerId == ownerId && e.WorkId == workId)
            .Select(e => e.Seq)
            .ToHashSetAsync(ct);

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
            var rows = entries
                .Where(e => !existingSeqs.Contains(e.Seq))
                .Select(e => new TaskLogEntryRow
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
    /// reuse seq values for discarded head lines.
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

        var pageSize = ClampLimit(limit);

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
    public const int MaxLimit = 5_000;

    private static int ClampLimit(int? limit)
    {
        if (limit is null or <= 0) return DefaultLimit;
        return Math.Min(limit.Value, MaxLimit);
    }

    private static void ValidateEntries(IReadOnlyList<TaskLogLine> entries)
    {
        long previous = 0;
        var totalTextLength = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Seq <= 0)
                throw new ArgumentException($"Task-log seq must be positive at index {i}", nameof(entries));
            if (entry.Seq <= previous)
                throw new ArgumentException("Task-log seq values must be strictly increasing", nameof(entries));
            if (entry.Timestamp == default)
                throw new ArgumentException($"Task-log timestamp must be provided at index {i}", nameof(entries));
            if (string.IsNullOrWhiteSpace(entry.Source))
                throw new ArgumentException($"Task-log source must be provided at index {i}", nameof(entries));
            if (entry.Source.Length > TaskLogUploadLimits.MaxSourceLength)
                throw new ArgumentException($"Task-log source exceeds {TaskLogUploadLimits.MaxSourceLength} characters at index {i}", nameof(entries));
            if (entry.Text is null)
                throw new ArgumentException($"Task-log text must be provided at index {i}", nameof(entries));
            if (entry.Text.Length > TaskLogUploadLimits.MaxTextLength)
                throw new ArgumentException($"Task-log text exceeds {TaskLogUploadLimits.MaxTextLength} characters at index {i}", nameof(entries));
            totalTextLength += entry.Text.Length;
            if (totalTextLength > TaskLogUploadLimits.MaxTotalTextLength)
                throw new ArgumentException($"Task-log text payload exceeds {TaskLogUploadLimits.MaxTotalTextLength} characters", nameof(entries));
            previous = entry.Seq;
        }
    }
}
