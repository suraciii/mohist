using System.ComponentModel.DataAnnotations;

namespace Mohist.Server.Infrastructure.Data.Runner;

/// <summary>
/// Batch-level metadata for a task log upload. One row per work
/// item (the runner flushes a single terminal batch per work item),
/// keyed by <c>(OwnerKind, OwnerId, WorkId)</c>.
/// <c>Truncated</c> reflects whether the runner dropped head lines
/// at capture time so the web client can signal that to the user.
/// </summary>
public class TaskLogBatchRow
{
    [MaxLength(16)]
    public string OwnerKind { get; set; } = string.Empty;

    [MaxLength(256)]
    public string OwnerId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string WorkId { get; set; } = string.Empty;

    public bool Truncated { get; set; }

    public bool Terminal { get; set; }

    [MaxLength(64)]
    public string? TerminalDigest { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
