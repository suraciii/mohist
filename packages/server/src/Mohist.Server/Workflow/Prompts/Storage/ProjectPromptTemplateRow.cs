namespace Mohist.Server.Workflow.Prompts.Storage;

public class ProjectPromptTemplateRow
{
    public string ProjectId { get; set; } = "";
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string TagsJson { get; set; } = "[]";
    public string? Stage { get; set; }
    public string Body { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
