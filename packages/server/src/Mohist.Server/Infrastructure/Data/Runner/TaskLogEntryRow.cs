using System.ComponentModel.DataAnnotations;

namespace Mohist.Server.Infrastructure.Data.Runner;

/// <summary>
/// One captured line of an ops task's execution log, persisted
/// independently of <c>WorkflowRun</c> / <c>WorkResult</c> / the report
/// channel. Mirrors how <c>WorkflowArtifactRow</c> stores review
/// evidence without participating in status adjudication.
///
/// <para>
/// The row key is <c>(OwnerKind, OwnerId, WorkId, Seq)</c>: the same
/// shape the runner uses to address the buffered entries. The
/// composite index supports cursor pagination in <c>TaskLogStore</c>.
/// </para>
/// </summary>
public class TaskLogEntryRow
{
    public long Id { get; set; }

    [MaxLength(16)]
    public string OwnerKind { get; set; } = string.Empty;

    [MaxLength(256)]
    public string OwnerId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string WorkId { get; set; } = string.Empty;

    /// <summary>
    /// Work-scoped monotonic sequence assigned by the runner sink
    /// before buffering. Discarded head lines (capacity overflow)
    /// do not reuse already-allocated seq values, so the retained
    /// range stays contiguous and cursor pagination remains stable.
    /// </summary>
    public long Seq { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    [MaxLength(64)]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Already-masked text produced by the runner sink. SQLite
    /// stores TEXT without a length cap; the head-drop truncation
    /// in <c>TaskLogCollector</c> bounds the total per-line bytes,
    /// so a TEXT column is the right choice here.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}