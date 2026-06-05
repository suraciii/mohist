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

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public string? MetadataProjectId { get; set; }
}
