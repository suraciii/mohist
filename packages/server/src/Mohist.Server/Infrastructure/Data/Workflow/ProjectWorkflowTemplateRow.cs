namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// 项目 workflow 模板 (key: ProjectId + TemplateId)。
/// 每个项目可以拥有多个自定义模板, 与系统模板并存。
/// </summary>
public class ProjectWorkflowTemplateRow
{
    public string ProjectId { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>
    /// WorkflowDefinition JSON - 模板结构 (stages/tasks/checks + 内嵌 variables 段)。
    /// </summary>
    public string Template { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
