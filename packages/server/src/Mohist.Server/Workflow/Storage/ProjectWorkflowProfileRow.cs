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
    /// 结构: { "vars": {...}, "stages": { "plan": { "vars": {...} } } }
    /// </summary>
    public string VariablesJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
