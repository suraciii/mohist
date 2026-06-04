namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// 项目 workflow profile (key: ProjectId, 1:1 与 project)。
/// 存储项目级默认模板引用 + 项目级变量配置。
/// </summary>
public class ProjectWorkflowProfileRow
{
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// 项目默认模板 ID。可为 null (未设置默认模板)。
    /// </summary>
    public string? DefaultTemplateId { get; set; }

    /// <summary>
    /// VariableBundle JSON - 项目级变量配置。
    /// </summary>
    public string VariablesJson { get; set; } = "{}";

    /// <summary>
    /// 项目级提示词。key → body, JSON 序列化。
    /// </summary>
    public string Prompts { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
