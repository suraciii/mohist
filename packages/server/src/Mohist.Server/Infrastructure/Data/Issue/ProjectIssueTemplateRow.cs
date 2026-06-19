namespace Mohist.Server.Infrastructure.Data.Issue;

/// <summary>
/// 项目 issue 模板 (key: ProjectId + Name)。
/// 每个项目可以拥有多个自定义模板, 与内置默认模板并存。
/// Mirrors <see cref="Workflow.ProjectWorkflowTemplateRow"/>.
/// </summary>
public class ProjectIssueTemplateRow
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IIssueTemplate JSON - 模板结构 (frontmatter + sections).
    /// </summary>
    public string Template { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
