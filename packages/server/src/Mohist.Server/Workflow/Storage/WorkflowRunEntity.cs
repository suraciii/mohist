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

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? MetadataProjectId { get; set; }
}
