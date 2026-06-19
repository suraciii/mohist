namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// 项目 workflow profile (key: ProjectId, 1:1 与 project)。
/// 存储项目级默认模板引用 + 项目级变量配置。
/// </summary>
public class ProjectWorkflowProfile
{
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// 项目默认模板 ID。可为 null (未设置默认模板)。
    /// </summary>
    public string? DefaultTemplateId { get; set; }

    /// <summary>
    /// VariableBundle JSON - 项目级变量配置。
    /// </summary>
    public string Variables { get; set; } = "{}";

    /// <summary>
    /// 项目级提示词。key → body。
    /// </summary>
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 是否禁用了内置默认 issue 模板 mohist/default。
    /// </summary>
    public bool DisableDefaultIssueTemplate { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
