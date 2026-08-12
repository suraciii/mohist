using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// Bound, user-visible <c>WorkflowArtifact</c> record. One row per
/// recorded artifact version. A later task run that records the same
/// path inserts a new row; the earlier row is never mutated.
/// </summary>
/// <remarks>
/// <para>
/// Persistence-side metadata that is not part of the core
/// <c>WorkflowArtifact</c> domain fact is kept here on the row:
/// <list type="bullet">
///   <item><description>
///     <c>ArtifactStoragePath</c> — generated/sanitized filesystem
///     location. Never derived from the source <c>Path</c>.
///   </description></item>
///   <item><description>
///     <c>ContentType</c> / <c>ContentHash</c> / <c>Size</c> —
///     transport/storage details for content retrieval.
///   </description></item>
///   <item><description>
    ///     <c>IssueNumber</c> / <c>ProjectId</c> — issue-scoped query
///     optimization; carried on the row, not in the workflow JSON.
///   </description></item>
///   <item><description>
///     <c>DisplayName</c> — derived from <c>Path</c> for UI use.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Latest grouping (<c>newest per path</c>) is a query projection over
/// this table, not a separate <c>LatestWorkflowArtifact</c> row.
/// </para>
/// </remarks>
public class WorkflowArtifactRow
{
    [Key]
    [MaxLength(64)]
    public string ArtifactId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string WorkflowRunId { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string TaskRunId { get; set; } = string.Empty;

    /// <summary>Consumed pending-upload identity used to make binding replay-safe.</summary>
    [MaxLength(64)]
    public string? SourceUploadId { get; set; }

    [Required]
    [MaxLength(1024)]
    public string Path { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>Generated or sanitized Mohist-side storage path segment.</summary>
    [Required]
    [MaxLength(1024)]
    public string ArtifactStoragePath { get; set; } = string.Empty;

    /// <summary>Recorded artifact shape: <c>file</c> or <c>directory</c>.</summary>
    [Required]
    [MaxLength(16)]
    public string Kind { get; set; } = "file";

    [MaxLength(128)]
    public string? ContentType { get; set; }

    [MaxLength(128)]
    public string? ContentHash { get; set; }

    public long? Size { get; set; }

    /// <summary>
    /// Carried on the row for issue-scoped query optimization only.
    /// Not part of the domain artifact fact.
    /// </summary>
    [MaxLength(256)]
    public string? ProjectId { get; set; }

    public int? IssueNumber { get; set; }

    /// <summary>
    /// Carried on the row for issue-scoped query optimization only.
    /// Not part of the domain artifact fact.
    /// </summary>
    /// <summary>
    /// UI display name derived from <see cref="Path"/>. Not part of
    /// artifact identity.
    /// </summary>
    [MaxLength(512)]
    public string? DisplayName { get; set; }
}
