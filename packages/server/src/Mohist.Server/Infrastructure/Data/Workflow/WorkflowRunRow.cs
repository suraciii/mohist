using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public class WorkflowRunRow
{
    [Key]
    [MaxLength(50)]
    public string WorkflowRunId { get; set; } = string.Empty;

    [Required]
    public string State { get; set; } = "{}";

    [MaxLength(64)]
    public string? EpicId { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? MetadataProjectId { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTime? CreatedAt { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? AssignedWorkerId { get; set; }

    /// <summary>
    /// VIRTUAL computed column projecting the persisted
    /// <c>State.readySince</c> fairness ordering key (when the run last
    /// (re-)entered Ready). Non-stored: the scheduler reads it only to ORDER
    /// Ready runs round-robin (<c>ReadySince ASC</c>), never to filter. The
    /// matching <c>IX_WorkflowRuns_Status_ReadySince</c> covering index is
    /// declared in <c>MohistDbContext</c>. Typed as <see cref="DateTime"/> to
    /// match <see cref="CreatedAt"/> — the SQLite EF provider cannot translate
    /// <c>ORDER BY</c> over <c>DateTimeOffset</c>, only <c>DateTime</c>.
    /// </summary>
    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTime? ReadySince { get; set; }

    /// <summary>
    /// Issue-318 D3: STORED computed column mirroring the persisted
    /// <c>State.status</c> enum value, normalized to lowercase so the
    /// scheduler can filter on <c>status</c> at the database layer
    /// without deserializing the JSON state. Mirrors the
    /// <c>MetadataProjectId</c> precedent above. The matching
    /// <c>IX_WorkflowRuns_Status</c> index is declared on the
    /// <c>WorkflowRunRow</c> entity in <c>MohistDbContext</c>; this row
    /// model just exposes the projected scalar for EF reads/writes.
    /// </summary>
    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? Status { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public int? IssueNumber { get; set; }
}
