using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mohist.Server.Workflow.Storage;

[Table("workflow_runs")]
public class WorkflowRunEntity
{
    [Key]
    [MaxLength(50)]
    public string WorkflowRunId { get; set; } = string.Empty;

    [Required]
    public string State { get; set; } = "{}";

    [MaxLength(100)]
    public string? MetadataName { get; set; }

    [MaxLength(50)]
    public string? MetadataProjectId { get; set; }

    public long MetadataCreatedAt { get; set; }

    public string? MetadataLabels { get; set; }

    [MaxLength(50)]
    public string? MetadataDefinitionId { get; set; }

    [MaxLength(20)]
    public string Phase { get; set; } = "Pending";

    [MaxLength(50)]
    public string? CurrentStageId { get; set; }

    public long PhaseUpdatedAt { get; set; }
}
