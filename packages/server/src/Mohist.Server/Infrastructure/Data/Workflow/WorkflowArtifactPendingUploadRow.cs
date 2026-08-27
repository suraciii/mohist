using System.ComponentModel.DataAnnotations;

namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// Hidden pending upload row created when a runner uploads artifact
/// content before reporting the task result. Pending uploads are
/// <em>not</em> user-visible <c>WorkflowArtifact</c> records; they
/// become visible only after <c>WorkflowGrain.ReportResultAsync</c>
/// binds them during task result reporting.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency is keyed by
/// <c>(WorkflowRunId, WorkId, ActionAttemptId, Path)</c>. Same key + same
/// content hash returns the existing pending upload. Same key +
/// different content hash is rejected as a conflicting retry.
/// </para>
/// <para>
/// TTL cleanup applies to rows that never become bound because the
/// task result report never arrives.
/// </para>
/// </remarks>
public class WorkflowArtifactPendingUploadRow
{
    [Key]
    [MaxLength(64)]
    public string UploadId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string WorkflowRunId { get; set; } = string.Empty;

    /// <summary>Workflow work item the upload is attached to.</summary>
    [Required]
    [MaxLength(128)]
    public string WorkId { get; set; } = string.Empty;

    /// <summary>
    /// Server-derived producing task run id. The runner does not send
    /// an <c>attempt</c>; the server resolves it from the active work
    /// context.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string ActionAttemptId { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Captured artifact shape: <c>file</c> or <c>directory</c>. The
    /// upload service infers this from the request content type and
    /// persists the value so the binding flow can mirror it on the
    /// bound <c>WorkflowArtifactRow</c>.
    /// </summary>
    [Required]
    [MaxLength(16)]
    public string Kind { get; set; } = "file";

    /// <summary>
    /// Optional count of contained files for a directory upload. The
    /// upload service sets this after the directory envelope is
    /// decoded and written through <c>WriteDirectoryAsync</c>.
    /// </summary>
    public int? FileCount { get; set; }

    [MaxLength(128)]
    public string? ContentType { get; set; }

    [MaxLength(128)]
    public string? ContentHash { get; set; }

    public long? Size { get; set; }

    [Required]
    [MaxLength(1024)]
    public string StoragePath { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Expiry timestamp. Rows past expiry are eligible for TTL
    /// cleanup if they are still unbound.
    /// </summary>
    [Required]
    public DateTimeOffset ExpiresAt { get; set; }
}
