using System.ComponentModel.DataAnnotations;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public class WorkflowDispatchSnapshotRow
{
    [Key]
    [MaxLength(50)]
    public string WorkflowRunId { get; set; } = string.Empty;

    [Key]
    [MaxLength(128)]
    public string WorkId { get; set; } = string.Empty;

    [Required]
    public string SnapshotJson { get; set; } = "{}";
}
